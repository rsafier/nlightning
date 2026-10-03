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
public sealed record ChainStatusClientResponse(
    bool IsChainProcessingHalted,
    string? HaltReason,
    uint LastProcessedBlockHeight,
    uint? ChainTipHeight,
    IReadOnlyList<string> RefusedOperations);