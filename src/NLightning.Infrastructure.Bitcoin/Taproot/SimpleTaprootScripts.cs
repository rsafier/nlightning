using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Taproot;

using Crypto.Hashes;
using Domain.Crypto.Constants;
using Exceptions;

/// <summary>
/// The tapscript leaves of <c>option_simple_taproot</c> (bolt-simple-taproot.md §Commitment Transactions, §HTLC Scripts
/// &amp; Transactions). Every key is pushed as its 32-byte x-only form (BIP 340); every leaf uses leaf version 0xc0.
/// </summary>
/// <remarks>
/// "local"/"remote" are the commitment holder and the other side, as in BOLT 3. The scripts are byte-exact against the
/// spec's vectors (<c>Taproot/SimpleTaprootScriptVectorTests</c>).
/// </remarks>
public static class SimpleTaprootScripts
{
    /// <summary>
    /// <c>simple_taproot_nums</c>: the nothing-up-my-sleeve point that is the internal key of to_local and to_remote, so
    /// both can only be spent by script. The spec's §To Remote Outputs names a second point (<c>0245b181...</c>) and a
    /// control block with <c>combined_funding_key</c>; its own vectors use this point for both outputs, and so do we.
    /// </summary>
    public static readonly PubKey NumsPoint =
        new(Convert.FromHexString("02dca094751109d0bd055d03565874e8276dd53e926b44e3bd1bb6bf4bc130a279"));

    /// <summary>The BIP 341 tapscript leaf version of every leaf.</summary>
    public const TapLeafVersion LeafVersion = TapLeafVersion.C0;

    /// <summary>
    /// to_local delay leaf: <c>&lt;local_delayedpubkey&gt; OP_CHECKSIGVERIFY &lt;to_self_delay&gt; OP_CHECKSEQUENCEVERIFY</c>.
    /// The second-level HTLC output uses the same leaf.
    /// </summary>
    public static Script CreateToLocalDelayScript(PubKey localDelayedPubKey, uint toSelfDelay)
    {
        ArgumentNullException.ThrowIfNull(localDelayedPubKey);

        return Validate(new Script(
                            Op.GetPushOp(XOnly(localDelayedPubKey)),
                            OpcodeType.OP_CHECKSIGVERIFY,
                            Op.GetPushOp(toSelfDelay),
                            OpcodeType.OP_CHECKSEQUENCEVERIFY));
    }

    /// <summary>
    /// to_local revocation leaf: <c>&lt;local_delayedpubkey&gt; OP_DROP &lt;revocation_pubkey&gt; OP_CHECKSIG</c> (the noop push
    /// reveals the delayed key, so the anchor can be spent from chain data).
    /// </summary>
    public static Script CreateToLocalRevokeScript(PubKey localDelayedPubKey, PubKey revocationPubKey)
    {
        ArgumentNullException.ThrowIfNull(localDelayedPubKey);
        ArgumentNullException.ThrowIfNull(revocationPubKey);

        return Validate(new Script(
                            Op.GetPushOp(XOnly(localDelayedPubKey)),
                            OpcodeType.OP_DROP,
                            Op.GetPushOp(XOnly(revocationPubKey)),
                            OpcodeType.OP_CHECKSIG));
    }

    /// <summary>to_remote leaf: <c>&lt;remotepubkey&gt; OP_CHECKSIGVERIFY 1 OP_CHECKSEQUENCEVERIFY</c>.</summary>
    public static Script CreateToRemoteScript(PubKey remotePubKey)
    {
        ArgumentNullException.ThrowIfNull(remotePubKey);

        return Validate(new Script(
                            Op.GetPushOp(XOnly(remotePubKey)),
                            OpcodeType.OP_CHECKSIGVERIFY,
                            OpcodeType.OP_1,
                            OpcodeType.OP_CHECKSEQUENCEVERIFY));
    }

    /// <summary>Anchor leaf: <c>OP_16 OP_CHECKSEQUENCEVERIFY</c> (anyone may sweep after 16 blocks).</summary>
    public static Script CreateAnchorScript() =>
        Validate(new Script(OpcodeType.OP_16, OpcodeType.OP_CHECKSEQUENCEVERIFY));

    /// <summary>
    /// Offered HTLC timeout leaf: <c>&lt;local_htlcpubkey&gt; OP_CHECKSIGVERIFY &lt;remote_htlcpubkey&gt; OP_CHECKSIG</c>
    /// (the HTLC-timeout transaction's path; its locktime enforces <c>cltv_expiry</c>).
    /// </summary>
    public static Script CreateOfferedHtlcTimeoutScript(PubKey localHtlcPubKey, PubKey remoteHtlcPubKey)
    {
        ArgumentNullException.ThrowIfNull(localHtlcPubKey);
        ArgumentNullException.ThrowIfNull(remoteHtlcPubKey);

        return Validate(new Script(
                            Op.GetPushOp(XOnly(localHtlcPubKey)),
                            OpcodeType.OP_CHECKSIGVERIFY,
                            Op.GetPushOp(XOnly(remoteHtlcPubKey)),
                            OpcodeType.OP_CHECKSIG));
    }

    /// <summary>
    /// Offered HTLC success leaf: <c>OP_SIZE 32 OP_EQUALVERIFY OP_HASH160 &lt;RIPEMD160(payment_hash)&gt; OP_EQUALVERIFY
    /// &lt;remote_htlcpubkey&gt; OP_CHECKSIGVERIFY 1 OP_CHECKSEQUENCEVERIFY</c> (the other side's preimage claim).
    /// </summary>
    public static Script CreateOfferedHtlcSuccessScript(ReadOnlySpan<byte> paymentHash, PubKey remoteHtlcPubKey)
    {
        ArgumentNullException.ThrowIfNull(remoteHtlcPubKey);

        return Validate(new Script(
                            [
                                .. PreimageCheck(paymentHash),
                                Op.GetPushOp(XOnly(remoteHtlcPubKey)),
                                OpcodeType.OP_CHECKSIGVERIFY,
                                OpcodeType.OP_1,
                                OpcodeType.OP_CHECKSEQUENCEVERIFY
                            ]));
    }

    /// <summary>
    /// Accepted HTLC timeout leaf: <c>&lt;remote_htlcpubkey&gt; OP_CHECKSIGVERIFY 1 OP_CHECKSEQUENCEVERIFY OP_VERIFY
    /// &lt;cltv_expiry&gt; OP_CHECKLOCKTIMEVERIFY</c> (the other side's claim once the HTLC expired).
    /// </summary>
    public static Script CreateAcceptedHtlcTimeoutScript(PubKey remoteHtlcPubKey, uint cltvExpiry)
    {
        ArgumentNullException.ThrowIfNull(remoteHtlcPubKey);

        return Validate(new Script(
                            Op.GetPushOp(XOnly(remoteHtlcPubKey)),
                            OpcodeType.OP_CHECKSIGVERIFY,
                            OpcodeType.OP_1,
                            OpcodeType.OP_CHECKSEQUENCEVERIFY,
                            OpcodeType.OP_VERIFY,
                            Op.GetPushOp(cltvExpiry),
                            OpcodeType.OP_CHECKLOCKTIMEVERIFY));
    }

    /// <summary>
    /// Accepted HTLC success leaf: <c>OP_SIZE 32 OP_EQUALVERIFY OP_HASH160 &lt;RIPEMD160(payment_hash)&gt; OP_EQUALVERIFY
    /// &lt;local_htlcpubkey&gt; OP_CHECKSIGVERIFY &lt;remote_htlcpubkey&gt; OP_CHECKSIG</c> (the HTLC-success transaction's
    /// path).
    /// </summary>
    public static Script CreateAcceptedHtlcSuccessScript(ReadOnlySpan<byte> paymentHash, PubKey localHtlcPubKey,
                                                         PubKey remoteHtlcPubKey)
    {
        ArgumentNullException.ThrowIfNull(localHtlcPubKey);
        ArgumentNullException.ThrowIfNull(remoteHtlcPubKey);

        return Validate(new Script(
                            [
                                .. PreimageCheck(paymentHash),
                                Op.GetPushOp(XOnly(localHtlcPubKey)),
                                OpcodeType.OP_CHECKSIGVERIFY,
                                Op.GetPushOp(XOnly(remoteHtlcPubKey)),
                                OpcodeType.OP_CHECKSIG
                            ]));
    }

    /// <summary>
    /// The funding output's scriptPubKey <c>OP_1 &lt;funding_key&gt;</c>, where <c>funding_key</c> is the BIP 86 tweak of
    /// <paramref name="combinedFundingKey"/>, the MuSig2 aggregate of both funding keys (<c>KeyAgg(KeySort(...))</c>,
    /// computed by the MuSig2 module).
    /// </summary>
    public static TaprootFullPubKey CreateFundingOutputKey(TaprootInternalPubKey combinedFundingKey)
    {
        ArgumentNullException.ThrowIfNull(combinedFundingKey);
        return combinedFundingKey.GetTaprootFullPubKey();
    }

    /// <summary>The 32-byte x-only (BIP 340) form of a key.</summary>
    internal static byte[] XOnly(PubKey pubKey) => pubKey.TaprootInternalKey.ToBytes();

    private static Op[] PreimageCheck(ReadOnlySpan<byte> paymentHash)
    {
        if (paymentHash.Length != CryptoConstants.Sha256HashLen)
            throw new ArgumentException("A payment hash is 32 bytes", nameof(paymentHash));

        return
        [
            OpcodeType.OP_SIZE,
            Op.GetPushOp(32),
            OpcodeType.OP_EQUALVERIFY,
            OpcodeType.OP_HASH160,
            Op.GetPushOp(Ripemd160.Hash(paymentHash)),
            OpcodeType.OP_EQUALVERIFY
        ];
    }

    private static Script Validate(Script script)
    {
        if (script.IsUnspendable || !script.IsValid)
            throw new InvalidScriptException("Tapscript leaf is either 'invalid' or 'unspendable'.");

        return script;
    }
}