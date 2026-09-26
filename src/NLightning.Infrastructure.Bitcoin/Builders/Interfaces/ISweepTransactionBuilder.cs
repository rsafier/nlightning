namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Models;

/// <summary>
/// Builds sweep and claim transactions (BOLT 5 plan O3-T1, O4-T1/T2): any number of channel outputs of known scripts
/// into one wallet output.
/// </summary>
public interface ISweepTransactionBuilder
{
    /// <summary>
    /// Builds the unsigned transaction paying the inputs' total minus the fee for its estimated weight (73-byte
    /// signatures) at <paramref name="feeratePerKw"/>.
    /// </summary>
    /// <param name="inputs">The outputs to spend, in input order.</param>
    /// <param name="destinationScript">The wallet scriptPubKey that receives the funds.</param>
    /// <param name="feeratePerKw">The feerate in sat per 1000 weight units.</param>
    /// <param name="lockTime">A minimum <c>nLockTime</c> (block height); raised to the largest
    /// <c>cltv_expiry</c> of the timeout claims.</param>
    /// <exception cref="ArgumentException">An input lacks what its spend kind needs, or the output would be below the
    /// destination's dust limit after the fee.</exception>
    UnsignedSweepTransaction Build(IReadOnlyList<SweepInput> inputs, byte[] destinationScript, uint feeratePerKw,
                                   uint lockTime = 0);

    /// <summary>
    /// Builds the unsigned transaction with an absolute fee (an RBF replacement, BIP 125 rules 3 and 4).
    /// </summary>
    UnsignedSweepTransaction BuildWithFee(IReadOnlyList<SweepInput> inputs, byte[] destinationScript, ulong feeSat,
                                          uint lockTime = 0);

    /// <summary>
    /// Adds the BOLT 3 witness of every input from one signature per input (64-byte compact, <c>SIGHASH_ALL</c>).
    /// </summary>
    SignedTransaction AddWitnesses(UnsignedSweepTransaction transaction, IReadOnlyList<CompactSignature> signatures);

    /// <summary>
    /// Signs every input with <see cref="ILightningSigner.SignSweepInput"/> and adds the witnesses.
    /// </summary>
    SignedTransaction Sign(UnsignedSweepTransaction transaction, ILightningSigner signer, ChannelId channelId);
}