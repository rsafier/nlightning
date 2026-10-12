namespace NLightning.Domain.Onchain.Models;

using Crypto.ValueObjects;

/// <summary>
/// A processed block of the active chain, kept for reorg detection (BOLT 5 plan O0-T3: the last
/// <c>BlockchainMonitorService.HeaderRingSize</c> blocks).
/// </summary>
/// <param name="Height">The block height.</param>
/// <param name="BlockHash">The block hash, in internal byte order.</param>
/// <param name="PreviousBlockHash">The hash of its parent, in internal byte order.</param>
public sealed record BlockHeaderModel(uint Height, Hash BlockHash, Hash PreviousBlockHash);