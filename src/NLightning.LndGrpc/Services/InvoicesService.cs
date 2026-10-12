using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Accounting.Labels;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Invoicesrpc;
using Invoice = Lnrpc.Invoice;

/// <summary>
/// LND's <c>invoicesrpc.Invoices</c> over the node's hold invoices (NL-995) and invoice store
/// (<c>LND_GRPC_PLAN.md</c> §3 wave 2, NL-1164): <c>AddHoldInvoice</c>, <c>SettleInvoice</c>, <c>CancelInvoice</c>,
/// <c>SubscribeSingleInvoice</c> and <c>LookupInvoiceV2</c> (by payment hash); <c>HtlcModifier</c> answers
/// <c>UNIMPLEMENTED</c>. LND's <c>ACCEPTED</c> is our <c>Held</c>.
/// </summary>
public sealed partial class InvoicesService : Invoices.InvoicesBase
{
    private static readonly SourceLabels s_labels = SourceLabels.Create(LightningService.InvoiceLabel, []);

    private readonly IInvoiceService _invoiceService;
    private readonly TimeProvider _timeProvider;
    private readonly IHoldInvoiceService? _holdInvoices;
    private readonly IPaymentEventSource? _events;

    public InvoicesService(IInvoiceService invoiceService, TimeProvider? timeProvider = null,
                           IHoldInvoiceService? holdInvoices = null,
                           IPaymentEventSource? events = null)
    {
        _invoiceService = invoiceService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _holdInvoices = holdInvoices;
        _events = events;
    }

    /// <summary>
    /// <c>AddHoldInvoice</c>: a BOLT 11 hold invoice for the caller's payment hash (the node never sees the preimage
    /// until <c>SettleInvoice</c>), labelled <c>lnd-grpc</c>; <c>cltv_expiry</c> is the final CLTV delta asked for
    /// (never below the node's). Refused: <c>description_hash</c>, <c>fallback_addr</c>, route hints of the caller's.
    /// </summary>
    public override async Task<AddHoldInvoiceResp> AddHoldInvoice(AddHoldInvoiceRequest request,
                                                                  ServerCallContext context)
    {
        if (request.Hash.Length != 32)
            throw InvalidArgument("hash must be 32 bytes");
        if (!request.DescriptionHash.IsEmpty || !string.IsNullOrEmpty(request.FallbackAddr)
         || request.RouteHints.Count > 0)
            throw Unimplemented("description_hash, fallback_addr and route_hints are not supported");
        if (request.Value != 0 && request.ValueMsat != 0 && request.Value * 1000 != request.ValueMsat)
            throw InvalidArgument("value and value_msat are mutually exclusive");
        if (request.Value < 0 || request.ValueMsat < 0 || request.Expiry < 0 || request.Expiry > uint.MaxValue)
            throw InvalidArgument("value and expiry cannot be negative");
        if (request.CltvExpiry > ushort.MaxValue)
            throw InvalidArgument("cltv_expiry is out of range");

        var msat = request.ValueMsat != 0 ? (ulong)request.ValueMsat : (ulong)request.Value * 1000;
        InvoiceModel invoice;
        try
        {
            invoice = await _invoiceService.CreateHoldInvoiceAsync(
                          new Hash(request.Hash.ToByteArray()), msat == 0 ? null : LightningMoney.MilliSatoshis(msat),
                          request.Memo ?? string.Empty, request.Expiry == 0 ? null : (uint)request.Expiry,
                          request.CltvExpiry == 0 ? null : (ushort)request.CltvExpiry, s_labels,
                          context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw InvalidArgument(e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, e.Message));
        }

        invoice = await _invoiceService.GetInvoiceAsync(invoice.PaymentHash, context.CancellationToken) ?? invoice;
        return new AddHoldInvoiceResp
        {
            PaymentRequest = invoice.Bolt11 ?? string.Empty,
            AddIndex = LightningService.AddIndex(invoice),
            PaymentAddr = ByteString.CopyFrom((byte[])invoice.PaymentSecret)
        };
    }

    /// <summary><c>SettleInvoice</c>: settles the held invoice whose hash is SHA256(<c>preimage</c>).</summary>
    public override async Task<SettleInvoiceResp> SettleInvoice(SettleInvoiceMsg request, ServerCallContext context)
    {
        if (request.Preimage.Length != 32)
            throw InvalidArgument("preimage must be 32 bytes");

        var hash = new Hash(SHA256.HashData(request.Preimage.Span));
        try
        {
            await HoldInvoices().SettleHoldInvoiceAsync(hash, new Secret(request.Preimage.ToByteArray()),
                                                        context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.NotFound, e.Message));
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        return new SettleInvoiceResp();
    }

    /// <summary><c>CancelInvoice</c>: cancels an open or held invoice (a held one's HTLCs are failed back).</summary>
    public override async Task<CancelInvoiceResp> CancelInvoice(CancelInvoiceMsg request, ServerCallContext context)
    {
        if (request.PaymentHash.Length != 32)
            throw InvalidArgument("payment_hash must be 32 bytes");

        try
        {
            await HoldInvoices().CancelHoldInvoiceAsync(new Hash(request.PaymentHash.ToByteArray()),
                                                        context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.NotFound, e.Message));
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        return new CancelInvoiceResp();
    }

    /// <summary>
    /// <c>SubscribeSingleInvoice</c>: the invoice now, then every change of its state or amount paid, until it is
    /// settled or canceled (the stream then ends, as LND's). Changes are noticed on the payment event bus and by a
    /// re-read every second (cancellations raise no event).
    /// </summary>
    public override async Task SubscribeSingleInvoice(SubscribeSingleInvoiceRequest request,
                                                      IServerStreamWriter<Invoice> responseStream,
                                                      ServerCallContext context)
    {
        if (request.RHash.Length != 32)
            throw InvalidArgument("r_hash must be 32 bytes");

        var hash = new Hash(request.RHash.ToByteArray());
        using var subscription = _events?.Subscribe(64);
        var invoice = await _invoiceService.GetInvoiceAsync(hash, context.CancellationToken)
                   ?? throw new RpcException(new Status(StatusCode.NotFound, "unable to locate invoice"));
        var sent = LightningService.ToLndInvoice(invoice);
        await responseStream.WriteAsync(sent, context.CancellationToken);
        while (sent.State is not (Invoice.Types.InvoiceState.Settled or Invoice.Types.InvoiceState.Canceled))
        {
            await InvoiceStreams.WaitForChangeAsync(subscription, _timeProvider, context.CancellationToken);
            if (await _invoiceService.GetInvoiceAsync(hash, context.CancellationToken) is not { } current)
                return;

            var next = LightningService.ToLndInvoice(current);
            if (next.State == sent.State && next.AmtPaidMsat == sent.AmtPaidMsat)
                continue;

            await responseStream.WriteAsync(next, context.CancellationToken);
            sent = next;
        }
    }

    /// <summary><c>LookupInvoiceV2</c> by <c>payment_hash</c>; by <c>payment_addr</c> or AMP set id it is refused.</summary>
    public override async Task<Invoice> LookupInvoiceV2(LookupInvoiceMsg request, ServerCallContext context)
    {
        if (request.InvoiceRefCase != LookupInvoiceMsg.InvoiceRefOneofCase.PaymentHash)
            throw Unimplemented("lookups by payment_addr or set_id are not supported");
        if (request.PaymentHash.Length != 32)
            throw InvalidArgument("payment_hash must be 32 bytes");

        var invoice = await _invoiceService.GetInvoiceAsync(new Hash(request.PaymentHash.ToByteArray()),
                                                            context.CancellationToken)
                   ?? throw new RpcException(new Status(StatusCode.NotFound, "unable to locate invoice"));
        return LightningService.ToLndInvoice(invoice);
    }

    private IHoldInvoiceService HoldInvoices() =>
        _holdInvoices ?? throw Unimplemented("hold invoices are not available on this node");

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static RpcException Unimplemented(string message) => new(new Status(StatusCode.Unimplemented, message));
}

/// <summary>Waiting for invoice changes on the payment event bus with a re-read fallback.</summary>
internal static class InvoiceStreams
{
    /// <summary>The longest wait before a stream re-reads (cancellations and new invoices raise no event).</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    /// <summary>Returns at the next payment event or after <see cref="Poll"/>, whichever is first.</summary>
    public static async Task WaitForChangeAsync(IPaymentEventSubscription? subscription, TimeProvider timeProvider,
                                                CancellationToken cancellationToken)
    {
        if (subscription is null)
        {
            await Task.Delay(Poll, timeProvider, cancellationToken);
            return;
        }

        using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        poll.CancelAfter(Poll);
        try
        {
            await foreach (var _ in subscription.ReadAllAsync(poll.Token))
                return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The poll interval passed
        }
    }
}