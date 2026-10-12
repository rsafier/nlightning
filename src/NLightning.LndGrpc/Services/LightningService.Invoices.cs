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
    /// draws a preimage unless r_preimage is supplied, and draws the payment secret), labelled <c>lnd-grpc</c>. Refused: <c>description_hash</c>, <c>fallback_addr</c>, a <c>cltv_expiry</c> above the node's
    /// (<c>Node:Routing:InvoiceMinFinalCltvExpiry</c>), AMP and blinded invoices. <c>private</c> is accepted: the node
    /// adds route hints when its channels need them (<c>Node:Invoices:RouteHints</c>).
    /// </summary>
    public override async Task<AddInvoiceResponse> AddInvoice(Invoice request, ServerCallContext context)
    {
        if (!request.RPreimage.IsEmpty && request.RPreimage.Length != 32)
            throw InvalidArgument("r_preimage must be 32 bytes");
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
            var amount = msat == 0 ? null : LightningMoney.MilliSatoshis(msat);
            var expiry = request.Expiry == 0 ? (uint?)null : (uint)request.Expiry;
            invoice = request.RPreimage.IsEmpty
                ? await _invoiceService.CreateInvoiceAsync(amount, request.Memo ?? string.Empty, expiry,
                    s_invoiceLabels, context.CancellationToken)
                : await _invoiceService.CreateInvoiceAsync(amount, request.Memo ?? string.Empty, expiry,
                    s_invoiceLabels, new Secret(request.RPreimage.ToByteArray()), context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw InvalidArgument(e.Message);
        }

        // The add_index is assigned by the save (NL-1165): read the row back for it
        invoice = await _invoiceService.GetInvoiceAsync(invoice.PaymentHash, context.CancellationToken) ?? invoice;
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
    /// <c>ListInvoices</c> with LND's paging: <c>index_offset</c> is an <c>add_index</c> (dense, NL-1165);
    /// forward the invoices after it oldest first, <c>reversed</c> the ones before it (0: from the newest), answered
    /// oldest first either way; <c>pending_only</c>, <c>creation_date_start</c>/<c>end</c> (inclusive Unix seconds).
    /// </summary>
    public override async Task<ListInvoiceResponse> ListInvoices(ListInvoiceRequest request, ServerCallContext context)
    {
        var query = PageQuery(request.IndexOffset, request.Reversed, request.NumMaxInvoices, DefaultInvoicePage,
                              request.CreationDateStart, request.CreationDateEnd);
        await using var scope = CreateScope();
        var invoices = await UnitOfWork(scope).InvoiceDbRepository.ListByIndexAsync(query, request.PendingOnly);
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

    /// <summary>LND's <c>add_index</c> of our invoice (dense, NL-1165); 0 for a row saved without one.</summary>
    internal static ulong AddIndex(InvoiceModel invoice) => invoice.AddIndex ?? 0;

    /// <summary>LND's <c>settle_index</c> (dense, NL-1165); 0 until settled.</summary>
    internal static ulong SettleIndex(InvoiceModel invoice) => invoice.SettleIndex ?? 0;

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
        var lndInvoice = new Invoice
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
        lndInvoice.Htlcs.Add(invoice.Htlcs.Select(h => new InvoiceHTLC
        {
            ChanId = ToChanId(h.ShortChannelId),
            HtlcIndex = h.HtlcId,
            AmtMsat = h.AmountMsat,
            AcceptHeight = (int)h.AcceptHeight,
            AcceptTime = h.AcceptTime.ToUnixTimeSeconds(),
            ResolveTime = h.ResolveTime?.ToUnixTimeSeconds() ?? 0,
            ExpiryHeight = (int)h.ExpiryHeight,
            State = h.State switch
            {
                InvoiceHtlcState.Settled => InvoiceHTLCState.Settled,
                InvoiceHtlcState.Canceled => InvoiceHTLCState.Canceled,
                _ => InvoiceHTLCState.Accepted
            },
            MppTotalAmtMsat = h.MppTotalMsat
        }));
        return lndInvoice;
    }

    /// <summary>
    /// The page of an LND index-offset listing: forward the rows after the offset lowest index first, reversed the rows
    /// before it highest first (0: from the last), within the inclusive creation dates (Unix seconds).
    /// </summary>
    internal static LndIndexQuery PageQuery(ulong indexOffset, bool reversed, ulong max, ulong defaultMax,
                                            ulong creationDateStart, ulong creationDateEnd)
    {
        ulong? after = null, before = null;
        if (indexOffset != 0)
        {
            if (reversed)
                before = indexOffset;
            else
                after = indexOffset;
        }

        var take = (int)Math.Min(max == 0 ? defaultMax : max, MaxPage);
        return new LndIndexQuery(after, before, !reversed, take,
                                 creationDateStart != 0 ? FromUnixSeconds(creationDateStart) : null,
                                 creationDateEnd != 0 ? FromUnixSeconds(creationDateEnd).AddSeconds(1).AddTicks(-1)
                                                      : null);
    }

    private static DateTimeOffset FromUnixSeconds(ulong seconds) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(seconds, 253_402_300_798UL));
}