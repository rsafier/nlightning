namespace NLightning.Domain.Onchain.Events;

using Crypto.ValueObjects;

/// <summary>
/// A processed block left the active chain (reorg). Raised once per disconnected block, highest first, after the
/// rows it had changed (spends, confirmations, first-seen heights) were rolled back and saved.
/// </summary>
public sealed class BlockDisconnectedEventArgs : EventArgs
{
    /// <summary>The height of the disconnected block.</summary>
    public uint Height { get; }

    /// <summary>The hash of the disconnected block, in internal byte order.</summary>
    public Hash BlockHash { get; }

    /// <summary>The height of the last block both branches share (the fork point).</summary>
    public uint ForkHeight { get; }

    public BlockDisconnectedEventArgs(uint height, Hash blockHash, uint forkHeight)
    {
        Height = height;
        BlockHash = blockHash;
        ForkHeight = forkHeight;
    }
}