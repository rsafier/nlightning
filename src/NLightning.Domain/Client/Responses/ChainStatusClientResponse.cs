namespace NLightning.Domain.Client.Responses;

/// <summary>
/// How the node follows the chain (<c>ClientCommand.ChainStatus</c>, NL-216).
/// </summary>
/// <param name="IsChainProcessingHalted">True while block processing is halted (a block that keeps failing, or a reorg
/// deeper than the header ring): nothing on chain is seen and the operations in <paramref name="RefusedOperations"/>
/// are refused.</param>
/// <param name="HaltReason">Why it halted, while it is halted.</param>
/// <param name="LastProcessedBlockHeight">The last block the node processed.</param>
/// <param name="ChainTipHeight">bitcoind's tip, or null when bitcoind could not be asked.</param>
/// <param name="RefusedOperations">What the node refuses while halted (empty when it is not).</param>
/// <param name="Notifications">How new blocks are learned (<c>Bitcoin:Notifications</c>, NL-1094): "zmq" or
/// "poll every &lt;interval&gt;"; null when unknown.</param>
/// <param name="MempoolWatch">How the mempool is watched (BOLT 5 O8): "zmq rawtx", "gettxspendingprevout poll" or
/// "off"; null when unknown.</param>
public sealed record ChainStatusClientResponse(
    bool IsChainProcessingHalted,
    string? HaltReason,
    uint LastProcessedBlockHeight,
    uint? ChainTipHeight,
    IReadOnlyList<string> RefusedOperations,
    string? Notifications = null,
    string? MempoolWatch = null);