using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for ChainStatus (ClientCommand 16, NL-216): whether chain processing is halted, why, the last processed
/// block, bitcoind's tip (null when it could not be asked) and the operations refused while halted.
/// </summary>
[MessagePackObject]
public sealed class ChainStatusIpcResponse
{
    [Key(0)] public bool IsChainProcessingHalted { get; init; }
    [Key(1)] public string? HaltReason { get; init; }
    [Key(2)] public uint LastProcessedBlockHeight { get; init; }
    [Key(3)] public uint? ChainTipHeight { get; init; }
    [Key(4)] public required List<string> RefusedOperations { get; init; }

    public static ChainStatusIpcResponse FromClientResponse(ChainStatusClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ChainStatusIpcResponse
        {
            IsChainProcessingHalted = clientResponse.IsChainProcessingHalted,
            HaltReason = clientResponse.HaltReason,
            LastProcessedBlockHeight = clientResponse.LastProcessedBlockHeight,
            ChainTipHeight = clientResponse.ChainTipHeight,
            RefusedOperations = clientResponse.RefusedOperations.ToList()
        };
    }
}