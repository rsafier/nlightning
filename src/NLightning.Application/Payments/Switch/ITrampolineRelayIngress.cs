namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments.Events;
using Onion;

/// <summary>
/// Where <see cref="HtlcSwitch"/> hands an incoming HTLC that it classified as a part of a trampoline relay (BOLTs PR
/// 836, NL-875): the relay engine collects the parts of the payment, pays the next trampoline node and resolves every
/// part (decision D-TR5).
/// </summary>
/// <remarks>
/// <para>The switch calls it under the incoming HTLC's lock, after storing the HTLC's outer shared secret
/// (<c>IChannelOperations.RecordOnionSecretAsync</c>), for every lock-in it processes: the first one and its replays
/// (startup, link-up), until the HTLC is removed. An implementation must therefore be idempotent per incoming HTLC
/// (<see cref="IncomingHtlcLockedIn.ChannelId"/>, <c>Htlc.Id</c>). It is resolved from the container when an HTLC needs
/// it (so its registration creates no construction cycle with the switch); when none is registered the switch fails
/// the HTLC back with <c>temporary_trampoline_failure</c> created with both secrets (inside a blinded trampoline route,
/// as the blinding rules say).</para>
/// <para>Failures the engine sends for a part are created with both secrets
/// (<see cref="TrampolineErrorPackets"/>); <c>attribution_data</c> stays on the outer layer.</para>
/// </remarks>
public interface ITrampolineRelayIngress
{
    /// <summary>
    /// A locked-in incoming HTLC that is a part of a trampoline relay.
    /// </summary>
    /// <param name="lockedIn">The lock-in event (the incoming channel and HTLC).</param>
    /// <param name="onion">The classified onion: both secrets, both payloads, the next trampoline packet and, inside a
    /// blinded route, the decrypted recipient data.</param>
    /// <param name="cancellationToken">Cancels the handling (the event is derived again by the next replay).</param>
    Task HandleNewPartAsync(IncomingHtlcLockedIn lockedIn, IncomingOnionTrampolineRelay onion,
                            CancellationToken cancellationToken);
}