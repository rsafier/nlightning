using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Channels.ValueObjects;

/// <summary>
/// The channel must be failed: BOLT 1/2 "send an <c>error</c> and fail the channel".
/// </summary>
/// <remarks>
/// Contract for the code that catches it (BOLT2 plan N6-T3, D10):
/// <list type="number">
///   <item>Persist <c>ChannelState.Failed</c> and the <c>error</c> that will be sent (so it can be re-sent on
///   reconnect, B2-RE-05) <b>before</b> sending anything.</item>
///   <item>Send an <c>error</c> whose <c>channel_id</c> is <see cref="FailedChannelId"/> and whose data is
///   <see cref="ChannelErrorException.PeerMessage"/>. That is never null or blank here (it defaults to
///   <see cref="DefaultPeerMessage"/>), so the send path, which falls back to <see cref="Exception.Message"/> for a
///   blank peer message, never leaks the local message.</item>
///   <item>Refuse every later update on the channel, in both directions.</item>
///   <item>When <see cref="MustBroadcast"/> is true, broadcast the latest local commitment through the single
///   fail-the-channel service (N9-T4, <c>IChannelFailureService</c>).</item>
/// </list>
/// Unlike <see cref="ChannelWarningException"/>, the connection itself may stay open; the peer's other channels are
/// unaffected.
/// <para>Do not throw it before that catcher exists: it derives from <see cref="ChannelErrorException"/>, so today
/// the generic channel-error path (PeerManager/ChannelManager) catches it, sends the error and disconnects
/// <b>without</b> persisting <c>ChannelState.Failed</c>. The N6-T3 handler must add a specific
/// <c>catch (ChannelFailedException)</c> before every generic <c>ChannelErrorException</c> handler.</para>
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class ChannelFailedException : ChannelErrorException
{
    /// <summary>
    /// The channel to fail (never null, unlike <see cref="ChannelErrorException.ChannelId"/>).
    /// </summary>
    public ChannelId FailedChannelId { get; }

    /// <summary>
    /// True when our latest local commitment must be broadcast (for example the peer sent
    /// <c>next_commitment_number == 0</c> in <c>channel_reestablish</c>, or an offered HTLC passed its deadline).
    /// Never set when the peer proved we lost data: then we must not broadcast at all (I12).
    /// </summary>
    public bool MustBroadcast { get; init; }

    /// <summary>
    /// The <c>B2-*</c> row of the BOLT 2 plan's traceability matrix that required the failure, when there is one.
    /// </summary>
    public string? RequirementId { get; init; }

    /// <summary>
    /// The <c>error</c> data sent when the caller gives no (or a blank) peer message.
    /// </summary>
    public const string DefaultPeerMessage = "channel failed";

    public ChannelFailedException(ChannelId channelId, string message, string? peerMessage = null)
        : base(message, channelId, PeerMessageOrDefault(peerMessage))
    {
        FailedChannelId = channelId;
    }

    public ChannelFailedException(ChannelId channelId, string message, Exception innerException,
                                  string? peerMessage = null)
        : base(message, channelId, innerException, PeerMessageOrDefault(peerMessage))
    {
        FailedChannelId = channelId;
    }

    private static string PeerMessageOrDefault(string? peerMessage) =>
        string.IsNullOrWhiteSpace(peerMessage) ? DefaultPeerMessage : peerMessage;
}