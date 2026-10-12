namespace NLightning.Domain.Payments.Trampoline;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// One incoming HTLC of a trampoline relay (NL-875): a part of the incoming MPP set of the relay with
/// <see cref="PaymentHash"/>. Keyed by the incoming (<see cref="ChannelId"/>, <see cref="HtlcId"/>); immutable once
/// stored.
/// </summary>
/// <param name="PaymentHash">The relay's payment hash (the same on every part and on the outgoing payment).</param>
/// <param name="ChannelId">The channel the HTLC came in on.</param>
/// <param name="HtlcId">The HTLC's id (offered by the peer).</param>
/// <param name="Amount">The HTLC's amount.</param>
/// <param name="CltvExpiry">The HTLC's <c>cltv_expiry</c>.</param>
/// <param name="OuterSharedSecret">The shared secret of the outer onion (the key of the error returned upstream).</param>
/// <param name="TrampolineSharedSecret">The shared secret of the trampoline onion this node peeled (the inner error
/// layer of a trampoline failure).</param>
/// <param name="OuterPaymentSecret">The <c>payment_secret</c> of the outer onion's final payload, when it had one.</param>
public sealed record TrampolineRelayPartModel(
    Hash PaymentHash,
    ChannelId ChannelId,
    ulong HtlcId,
    LightningMoney Amount,
    uint CltvExpiry,
    Secret OuterSharedSecret,
    Secret TrampolineSharedSecret,
    byte[]? OuterPaymentSecret);