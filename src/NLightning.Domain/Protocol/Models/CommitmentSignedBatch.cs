namespace NLightning.Domain.Protocol.Models;

using Channels.ValueObjects;
using Messages;

/// <summary>
/// A group of <c>commitment_signed</c> messages announced by one <c>start_batch</c> (BOLT 2 "Batching channel
/// messages"; splicing plan D15, SP-OP-03..06): one per active funding of the channel while splices are pending.
/// </summary>
/// <remarks>
/// Built by the per-peer inbound loop (<c>PeerManager</c>, lane SP1-A-T3) once all <c>batch_size</c> messages of the
/// same channel arrived (a message for another channel is a warning and close, SP-OP-04), and handed to
/// <c>IChannelManager.HandleCommitmentSignedBatchAsync</c>, which processes it under one acquisition of the channel's
/// lock and answers with a single <c>revoke_and_ack</c> (SP-OP-07). The batch's own rules (every CS carries
/// <c>funding_txid</c>, one per active funding, obsolete ones ignored when no splice is pending, SP-OP-05/06) are the
/// engine's (SP1-B-T2).
/// </remarks>
/// <param name="ChannelId">The channel of the <c>start_batch</c> and of every message.</param>
/// <param name="Messages">The batched messages, in arrival order.</param>
public sealed record CommitmentSignedBatch(ChannelId ChannelId, IReadOnlyList<CommitmentSignedMessage> Messages);