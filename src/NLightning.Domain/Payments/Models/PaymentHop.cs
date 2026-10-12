namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// One hop of the route an outgoing payment's onion was built for, first hop (our peer) first and the payee last.
/// </summary>
/// <remarks>
/// It is persisted with the <see cref="PaymentModel"/> (ONION M4-T7) because the origin needs every hop's Sphinx
/// <see cref="SharedSecret"/> to decrypt a returned error onion (<c>IFailureOnionService.DecryptErrorPacket</c>) after a
/// restart, and the route length and hop ids to interpret it (<c>FailureInterpreter</c>).
/// </remarks>
/// <param name="NodeId">The node of this hop.</param>
/// <param name="ShortChannelId">The channel that reaches <paramref name="NodeId"/> (from the previous hop, or from us
/// for the first hop).</param>
/// <param name="Amount">The amount of the HTLC <paramref name="NodeId"/> receives (<c>amt_to_forward</c> of the
/// previous hop).</param>
/// <param name="CltvExpiry">The <c>cltv_expiry</c> of the HTLC <paramref name="NodeId"/> receives.</param>
/// <param name="SharedSecret">The Sphinx shared secret of this hop (from the onion construction).</param>
/// <param name="HoldTime">How long this hop reported holding the HTLC, from a verified <c>attribution_data</c> of the
/// payment's fulfill or failure (BOLT 4, reported in units of 100 ms; zero means "no timing information"), or null
/// when none was verified for this hop.</param>
public sealed record PaymentHop(
    CompactPubKey NodeId,
    ShortChannelId ShortChannelId,
    LightningMoney Amount,
    uint CltvExpiry,
    Secret SharedSecret,
    TimeSpan? HoldTime = null);