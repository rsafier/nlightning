namespace NLightning.Application.Gossip.Graph;

using Domain.Bitcoin.ValueObjects;
using Domain.Gossip.Graph;

/// <summary>An immutable live graph change, including its funding point before a removed edge is forgotten.</summary>
public sealed record GraphChange(
    GraphChannel? Channel = null,
    GraphNode? Node = null,
    GraphPolicy? Policy = null,
    TxId? FundingTxId = null,
    bool Removed = false,
    uint ClosedHeight = 0);