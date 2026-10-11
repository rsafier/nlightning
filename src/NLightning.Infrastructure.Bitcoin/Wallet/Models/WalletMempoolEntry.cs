namespace NLightning.Infrastructure.Bitcoin.Wallet.Models;

/// <summary>Core's current ancestor package, including this transaction; fees are satoshis, sizes virtual bytes.</summary>
public sealed record WalletMempoolEntry(long FeeSat, long VirtualSize, long AncestorFeeSat, long AncestorVirtualSize,
                                       int AncestorCount);