namespace NLightning.Domain.Bitcoin.Wallet.Models;

using ValueObjects;

/// <summary>
/// One transaction of the on-chain wallet's durable history (NL-1187): the raw transaction as a block held it, where
/// it confirmed and which of its outputs and inputs belong to the wallet. Written by the chain monitor in the block's
/// own save (the same one that stores the wallet's outputs and the accounting feed), so it does not depend on the
/// accounting feed's cutover and keeps the raw transaction and block hash after bitcoind prunes the block. A reorg makes
/// the record unconfirmed (<see cref="BlockHeight"/> and <see cref="BlockHash"/> null) in the rewind's save.
/// </summary>
/// <param name="TxId">The transaction id.</param>
/// <param name="RawTransaction">The transaction's serialization.</param>
/// <param name="BlockHeight">The height of the block that holds it; null when a reorg disconnected that block.</param>
/// <param name="BlockHash">The hash of that block (internal byte order); null with <paramref name="BlockHeight"/>.</param>
/// <param name="Timestamp">The block's time.</param>
/// <param name="OurOutputs">The indexes of its outputs that pay a wallet address.</param>
/// <param name="OurInputs">Its inputs that spend a wallet output, with the value they spend.</param>
public sealed record WalletTransactionRecord(TxId TxId, byte[] RawTransaction, uint? BlockHeight, byte[]? BlockHash,
                                             DateTimeOffset Timestamp, IReadOnlyList<uint> OurOutputs,
                                             IReadOnlyList<WalletTransactionInput> OurInputs);

/// <summary>An input of a wallet transaction that spends a wallet output.</summary>
/// <param name="InputIndex">The input's position in the transaction.</param>
/// <param name="AmountSat">The value of the wallet output it spends.</param>
public readonly record struct WalletTransactionInput(uint InputIndex, long AmountSat);