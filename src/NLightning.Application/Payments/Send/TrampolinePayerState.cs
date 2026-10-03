namespace NLightning.Application.Payments.Send;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Onion.Models;
using Trampoline;

/// <summary>
/// The trampoline route and budget of one of our payments sent through a trampoline node (NL-875, payer side): the
/// recipient, the policy the current attempt pays, and the attempt's trampoline onion, outer secret and total. Mutated
/// only under the payment hash's lock.
/// </summary>
/// <remarks>
/// An attempt is one trampoline onion shared by every part offered for it (the parts of a split and the parts re-sent
/// after an outer failure), with one random outer <c>payment_secret</c> and <c>total_msat</c>, so the trampoline node
/// collects them as one set. A trampoline-layer failure that asks for another budget
/// (<c>trampoline_fee_or_expiry_insufficient</c>, <c>temporary_trampoline_failure</c>) ends the attempt: once no part
/// of it is in flight, the next round starts a new attempt (<see cref="NeedsNewAttempt"/>) with a new onion, secret and
/// total.
/// </remarks>
internal sealed class TrampolinePayerState
{
    public TrampolinePayerState(CompactPubKey trampolineNode, TrampolineRecipient recipient, TrampolinePolicy policy,
                                int firstAttempt)
    {
        TrampolineNode = trampolineNode;
        Recipient = recipient;
        Policy = policy;
        Attempt = firstAttempt - 1;
    }

    /// <summary>The trampoline node the payment goes through.</summary>
    public CompactPubKey TrampolineNode { get; }

    /// <summary>Who receives the payment behind the trampoline node.</summary>
    public TrampolineRecipient Recipient { get; }

    /// <summary>The policy the current (or next) attempt pays the trampoline node.</summary>
    public TrampolinePolicy Policy { get; set; }

    /// <summary>The attempt number of <see cref="Onion"/> (persisted in <c>PaymentTrampolineHops</c>).</summary>
    public int Attempt { get; set; }

    /// <summary>The current attempt's trampoline hops; null before the first attempt.</summary>
    public IReadOnlyList<TrampolineHop>? InnerHops { get; set; }

    /// <summary>The current attempt's trampoline onion; null until its first round is planned.</summary>
    public TrampolineOnion? Onion { get; set; }

    /// <summary>The current attempt's outer <c>total_msat</c>: what the trampoline node receives, all parts together.
    /// </summary>
    public LightningMoney? OuterTotal { get; set; }

    /// <summary>The current attempt's outer <c>outgoing_cltv_value</c> at the trampoline node.</summary>
    public uint OuterFinalCltv { get; set; }

    /// <summary>A failure ended the current attempt: the next round, once no part is in flight, starts another.</summary>
    public bool NeedsNewAttempt { get; set; } = true;

    /// <summary>The one retry with a doubled budget after <c>temporary_trampoline_failure</c> was used.</summary>
    public bool TemporaryRetryUsed { get; set; }

    /// <summary>Attempts started after a <c>trampoline_fee_or_expiry_insufficient</c>.</summary>
    public int PolicyRetries { get; set; }
}

/// <summary>
/// A trampoline node's fee and CLTV delta (BOLTs PR 836 <c>trampoline_fee_or_expiry_insufficient</c> data).
/// </summary>
/// <param name="FeeBaseMsat"><c>fee_base_msat</c>.</param>
/// <param name="FeeProportionalMillionths"><c>fee_proportional_millionths</c>.</param>
/// <param name="CltvExpiryDelta"><c>cltv_expiry_delta</c>.</param>
public sealed record TrampolinePolicy(uint FeeBaseMsat, uint FeeProportionalMillionths, ushort CltvExpiryDelta)
{
    /// <summary>The fee to forward <paramref name="amountMsat"/>: base + amount x proportional / 1,000,000 (rounded
    /// up).</summary>
    public ulong FeeMsat(ulong amountMsat) =>
        checked(FeeBaseMsat + (ulong)(((UInt128)amountMsat * FeeProportionalMillionths + 999_999) / 1_000_000));
}

/// <summary>Who a payment through a trampoline node pays (NL-875).</summary>
internal abstract record TrampolineRecipient(LightningMoney Amount);

/// <summary>A BOLT 11 recipient that supports trampoline: inner route [trampoline, payee].</summary>
/// <param name="Amount">What the payee receives.</param>
/// <param name="PayeeNodeId">The payee.</param>
/// <param name="PaymentSecret">The invoice's <c>payment_secret</c> (never sent in the outer onion).</param>
/// <param name="MinFinalCltvExpiryDelta">The invoice's <c>c</c>.</param>
/// <param name="PaymentMetadata">The invoice's <c>payment_metadata</c>, if any.</param>
internal sealed record Bolt11TrampolineRecipient(
    LightningMoney Amount,
    CompactPubKey PayeeNodeId,
    Secret PaymentSecret,
    ushort MinFinalCltvExpiryDelta,
    ReadOnlyMemory<byte>? PaymentMetadata) : TrampolineRecipient(Amount);

/// <summary>A BOLT 12 recipient that supports trampoline: the hops of <paramref name="Path"/> are trampoline hops after
/// the trampoline node.</summary>
internal sealed record BlindedTrampolineRecipient(LightningMoney Amount, BlindedPaymentPath Path)
    : TrampolineRecipient(Amount);

/// <summary>A BOLT 12 recipient without trampoline support: the trampoline node gets the invoice's blinded paths
/// (<c>recipient_blinded_paths</c>) and features (<c>recipient_features</c>).</summary>
internal sealed record BlindedPathsTrampolineRecipient(
    LightningMoney Amount,
    IReadOnlyList<WireBlindedPaymentPath> Paths,
    FeatureSet? Features) : TrampolineRecipient(Amount);