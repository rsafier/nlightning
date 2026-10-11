namespace NLightning.Domain.Channels.ValueObjects;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// The MuSig2 session a simple taproot channel's verification nonce signed for the broadcast of our commitment
/// (NL-1376). The nonce is derived, never stored: it is the same for every process that signs local commitment
/// <see cref="CommitmentNumber"/> in the shachain context of <see cref="NonceFundingTxId"/>, so it must sign one
/// session only (two partial signatures with one nonce over different messages or aggregate nonces reveal the funding
/// key). The same session again gives the same partial signature and is safe.
/// </summary>
/// <param name="NonceFundingTxId">The funding txid the nonce is bound to; null for the txid-free context of a v1
/// open's commitment 0.</param>
/// <param name="CommitmentNumber">The local commitment number signed for broadcast.</param>
/// <param name="SessionDigest">SHA-256 of the session's message (the BIP 341 sighash) and aggregate nonce: what
/// decides the partial signature for a given nonce and key.</param>
public readonly record struct ChannelBroadcastSession(TxId? NonceFundingTxId, ulong CommitmentNumber,
                                                      Hash SessionDigest);