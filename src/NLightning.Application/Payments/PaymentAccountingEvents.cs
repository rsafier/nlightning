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
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
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
            AmountMsat = checked((long)circuit.ActualFee.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Details = ForwardDetails(circuit, incoming, outgoing)
        };
    }

    /// <summary>The detail <c>kind</c> of an <see cref="AccountingEventKind.InterceptedHtlcSettled"/> (NL-1182).</summary>
    public const string InterceptedKind = "intercepted";

    /// <summary>
    /// A forward held for the HTLC interceptor was settled by it with the preimage (NL-1182): the incoming HTLC was
    /// fulfilled without an outgoing leg, so its whole amount is ours. Keyed by the incoming HTLC
    /// (<see cref="AccountingEventKeys.InterceptedHtlcSettled"/>); the details keep what the onion asked us to forward.
    /// </summary>
    /// <param name="incomingChannelId">The incoming channel.</param>
    /// <param name="incomingHtlcId">The incoming HTLC.</param>
    /// <param name="paymentHash">Its payment hash.</param>
    /// <param name="amount">Its amount (what we received).</param>
    /// <param name="incoming">The incoming channel, when loaded.</param>
    /// <param name="outgoingScid">The channel the onion named for the forward, if it named one.</param>
    /// <param name="nextNodeId">The next node a blinded route named instead, if any.</param>
    /// <param name="amountToForward">What the onion asked us to forward.</param>
    /// <param name="occurredAt">When the fulfill was staged.</param>
    /// <param name="blockHeight">The current height, when known (0 = unknown).</param>
    public static AccountingEventModel InterceptedHtlcSettled(ChannelId incomingChannelId, ulong incomingHtlcId,
                                                              Hash paymentHash, LightningMoney amount,
                                                              ChannelModel? incoming, ShortChannelId? outgoingScid,
                                                              CompactPubKey? nextNodeId, LightningMoney amountToForward,
                                                              DateTimeOffset occurredAt, uint blockHeight)
    {
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, InterceptedKind),
            ("incomingChannelId", incomingChannelId.ToString()),
            ("incomingHtlcId", incomingHtlcId.ToString(CultureInfo.InvariantCulture)),
            (AccountingDetailKeys.IncomingScid, ScidOf(incoming)?.ToString()),
            ("incomingAmountMsat", Msat(amount)),
            (AccountingDetailKeys.OutgoingScid, outgoingScid?.ToString()),
            ("nextNodeId", nextNodeId?.ToString()),
            ("amountToForwardMsat", Msat(amountToForward)),
            ("settledBy", "interceptor")
        ]);

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.InterceptedHtlcSettled(incomingChannelId, incomingHtlcId),
            Kind = AccountingEventKind.InterceptedHtlcSettled,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight > 0 ? blockHeight : null,
            ChannelId = incomingChannelId,
            ShortChannelId = ScidOf(incoming),
            PaymentHash = paymentHash,
            Counterparty = incoming?.RemoteNodeId,
            AmountMsat = checked((long)amount.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Details = details
        };
    }

    /// <summary>Stages an interceptor income fact atomically with a durable circuit transition; errors abort the save.</summary>
    public static async Task StageInterceptedHtlcSettledStrictAsync(IUnitOfWork unitOfWork, ChannelId incomingChannelId,
        ulong incomingHtlcId, Func<AccountingEventModel> build, CancellationToken cancellationToken = default)
    {
        var events = unitOfWork.AccountingEventDbRepository;
        if (await events.ExistsAsync(AccountingEventKeys.InterceptedHtlcSettled(incomingChannelId, incomingHtlcId),
                                     cancellationToken))
            return;
        events.Add(build());
    }

    /// <summary>
    /// Stages the <see cref="InterceptedHtlcSettled"/> event <paramref name="build"/> returns on
    /// <paramref name="unitOfWork"/>, for the switch to call in the save of the interceptor's fulfill: once per incoming
    /// HTLC (nothing when the key is in the feed already). Never throws (a failure is logged and the fulfill is saved
    /// without its event).
    /// </summary>
    public static async Task StageInterceptedHtlcSettledAsync(IUnitOfWork unitOfWork, ChannelId incomingChannelId,
                                                              ulong incomingHtlcId, Func<AccountingEventModel> build,
                                                              ILogger logger,
                                                              CancellationToken cancellationToken = default)
    {
        try
        {
            var events = unitOfWork.AccountingEventDbRepository;
            if (await events.ExistsAsync(AccountingEventKeys.InterceptedHtlcSettled(incomingChannelId, incomingHtlcId),
                                         cancellationToken))
                return;

            events.Add(build());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the accounting event of intercepted HTLC {HtlcId} of channel "
                             + "{ChannelId}; the fulfill is saved without it", incomingHtlcId, incomingChannelId);
        }
    }

    /// <summary>
    /// An HTLC settled by the interceptor (<see cref="AccountingEventKind.InterceptedHtlcSettled"/>) that we then lost on
    /// chain (NL-1182, as NL-608 for a forward): the fulfill never got through and the peer took the HTLC output by its
    /// timeout, or we gave it up, or it had no output (trimmed). A <see cref="AccountingEventKind.ForwardLostOnchain"/>
    /// with the <see cref="UpstreamOnchainCause"/>, keyed by the incoming HTLC
    /// (<see cref="AccountingEventKeys.ForwardLostOnchain"/>), so a reorg reverses it as a forward's.
    /// </summary>
    /// <param name="key">The event key (the generation of <see cref="AccountingEventKeys.ForwardLostOnchain"/>).</param>
    /// <param name="settled">The standing <see cref="AccountingEventKind.InterceptedHtlcSettled"/>.</param>
    /// <param name="incoming">The incoming channel.</param>
    /// <param name="closeTxId">The commitment the HTLC output belongs to.</param>
    /// <param name="spenderTxId">The peer's transaction that took it; null when we gave it up or it was trimmed.</param>
    /// <param name="occurredAt">When the resolution was recorded.</param>
    /// <param name="blockHeight">The block of the spend (or of the round that gave it up, or of the close).</param>
    /// <param name="trimmed">The HTLC had no output on the commitment that confirmed (below dust, NL-760).</param>
    public static AccountingEventModel InterceptedHtlcLostOnchain(string key, AccountingEventModel settled,
                                                                  ChannelModel? incoming, TxId closeTxId,
                                                                  TxId? spenderTxId, DateTimeOffset occurredAt,
                                                                  uint blockHeight, bool trimmed = false)
    {
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, InterceptedKind),
            ("incomingChannelId", settled.Details.GetValueOrDefault("incomingChannelId")),
            ("incomingHtlcId", settled.Details.GetValueOrDefault("incomingHtlcId")),
            (AccountingDetailKeys.IncomingScid, ScidOf(incoming)?.ToString()),
            ("incomingAmountMsat", settled.AmountMsat.ToString(CultureInfo.InvariantCulture)),
            ("settledKey", settled.EventKey),
            ("cause", UpstreamOnchainCause),
            (AccountingDetailKeys.CloseTxId, closeTxId.ToString()),
            ("spenderTxId", spenderTxId?.ToString()),
            (TrimmedDetail, trimmed ? "true" : null),
            (AccountingDetailKeys.Reason,
             trimmed
                 ? "An HTLC settled by the interceptor was trimmed (below dust) on the commitment that confirmed"
                 : spenderTxId is null
                     ? "An HTLC settled by the interceptor was given up on chain"
                     : "An HTLC settled by the interceptor was taken back by the peer on chain (its timeout)")
        ]);

        return new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.ForwardLostOnchain,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight,
            ChannelId = settled.ChannelId,
            ShortChannelId = ScidOf(incoming) ?? settled.ShortChannelId,
            PaymentHash = settled.PaymentHash,
            Counterparty = incoming?.RemoteNodeId ?? settled.Counterparty,
            AmountMsat = -settled.AmountMsat,
            FeeMsat = 0,
            Finality = AccountingFinality.Confirmed,
            Details = details
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

    /// <summary>The detail <c>cause</c> of a <see cref="AccountingEventKind.ForwardLostOnchain"/> whose upstream HTLC we
    /// lost on chain after the forward was settled (NL-608).</summary>
    public const string UpstreamOnchainCause = "upstreamOnchain";

    /// <summary>
    /// A forward booked as settled (<see cref="ForwardSettled"/>) whose incoming HTLC we then lost on chain (NL-608): the
    /// upstream fulfill was refused (link down, channel on chain) and the peer took the HTLC output by its timeout, or we
    /// gave it up. We paid downstream and lost the incoming amount.
    /// </summary>
    /// <param name="key">The event key (the generation of <see cref="AccountingEventKeys.ForwardLostOnchain"/>).</param>
    /// <param name="circuit">The forward's circuit, <c>Fulfilled</c>.</param>
    /// <param name="incoming">The incoming channel.</param>
    /// <param name="closeTxId">The commitment the HTLC output belongs to.</param>
    /// <param name="spenderTxId">The peer's transaction that took it; null when we gave it up.</param>
    /// <param name="occurredAt">When the resolution was recorded.</param>
    /// <param name="blockHeight">The block of the spend (or of the round that gave it up, or of the close).</param>
    /// <param name="trimmed">The HTLC had no output on the commitment that confirmed (below dust, NL-760): lost with
    /// the close.</param>
    public static AccountingEventModel ForwardUpstreamLostOnchain(string key, ForwardCircuitModel circuit,
                                                                  ChannelModel? incoming, TxId closeTxId,
                                                                  TxId? spenderTxId, DateTimeOffset occurredAt,
                                                                  uint blockHeight, bool trimmed = false)
    {
        var details = AccountingDetailsCodec.Create(
        [
            .. ForwardDetails(circuit, incoming, null).Select(p => (p.Key, (string?)p.Value)),
            ("cause", UpstreamOnchainCause),
            (AccountingDetailKeys.CloseTxId, closeTxId.ToString()),
            ("spenderTxId", spenderTxId?.ToString()),
            (TrimmedDetail, trimmed ? "true" : null),
            (AccountingDetailKeys.Reason,
             trimmed
                 ? "The incoming HTLC of a settled forward was trimmed (below dust) on the commitment that confirmed"
                 : spenderTxId is null
                     ? "The incoming HTLC of a settled forward was given up on chain"
                     : "The incoming HTLC of a settled forward was taken by the peer on chain")
        ]);

        return new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.ForwardLostOnchain,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight,
            ChannelId = circuit.IncomingChannelId,
            ShortChannelId = ScidOf(incoming),
            PaymentHash = circuit.PaymentHash,
            Counterparty = incoming?.RemoteNodeId,
            AmountMsat = -checked((long)circuit.ActualIncomingAmount.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Confirmed,
            Details = details
        };
    }

    /// <summary>The detail of a <see cref="AccountingEventKind.ForwardLostOnchain"/> or
    /// <see cref="AccountingEventKind.InvoiceLostOnchain"/> whose incoming HTLC had no output on the commitment that
    /// confirmed (NL-760): <c>true</c>.</summary>
    public const string TrimmedDetail = "trimmed";

    /// <summary>The detail <c>cause</c> of an <see cref="AccountingEventKind.InvoiceLostOnchain"/> (NL-688).</summary>
    public const string InvoiceOnchainCause = "invoiceOnchain";

    /// <summary>
    /// One of our invoices booked as settled (<see cref="InvoiceSettled"/>) whose incoming HTLC we then lost on chain
    /// (NL-688): the peer took the HTLC output by its timeout before our claim with the preimage confirmed, or we gave it
    /// up. The payer holds the preimage (the invoice is paid), but the HTLC's amount never reached us.
    /// </summary>
    /// <param name="key">The event key (the generation of <see cref="AccountingEventKeys.InvoiceLostOnchain"/>).</param>
    /// <param name="invoice">The invoice, <c>Settled</c>.</param>
    /// <param name="htlcId">The incoming HTLC's id.</param>
    /// <param name="htlcAmountMsat">The incoming HTLC's amount (the part of the settled set that is lost).</param>
    /// <param name="channel">The channel the HTLC came in on.</param>
    /// <param name="closeTxId">The commitment the HTLC output belongs to.</param>
    /// <param name="spenderTxId">The peer's transaction that took it; null when we gave it up.</param>
    /// <param name="occurredAt">When the resolution was recorded.</param>
    /// <param name="blockHeight">The block of the spend (or of the round that gave it up, or of the close).</param>
    /// <param name="trimmed">The HTLC had no output on the commitment that confirmed (below dust, NL-760): lost with
    /// the close.</param>
    public static AccountingEventModel InvoiceLostOnchain(string key, InvoiceModel invoice, ulong htlcId,
                                                          ulong htlcAmountMsat, ChannelModel channel, TxId closeTxId,
                                                          TxId? spenderTxId, DateTimeOffset occurredAt,
                                                          uint blockHeight, bool trimmed = false)
    {
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, KindName(invoice.Kind)),
            (AccountingDetailKeys.Description,
             string.IsNullOrEmpty(invoice.Description) ? null : invoice.Description),
            ("cause", InvoiceOnchainCause),
            ("htlcId", htlcId.ToString(CultureInfo.InvariantCulture)),
            (AccountingDetailKeys.CloseTxId, closeTxId.ToString()),
            ("spenderTxId", spenderTxId?.ToString()),
            ("settledKey", AccountingEventKeys.InvoiceSettled(invoice.PaymentHash)),
            (TrimmedDetail, trimmed ? "true" : null),
            (AccountingDetailKeys.Reason,
             trimmed
                 ? "An incoming HTLC of a settled invoice was trimmed (below dust) on the commitment that confirmed"
                 : spenderTxId is null
                     ? "An incoming HTLC of a settled invoice was given up on chain"
                     : "An incoming HTLC of a settled invoice was taken back by the peer on chain (its timeout)"),
            .. SourceLabels.FromStored(invoice.Label, invoice.Tags).ToDetailPairs()
        ]);

        return new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.InvoiceLostOnchain,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight,
            ChannelId = channel.ChannelId,
            ShortChannelId = ScidOf(channel),
            PaymentHash = invoice.PaymentHash,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = -checked((long)htlcAmountMsat),
            FeeMsat = 0,
            Finality = AccountingFinality.Confirmed,
            Details = details
        };
    }

    /// <summary>The detail <c>kind</c> of a trampoline relay's events (NL-875).</summary>
    public const string TrampolineKind = "trampoline";

    /// <summary>
    /// The detail of a <c>TrampolineRelaySettled</c> event with the incoming amount per channel (NL-899):
    /// <c>channelId:msat</c> pairs joined by commas, one per channel in the order of <c>incomingChannelIds</c>. Events
    /// sealed before it have none.
    /// </summary>
    public const string TrampolineIncomingAmountsDetail = "incomingAmountsMsat";

    /// <summary>The <see cref="TrampolineIncomingAmountsDetail"/> value of a relay's parts.</summary>
    internal static string FormatIncomingAmounts(IReadOnlyList<TrampolineRelayPartModel> parts)
    {
        var sums = new List<(string Channel, long Msat)>();
        foreach (var part in parts)
        {
            var channel = part.ChannelId.ToString();
            var index = sums.FindIndex(s => s.Channel == channel);
            var msat = checked((long)part.Amount.MilliSatoshi);
            if (index < 0)
                sums.Add((channel, msat));
            else
                sums[index] = (channel, checked(sums[index].Msat + msat));
        }

        return string.Join(',', sums.Select(s => $"{s.Channel}:{s.Msat.ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <summary>
    /// A trampoline payment we relayed settled (NL-875): every incoming part of <paramref name="relay"/> was fulfilled and
    /// its outgoing payment succeeded. <c>AmountMsat</c> is the channels' net change: the sum of the incoming parts minus
    /// what the outgoing payment took (its amount and the routing fees we paid), from <paramref name="outgoingPayment"/>
    /// when given, else from the relay's <c>FeeEarned</c>. The outgoing payment books no event of its own.
    /// </summary>
    /// <param name="relay">The relay, already <see cref="TrampolineRelayStatus.Fulfilled"/>.</param>
    /// <param name="parts">Its incoming parts.</param>
    /// <param name="outgoingPayment">Its outgoing payment (<c>IsTrampolineRelay</c>), when known.</param>
    /// <param name="incoming">The channel of the first incoming part, when loaded.</param>
    /// <param name="blockHeight">The current height, when known (0 = unknown).</param>
    /// <exception cref="InvalidOperationException">The relay is not fulfilled, or has no part.</exception>
    public static AccountingEventModel TrampolineRelaySettled(TrampolineRelayModel relay,
                                                              IReadOnlyList<TrampolineRelayPartModel> parts,
                                                              PaymentModel? outgoingPayment, ChannelModel? incoming,
                                                              uint blockHeight = 0)
    {
        if (relay is not { Status: TrampolineRelayStatus.Fulfilled, CompletedAt: { } completedAt })
            throw new InvalidOperationException("The trampoline relay is not fulfilled.");
        if (parts.Count == 0)
            throw new InvalidOperationException("The trampoline relay has no incoming part.");

        var incomingMsat = 0L;
        foreach (var part in parts)
            incomingMsat = checked(incomingMsat + (long)part.Amount.MilliSatoshi);

        var outgoingMsat = outgoingPayment is not null
                               ? checked((long)outgoingPayment.TotalAmount.MilliSatoshi)
                               : checked(incomingMsat - (long)(relay.FeeEarned?.MilliSatoshi ?? 0));
        var first = parts[0];
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, TrampolineKind),
            ("parts", parts.Count.ToString(CultureInfo.InvariantCulture)),
            ("incomingChannelId", first.ChannelId.ToString()),
            ("incomingChannelIds", string.Join(',', parts.Select(p => p.ChannelId.ToString()).Distinct())),
            // NL-899: what each incoming channel brought, so the channels report splits the income among them
            (TrampolineIncomingAmountsDetail, FormatIncomingAmounts(parts)),
            (AccountingDetailKeys.IncomingScid, ScidOf(incoming)?.ToString()),
            ("incomingAmountMsat", incomingMsat.ToString(CultureInfo.InvariantCulture)),
            ("amountOutMsat", Msat(relay.AmountOut)),
            ("outgoingAmountMsat", outgoingMsat.ToString(CultureInfo.InvariantCulture)),
            ("routingFeePaidMsat", outgoingPayment is null ? null : Msat(outgoingPayment.Fee)),
            ("outgoingChannelId", outgoingPayment?.OutgoingChannelId?.ToString()),
            ("nextNodeId", relay.NextNodeId?.ToString()),
            ("blindedRecipient", relay.RecipientBlindedPaths is null ? null : AccountingDetailKeys.True)
        ]);

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.TrampolineRelaySettled(relay.PaymentHash),
            Kind = AccountingEventKind.TrampolineRelaySettled,
            OccurredAt = completedAt,
            BlockHeight = blockHeight > 0 ? blockHeight : null,
            ChannelId = first.ChannelId,
            ShortChannelId = ScidOf(incoming),
            PaymentHash = relay.PaymentHash,
            Counterparty = incoming?.RemoteNodeId,
            AmountMsat = checked(incomingMsat - outgoingMsat),
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Details = details
        };
    }

    /// <summary>
    /// Stages the <c>TrampolineRelaySettled</c> event of <paramref name="relay"/> on <paramref name="unitOfWork"/>, its
    /// parts and outgoing payment read from it, for the relay engine to call in the save that marks the relay
    /// <c>Fulfilled</c>. Nothing when the relay is not fulfilled or the event is already in the feed. Never throws (a
    /// failure is logged and the relay is saved without its event).
    /// </summary>
    public static async Task StageTrampolineRelaySettledAsync(IUnitOfWork unitOfWork, TrampolineRelayModel relay,
                                                              ChannelModel? incoming, uint blockHeight, ILogger logger,
                                                              CancellationToken cancellationToken = default)
    {
        try
        {
            if (relay.Status != TrampolineRelayStatus.Fulfilled)
                return;

            var events = unitOfWork.AccountingEventDbRepository;
            if (await events.ExistsAsync(AccountingEventKeys.TrampolineRelaySettled(relay.PaymentHash),
                                         cancellationToken))
                return;

            var parts = await unitOfWork.TrampolineRelayDbRepository.GetPartsAsync(relay.PaymentHash);
            var payment = await unitOfWork.PaymentDbRepository.GetByPaymentHashAsync(relay.PaymentHash);
            events.Add(TrampolineRelaySettled(relay, parts,
                                              payment is { IsTrampolineRelay: true, Status: PaymentStatus.Succeeded }
                                                  ? payment
                                                  : null, incoming, blockHeight));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the accounting event of trampoline relay {PaymentHash}; the relay is "
                             + "saved without it", relay.PaymentHash);
        }
    }

    /// <summary>
    /// An incoming part of a trampoline relay booked as settled (<see cref="AccountingEventKind.TrampolineRelaySettled"/>)
    /// that we then lost on chain (NL-875, as NL-608 for a forward): the upstream fulfill never got through and the peer
    /// took the HTLC output by its timeout, or we gave it up. The part's amount is lost. A
    /// <see cref="AccountingEventKind.ForwardLostOnchain"/> with the <see cref="UpstreamOnchainCause"/>, keyed by the
    /// incoming HTLC (<see cref="AccountingEventKeys.ForwardLostOnchain"/>), so a reorg reverses it as a forward's.
    /// </summary>
    /// <param name="key">The event key (the generation of <see cref="AccountingEventKeys.ForwardLostOnchain"/>).</param>
    /// <param name="part">The lost part.</param>
    /// <param name="incoming">The part's channel.</param>
    /// <param name="closeTxId">The commitment the HTLC output belongs to.</param>
    /// <param name="spenderTxId">The peer's transaction that took it; null when we gave it up.</param>
    /// <param name="occurredAt">When the resolution was recorded.</param>
    /// <param name="blockHeight">The block of the spend (or of the round that gave it up).</param>
    public static AccountingEventModel TrampolinePartLostOnchain(string key, TrampolineRelayPartModel part,
                                                                 ChannelModel? incoming, TxId closeTxId,
                                                                 TxId? spenderTxId, DateTimeOffset occurredAt,
                                                                 uint blockHeight)
    {
        var details = AccountingDetailsCodec.Create(
        [
            (AccountingDetailKeys.Kind, TrampolineKind),
            ("incomingChannelId", part.ChannelId.ToString()),
            ("incomingHtlcId", part.HtlcId.ToString(CultureInfo.InvariantCulture)),
            (AccountingDetailKeys.IncomingScid, ScidOf(incoming)?.ToString()),
            ("incomingAmountMsat", Msat(part.Amount)),
            ("settledKey", AccountingEventKeys.TrampolineRelaySettled(part.PaymentHash)),
            ("cause", UpstreamOnchainCause),
            (AccountingDetailKeys.CloseTxId, closeTxId.ToString()),
            ("spenderTxId", spenderTxId?.ToString()),
            (AccountingDetailKeys.Reason,
             spenderTxId is null
                 ? "An incoming HTLC of a settled trampoline relay was given up on chain"
                 : "An incoming HTLC of a settled trampoline relay was taken by the peer on chain")
        ]);

        return new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.ForwardLostOnchain,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight,
            ChannelId = part.ChannelId,
            ShortChannelId = ScidOf(incoming),
            PaymentHash = part.PaymentHash,
            Counterparty = incoming?.RemoteNodeId,
            AmountMsat = -checked((long)part.Amount.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Confirmed,
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
            ("incomingAmountMsat", Msat(circuit.ActualIncomingAmount)),
            ("interpretedIncomingAmountMsat", circuit.IncomingAmount != circuit.ActualIncomingAmount ? Msat(circuit.IncomingAmount) : null),
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