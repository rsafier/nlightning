namespace NLightning.Application.Tests.Channels.Harness;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;

/// <summary>
/// The in-process network a <see cref="SwitchNode"/> publishes into: <see cref="ThreeNodeHarness"/> and the trampoline
/// proofs' four-node harness (<c>Payments/Trampoline/Harness/TrampolineHarness</c>) deliver what it routes through
/// per-direction FIFOs.
/// </summary>
internal interface ISwitchNodeNetwork
{
    /// <summary>
    /// Queues <paramref name="message"/> from <paramref name="from"/> for <paramref name="to"/>, or records it in
    /// <see cref="SwitchNode.Dropped"/> when the peer is away or stopped.
    /// </summary>
    void Route(SwitchNode from, CompactPubKey to, IChannelMessage message);
}