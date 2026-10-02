using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Labels;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// The accounting events of the payment core (NL-602 A1-T2, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §4): invoices
/// settled, payments that succeeded or failed for good, forwards settled or lost on chain.
/// </summary>
/// <remarks>
/// The writers call <see cref="TryStage"/> inside the guard of the transition they record, on the unit of work that
/// saves it: the row commits with the fact or not at all. Building or staging a row never throws out of the core path
/// (a failure is logged and the fact is saved without its row).
/// </remarks>
internal static class PaymentAccountingEvents
{
    /// <summary>The detail of the BOLT 12 offer id (hex), on received and on sent payments (NL-645): what a
    /// classification rule on an offer matches.</summary>
    public const string OfferIdDetail = ClassificationEngine.OfferIdDetail;

    /// <summary>
    /// Stages the event <paramref name="build"/> returns on <paramref name="unitOfWork"/>; a failure is logged, never
    /// thrown.
    /// </summary>
    public static void TryStage(IUnitOfWork unitOfWork, Func<AccountingEventModel> build, ILogger logger)
    {
        try
        {
            unitOfWork.AccountingEventDbRepository.Add(build());
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not record an accounting event; the state change is saved without it");
        }
    }

    /// <summary>
    /// One of our invoices was settled: <paramref name="received"/> (the sum of the set's HTLCs) is ours.
    /// </summary>
    /// <param name="invoice">The invoice, already <c>Settled</c>.</param>
    /// <param name="received">What the HTLC set carried.</param>
    /// <param name="channelId">The channel of the part that settled the invoice (null: unknown, the backfill).</param>
    /// <param name="channel">That channel, when loaded.</param>
    /// <param name="parts">How many HTLCs the set had.</param>
    /// <param name="claimedOnchain">The settling part's channel is closing on chain: its amount is claimed there.</param>
    /// <param name="blockHeight">The current height, when known (0 = unknown).</param>
    /// <param name="selfPayment">We paid the invoice ourselves (the incoming side of a circular rebalance, NL-609):
    /// the books take it as no income (<c>selfPayment</c> detail).</param>
    public static AccountingEventModel InvoiceSettled(InvoiceModel invoice, LightningMoney received,
                                                      ChannelId? channelId, ChannelModel? channel, int parts,
                                                      bool claimedOnchain, uint blockHeight, bool selfPayment = false)
    {
        var requested = invoice.Amount is { } amount && amount != received ? Msat(amount) : null;
        var customRecords = invoice.Keysend?.CustomRecords;
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, KindName(invoice.Kind)),
            (AccountingDetailKeys.Description,
             string.IsNullOrEmpty(invoice.Description) ? null : invoice.Description),
            ("requestedMsat", requested),
            ("parts", parts.ToString(CultureInfo.InvariantCulture)),
            ("settledBy", claimedOnchain ? "onchainClaim" : "fulfill"),
            (OfferIdDetail, invoice.Bolt12?.OfferId.ToString()),
            ("payerNote", invoice.Bolt12?.PayerNote),
            ("quantity", invoice.Bolt12?.Quantity?.ToString(CultureInfo.InvariantCulture)),
            ("customRecords", customRecords is { Count: > 0 } records
                                  ? string.Join(',', records.Select(r => r.Type.ToString(CultureInfo.InvariantCulture)))
                                  : null),
            (AccountingDetailKeys.SelfPayment, selfPayment ? AccountingDetailKeys.True : null),
            // A3-T1: the operator's label and tags (a BOLT 12 invoice got its offer's when it was issued)
            .. SourceLabels.FromStored(invoice.Label, invoice.Tags).ToDetailPairs()
        ]);

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.InvoiceSettled(invoice.PaymentHash),
            Kind = AccountingEventKind.InvoiceSettled,
            OccurredAt = invoice.SettledAt ?? throw new InvalidOperationException("The invoice is not settled."),
            BlockHeight = blockHeight > 0 ? blockHeight : null,
            ChannelId = channelId,
            ShortChannelId = ScidOf(channel),
            PaymentHash = invoice.PaymentHash,
            Counterparty = channel?.RemoteNodeId,
            AmountMsat = checked((long)received.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Details = details
        };
    }

    /// <summary>
    /// One of our payments succeeded: its amount and route fee left our channels.
    /// </summary>
    /// <param name="payment">The payment, already <c>Succeeded</c>.</param>
    /// <param name="parts">How many HTLCs the payee settled (at least 1).</param>
    /// <param name="selfPayment">The payment paid an invoice of ours (a rebalance).</param>
    /// <param name="description">The paid invoice's description, when known.</param>
    public static AccountingEventModel PaymentSucceeded(PaymentModel payment, int parts, bool selfPayment,
                                                        string? description)
    {
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, PaymentKindName(payment)),
            (AccountingDetailKeys.Description, string.IsNullOrEmpty(description) ? null : description),
            ("parts", Math.Max(1, parts).ToString(CultureInfo.InvariantCulture)),
            (AccountingDetailKeys.SelfPayment, selfPayment ? AccountingDetailKeys.True : null),
            ("offer", payment.Bolt12?.Offer),
            (OfferIdDetail, OfferIdOf(payment.Bolt12?.Offer)),
            ("payerNote", payment.Bolt12?.PayerNote),
            ("customRecords", payment.Keysend?.CustomRecords is { Count: > 0 } records
                                  ? string.Join(',', records.Select(r => r.Type.ToString(CultureInfo.InvariantCulture)))
                                  : null),
            .. SourceLabels.FromStored(payment.Label, payment.Tags).ToDetailPairs()
        ]);

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.PaymentSucceeded(payment.PaymentHash),
            Kind = AccountingEventKind.PaymentSucceeded,
            OccurredAt = payment.CompletedAt ?? throw new InvalidOperationException("The payment is not completed."),
            ChannelId = payment.OutgoingChannelId,
            PaymentHash = payment.PaymentHash,
            Counterparty = payment.PayeeNodeId,
            AmountMsat = -checked((long)payment.TotalAmount.MilliSatoshi),
            FeeMsat = checked((long)payment.Fee.MilliSatoshi),
            Finality = AccountingFinality.Final,
            Details = details
        };
    }

    /// <summary>
    /// One of our payments failed for good (informational: no money moved).
    /// </summary>
    /// <param name="payment">The payment, already <c>Failed</c>.</param>
    public static AccountingEventModel PaymentFailed(PaymentModel payment)
    {
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, PaymentKindName(payment)),
            (AccountingDetailKeys.Reason, payment.FailureReason),
            ("failureCode", payment.FailureCode?.ToString()),
            ("failureSourceIndex", payment.FailureSourceIndex?.ToString(CultureInfo.InvariantCulture)),
            ("amountMsat", Msat(payment.Amount)),
            ("offer", payment.Bolt12?.Offer),
            (OfferIdDetail, OfferIdOf(payment.Bolt12?.Offer)),
            .. SourceLabels.FromStored(payment.Label, payment.Tags).ToDetailPairs()
        ]);

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.PaymentFailed(payment.PaymentHash, payment.CreatedAt.UtcTicks),
            Kind = AccountingEventKind.PaymentFailed,
            OccurredAt = payment.CompletedAt ?? throw new InvalidOperationException("The payment is not completed."),
            ChannelId = payment.OutgoingChannelId,
            PaymentHash = payment.PaymentHash,
            Counterparty = payment.PayeeNodeId,
            AmountMsat = 0,
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Details = details
        };
    }

    /// <summary>
    /// A forward we carried settled: the fee (incoming minus outgoing) is ours.
    /// </summary>
    /// <param name="circuit">The circuit, already <c>Fulfilled</c>.</param>
    /// <param name="incoming">The incoming channel, when loaded.</param>
    /// <param name="outgoing">The outgoing channel, when loaded.</param>
    public static AccountingEventModel ForwardSettled(ForwardCircuitModel circuit, ChannelModel? incoming,
                                                      ChannelModel? outgoing)
    {
        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ForwardSettled(circuit.IncomingChannelId, circuit.IncomingHtlcId),
            Kind = AccountingEventKind.ForwardSettled,
            OccurredAt = circuit.ResolvedAt ?? throw new InvalidOperationException("The circuit is not resolved."),
            ChannelId = circuit.IncomingChannelId,
            ShortChannelId = ScidOf(incoming),
            PaymentHash = circuit.PaymentHash,
            Counterparty = incoming?.RemoteNodeId,
            AmountMsat = checked((long)circuit.Fee.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Details = ForwardDetails(circuit, incoming, outgoing)
        };
    }

    /// <summary>
    /// A forward we paid downstream (the preimage was revealed on chain) whose upstream HTLC was already failed: the
    /// outgoing amount was paid without reimbursement.
    /// </summary>
    /// <param name="circuit">The forward's circuit.</param>
    /// <param name="outgoingChannelId">The channel whose HTLC was claimed with the preimage.</param>
    /// <param name="outgoingHtlcId">That HTLC.</param>
    /// <param name="incoming">The incoming channel, when loaded.</param>
    /// <param name="outgoing">The outgoing channel, when loaded.</param>
    /// <param name="occurredAt">When the late preimage was seen.</param>
    /// <param name="blockHeight">The current height, when known (0 = unknown).</param>
    public static AccountingEventModel ForwardLostOnchain(ForwardCircuitModel circuit, ChannelId outgoingChannelId,
                                                          ulong outgoingHtlcId, ChannelModel? incoming,
                                                          ChannelModel? outgoing, DateTimeOffset occurredAt,
                                                          uint blockHeight)
    {
        var details = AccountingDetailsCodec.Create(
        [
            .. ForwardDetails(circuit, incoming, outgoing).Select(p => (p.Key, (string?)p.Value)),
            ("outgoingChannelId", outgoingChannelId.ToString()),
            ("outgoingHtlcId", outgoingHtlcId.ToString(CultureInfo.InvariantCulture)),
            (AccountingDetailKeys.Reason,
             "The preimage of the outgoing HTLC was revealed on chain after the upstream HTLC was failed")
        ]);

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ForwardLostOnchain(circuit.IncomingChannelId, circuit.IncomingHtlcId),
            Kind = AccountingEventKind.ForwardLostOnchain,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight > 0 ? blockHeight : null,
            ChannelId = outgoingChannelId,
            ShortChannelId = ScidOf(outgoing),
            PaymentHash = circuit.PaymentHash,
            Counterparty = outgoing?.RemoteNodeId,
            AmountMsat = -checked((long)circuit.OutgoingAmount.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Details = details
        };
    }

    private static IReadOnlyDictionary<string, string> ForwardDetails(ForwardCircuitModel circuit,
                                                                      ChannelModel? incoming, ChannelModel? outgoing)
    {
        return AccountingDetailsCodec.Create(
            ("incomingChannelId", circuit.IncomingChannelId.ToString()),
            ("incomingHtlcId", circuit.IncomingHtlcId.ToString(CultureInfo.InvariantCulture)),
            (AccountingDetailKeys.IncomingScid, ScidOf(incoming)?.ToString()),
            ("incomingAmountMsat", Msat(circuit.IncomingAmount)),
            ("outgoingChannelId", circuit.OutgoingChannelId?.ToString()),
            ("outgoingHtlcId", circuit.OutgoingHtlcId?.ToString(CultureInfo.InvariantCulture)),
            (AccountingDetailKeys.OutgoingScid, (ScidOf(outgoing) ?? circuit.OutgoingShortChannelId).ToString()),
            ("outgoingAmountMsat", Msat(circuit.OutgoingAmount)));
    }

    /// <summary>
    /// The offer id (hex) of the offer string <paramref name="offer"/> (<c>lno1...</c>), as <c>OfferService</c> computes
    /// ours: the SHA-256 of the offer's TLV bytes. Null when there is no offer or it does not decode (NL-645).
    /// </summary>
    internal static string? OfferIdOf(string? offer)
    {
        if (string.IsNullOrEmpty(offer)
         || !Bolt12Bech32.TryDecode(offer, out var hrp, out var data, out _)
         || !string.Equals(hrp, Bolt12Constants.OfferHrp, StringComparison.Ordinal))
            return null;

        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    private static string KindName(InvoiceKind kind) => kind switch
    {
        InvoiceKind.Bolt12 => "bolt12",
        InvoiceKind.Keysend => "keysend",
        _ => "bolt11"
    };

    private static string PaymentKindName(PaymentModel payment) =>
        payment.Bolt12 is not null ? "bolt12"
        : payment.Keysend is not null ? "keysend"
        : payment.Bolt11 is not null ? "bolt11"
        : "blinded";

    private static ShortChannelId? ScidOf(ChannelModel? channel) =>
        channel is not null && channel.ShortChannelId != default ? channel.ShortChannelId : (ShortChannelId?)null;

    private static string Msat(LightningMoney amount) => amount.MilliSatoshi.ToString(CultureInfo.InvariantCulture);
}