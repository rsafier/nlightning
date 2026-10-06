using Google.Protobuf;
using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Accounting.Labels;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Lnrpc;
using Mapping;
using Invoice = Lnrpc.Invoice;

public sealed partial class LightningService
{
    /// <summary>LND's default page of <c>ListInvoices</c>.</summary>
    private const ulong DefaultInvoicePage = 100;

    /// <summary>Our largest page of <c>ListInvoices</c>/<c>ListPayments</c> (LND has none).</summary>
    private const ulong MaxPage = 10_000;

    private static readonly SourceLabels s_invoiceLabels = SourceLabels.Create(InvoiceLabel, []);

    /// <summary>
    /// <c>AddInvoice</c>: a BOLT 11 invoice from <see cref="Domain.Payments.Interfaces.IInvoiceService"/> (the node
    /// draws the preimage and the payment secret), labelled <c>lnd-grpc</c>. Refused: a caller preimage (hold invoices
    /// are wave 2), <c>description_hash</c>, <c>fallback_addr</c>, a <c>cltv_expiry</c> above the node's
    /// (<c>Node:Routing:InvoiceMinFinalCltvExpiry</c>), AMP and blinded invoices. <c>private</c> is accepted: the node
    /// adds route hints when its channels need them (<c>Node:Invoices:RouteHints</c>).
    /// </summary>
    public override async Task<AddInvoiceResponse> AddInvoice(Invoice request, ServerCallContext context)
    {
        if (!request.RPreimage.IsEmpty)
            throw Unimplemented("r_preimage: this node draws the preimage of its invoices");
        if (!request.DescriptionHash.IsEmpty)
            throw Unimplemented("description_hash invoices are not supported");
        if (!string.IsNullOrEmpty(request.FallbackAddr))
            throw Unimplemented("fallback_addr is not supported");
        if (request.IsAmp)
            throw Unimplemented("AMP invoices are not supported");
        if (request.IsBlinded)
            throw Unimplemented("blinded invoices are not supported through this API");
        if (request.CltvExpiry > _nodeOptions.Routing.InvoiceMinFinalCltvExpiry)
            throw InvalidArgument($"cltv_expiry {request.CltvExpiry} is above this node's final CLTV delta "
                                + $"{_nodeOptions.Routing.InvoiceMinFinalCltvExpiry}");
        if (request.Value < 0 || request.ValueMsat < 0)
            throw InvalidArgument("the invoice amount cannot be negative");
        if (request.Value != 0 && request.ValueMsat != 0 && request.Value * 1000 != request.ValueMsat)
            throw InvalidArgument("value and value_msat are mutually exclusive");
        if (request.Expiry < 0 || request.Expiry > uint.MaxValue)
            throw InvalidArgument("expiry is out of range");

        var msat = request.ValueMsat != 0 ? (ulong)request.ValueMsat : (ulong)request.Value * 1000;
        InvoiceModel invoice;
        try
        {
            invoice = await _invoiceService.CreateInvoiceAsync(msat == 0 ? null : LightningMoney.MilliSatoshis(msat),
                                                               request.Memo ?? string.Empty,
                                                               request.Expiry == 0 ? null : (uint)request.Expiry,
                                                               s_invoiceLabels, context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw InvalidArgument(e.Message);
        }

        return new AddInvoiceResponse
        {
            RHash = ByteString.CopyFrom((byte[])invoice.PaymentHash),
            PaymentRequest = invoice.Bolt11 ?? string.Empty,
            AddIndex = AddIndex(invoice),
            PaymentAddr = ByteString.CopyFrom((byte[])invoice.PaymentSecret)
        };
    }

    /// <summary><c>LookupInvoice</c> by <c>r_hash</c> (bytes) or <c>r_hash_str</c> (hex).</summary>
    public override async Task<Invoice> LookupInvoice(PaymentHash request, ServerCallContext context)
    {
        byte[] hash;
        try
        {
            hash = request.RHash.IsEmpty ? Convert.FromHexString(request.RHashStr) : request.RHash.ToByteArray();
        }
        catch (FormatException)
        {
            throw InvalidArgument("r_hash_str must be hex");
        }

        if (hash.Length != 32)
            throw InvalidArgument("the payment hash must be 32 bytes");

        var invoice = await _invoiceService.GetInvoiceAsync(new Hash(hash), context.CancellationToken)
                   ?? throw NotFound("unable to locate invoice");
        return ToLndInvoice(invoice);
    }

    /// <summary>
    /// <c>ListInvoices</c> with LND's paging: <c>index_offset</c> is an <c>add_index</c> (our creation time in ticks);
    /// forward the invoices after it oldest first, <c>reversed</c> the ones before it (0: from the newest), answered
    /// oldest first either way; <c>pending_only</c>, <c>creation_date_start</c>/<c>end</c> (inclusive Unix seconds).
    /// </summary>
    public override async Task<ListInvoiceResponse> ListInvoices(ListInvoiceRequest request, ServerCallContext context)
    {
        var query = PageQuery(request.IndexOffset, request.Reversed, request.NumMaxInvoices, DefaultInvoicePage,
                              request.CreationDateStart, request.CreationDateEnd);
        await using var scope = CreateScope();
        var invoices = await UnitOfWork(scope).InvoiceDbRepository.ListByCreationAsync(query, request.PendingOnly);
        var ordered = request.Reversed ? invoices.Reverse() : invoices;
        var response = new ListInvoiceResponse();
        response.Invoices.Add(ordered.Select(ToLndInvoice));
        if (response.Invoices.Count > 0)
        {
            response.FirstIndexOffset = response.Invoices[0].AddIndex;
            response.LastIndexOffset = response.Invoices[^1].AddIndex;
        }

        return response;
    }

    /// <summary>
    /// <c>DecodePayReq</c>: a BOLT 11 invoice for this node's network. BOLT 12 strings and invoices of other networks
    /// are <c>INVALID_ARGUMENT</c>; bLIP 39 blinded paths are not mapped.
    /// </summary>
    public override Task<PayReq> DecodePayReq(PayReqString request, ServerCallContext context)
    {
        Bolt11.Models.Invoice invoice;
        try
        {
            invoice = Bolt11.Models.Invoice.Decode(request.PayReq?.Trim(), _nodeOptions.BitcoinNetwork);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw InvalidArgument($"invalid payment request: {e.Message}");
        }

        var msat = invoice.Amount.MilliSatoshi;
        var response = new PayReq
        {
            Destination = invoice.PayeePubKey?.ToHex() ?? string.Empty,
            PaymentHash = invoice.PaymentHash?.ToString() ?? string.Empty,
            NumSatoshis = (long)(msat / 1000),
            NumMsat = (long)msat,
            Timestamp = invoice.Timestamp,
            Expiry = (long)(invoice.ExpiryDate - DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp)).TotalSeconds,
            Description = invoice.Description ?? string.Empty,
            DescriptionHash = invoice.DescriptionHash?.ToString() ?? string.Empty,
            FallbackAddr = invoice.FallbackAddresses?.FirstOrDefault()?.ToString() ?? string.Empty,
            CltvExpiry = invoice.MinFinalCltvExpiry,
            PaymentAddr = invoice.PaymentSecret is { } secret
                              ? ByteString.CopyFrom(Convert.FromHexString(secret.ToString()))
                              : ByteString.Empty
        };
        foreach (var hint in invoice.RouteHints)
        {
            var routeHint = new RouteHint();
            routeHint.HopHints.Add(hint.Select(h => new HopHint
            {
                NodeId = h.CompactPubKey.ToString(),
                ChanId = ToChanId(h.ShortChannelId),
                FeeBaseMsat = h.FeeBaseMsat,
                FeeProportionalMillionths = h.FeeProportionalMillionths,
                CltvExpiryDelta = h.CltvExpiryDelta
            }));
            response.RouteHints.Add(routeHint);
        }

        response.Features.Add(LndFeatures.ToMap(invoice.Features).ToDictionary(p => p.Key, p => p.Value));
        return Task.FromResult(response);
    }

    /// <summary>LND's <c>add_index</c> of our invoice: its creation time in .NET ticks (unique, increasing, sparse).</summary>
    internal static ulong AddIndex(InvoiceModel invoice) => (ulong)invoice.CreatedAt.UtcTicks;

    /// <summary>LND's <c>settle_index</c>: the settle time in ticks, 0 until settled.</summary>
    internal static ulong SettleIndex(InvoiceModel invoice) =>
        invoice.SettledAt is { } settled ? (ulong)settled.UtcTicks : 0;

    internal static Invoice ToLndInvoice(InvoiceModel invoice)
    {
        var msat = invoice.Amount?.MilliSatoshi ?? 0;
        var paid = invoice.AmountReceived?.MilliSatoshi ?? 0;
        var state = invoice.Status switch
        {
            InvoiceStatus.Settled => Invoice.Types.InvoiceState.Settled,
            InvoiceStatus.Canceled => Invoice.Types.InvoiceState.Canceled,
            InvoiceStatus.Accepted or InvoiceStatus.Held => Invoice.Types.InvoiceState.Accepted,
            _ => Invoice.Types.InvoiceState.Open
        };
        return new Invoice
        {
            Memo = invoice.Description ?? string.Empty,
            RPreimage = invoice.Preimage is { } preimage ? ByteString.CopyFrom((byte[])preimage) : ByteString.Empty,
            RHash = ByteString.CopyFrom((byte[])invoice.PaymentHash),
            Value = (long)(msat / 1000),
            ValueMsat = (long)msat,
            Settled = state == Invoice.Types.InvoiceState.Settled,
            CreationDate = invoice.CreatedAt.ToUnixTimeSeconds(),
            SettleDate = invoice.SettledAt?.ToUnixTimeSeconds() ?? 0,
            PaymentRequest = invoice.Bolt11 ?? string.Empty,
            Expiry = invoice.ExpirySeconds,
            CltvExpiry = invoice.MinFinalCltvExpiry,
            AddIndex = AddIndex(invoice),
            SettleIndex = SettleIndex(invoice),
            AmtPaid = (long)paid,
            AmtPaidSat = (long)(paid / 1000),
            AmtPaidMsat = (long)paid,
            State = state,
            IsKeysend = invoice.Kind == InvoiceKind.Keysend,
            PaymentAddr = ByteString.CopyFrom((byte[])invoice.PaymentSecret)
        };
    }

    /// <summary>
    /// The creation-time page of an LND index-offset listing: forward after the offset oldest first, reversed before it
    /// newest first, within the inclusive creation dates.
    /// </summary>
    internal static CreationRangeQuery PageQuery(ulong indexOffset, bool reversed, ulong max, ulong defaultMax,
                                                 ulong creationDateStart, ulong creationDateEnd)
    {
        DateTimeOffset? after = null, before = null;
        if (indexOffset != 0)
        {
            var offset = FromTicks(indexOffset);
            if (reversed)
                before = offset;
            else
                after = offset;
        }

        if (creationDateStart != 0)
        {
            var start = FromUnixSeconds(creationDateStart).AddTicks(-1);
            after = after is { } a && a > start ? a : start;
        }

        if (creationDateEnd != 0)
        {
            var end = FromUnixSeconds(creationDateEnd).AddSeconds(1);
            before = before is { } b && b < end ? b : end;
        }

        var take = (int)Math.Min(max == 0 ? defaultMax : max, MaxPage);
        return new CreationRangeQuery(after, before, !reversed, take);
    }

    private static DateTimeOffset FromTicks(ulong ticks) =>
        new(checked((long)Math.Min(ticks, (ulong)DateTimeOffset.MaxValue.UtcTicks)), TimeSpan.Zero);

    private static DateTimeOffset FromUnixSeconds(ulong seconds) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(seconds, 253_402_300_798UL));
}