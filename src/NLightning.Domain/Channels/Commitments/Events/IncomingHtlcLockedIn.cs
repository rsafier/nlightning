namespace NLightning.Domain.Channels.Commitments.Events;

using Enums;
using ValueObjects;

/// <summary>
/// An HTLC the peer offered is irrevocably committed (<see cref="HtlcState.RcvdAddAckRevocation"/>: in both
/// commitments, both previous commitments revoked). Only now may it be fulfilled, failed or forwarded (BOLT 2
/// "irrevocably committed", B2-NO-03, B2-FWD-01).
/// </summary>
/// <remarks>The engine raises it exactly once, on the <c>revoke_and_ack</c> that locks the HTLC in. It stays pending
/// (re-derived on startup) until we send the removal.</remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="Htlc">The HTLC as recorded at lock-in (amount, hash, expiry, onion, <c>path_key</c>).</param>
public sealed record IncomingHtlcLockedIn(ChannelId ChannelId, HtlcRecord Htlc) : IChannelDomainEvent
{
    public ulong HtlcId => Htlc.Id;
}