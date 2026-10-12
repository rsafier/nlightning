namespace NLightning.Domain.Onchain.Models;

using Channels.Enums;
using Channels.ValueObjects;

/// <summary>
/// A durable live-feed checkpoint for an HTLC outcome, independent of a commitment or chain confirmation.
/// Reorgs never delete it: operational resolver replay must not become new passive activity. This is neither an
/// outbox nor historical event payload storage; a crash between commit and fanout can lose a live notification.
/// </summary>
public sealed record OnchainHtlcObservationModel(
    ChannelId ChannelId, HtlcDirection Direction, ulong HtlcId, bool Settled, DateTimeOffset ObservedAt);