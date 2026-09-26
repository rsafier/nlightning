using System.Reflection;
using System.Runtime.ExceptionServices;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Onchain.Resolvers.Local;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;

/// <summary>
/// A stand-in for the wallet behind <see cref="IAnchorFeeInputProvider"/> (O7-T1's selector and
/// <c>SignWalletTransaction</c>): P2WPKH outputs of one key, each in its own funding transaction, reserved by
/// <see cref="SelectAsync"/> (largest first, until they pay the fee at the asked rate plus a dust-free change) and signed
/// with <c>SIGHASH_ALL</c> by <see cref="SignAsync"/>.
/// </summary>
internal sealed class AnchorTestWallet : IAnchorFeeInputProvider
{
    private static readonly Key s_key = new(Enumerable.Repeat((byte)0x5C, 32).ToArray());
    private static readonly byte[] s_script = s_key.PubKey.WitHash.ScriptPubKey.ToBytes();
    private readonly List<AnchorFeeInput> _available = [];

    public AnchorTestWallet(params ulong[] amounts)
    {
        for (var i = 0; i < amounts.Length; i++)
        {
            var funding = Transaction.Create(Network.Main);
            funding.Inputs.Add(new OutPoint(new uint256((ulong)(i + 1)), 0));
            funding.Outputs.Add(new TxOut(Money.Satoshis(amounts[i]), new Script(s_script)));
            FundingTransactions.Add(funding);
            _available.Add(new AnchorFeeInput(funding.GetHash().ToBytes(), 0, amounts[i], s_script,
                                              AnchorFeeInput.P2WpkhInputWeight));
        }
    }

    /// <summary>The transactions that hold the wallet's outputs (for the chain's script checks).</summary>
    public List<Transaction> FundingTransactions { get; } = [];

    /// <summary>The change script every selection returns.</summary>
    public byte[] ChangeScript { get; } =
        new Key(Enumerable.Repeat((byte)0x5D, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    public List<(ChannelId ChannelId, long BaseWeight, uint FeeratePerKw)> Selections { get; } = [];
    public List<AnchorFeeInput> Reserved { get; } = [];
    public List<AnchorFeeInput> Released { get; } = [];
    public int SignCount { get; private set; }

    public Task<AnchorFeeInputSelection?> SelectAsync(ChannelId channelId, long baseWeight, uint feeratePerKw,
                                                      CancellationToken cancellationToken)
    {
        Selections.Add((channelId, baseWeight, feeratePerKw));
        var chosen = new List<AnchorFeeInput>();
        ulong total = 0;
        foreach (var input in _available.Except(Reserved).OrderByDescending(i => i.AmountSat))
        {
            chosen.Add(input);
            total += input.AmountSat;
            var weight = baseWeight + chosen.Sum(i => i.InputWeight);
            if (total >= (ulong)feeratePerKw * (ulong)weight / 1000 + 294)
            {
                Reserved.AddRange(chosen);
                return Task.FromResult<AnchorFeeInputSelection?>(new AnchorFeeInputSelection(chosen, ChangeScript));
            }
        }

        return Task.FromResult<AnchorFeeInputSelection?>(null);
    }

    public Task<SignedTransaction> SignAsync(SignedTransaction transaction, IReadOnlyList<AnchorFeeInput> feeInputs,
                                             CancellationToken cancellationToken)
    {
        SignCount++;
        var tx = Transaction.Load(transaction.RawTxBytes, Network.Main);
        for (var i = 1; i < tx.Inputs.Count; i++)
        {
            var input = feeInputs.Single(f => new OutPoint(new uint256((byte[])f.TxId), f.Vout) == tx.Inputs[i].PrevOut);
            var spent = new TxOut(Money.Satoshis(input.AmountSat), new Script(input.ScriptPubKey));
            var hash = tx.GetSignatureHash(s_key.PubKey.Hash.ScriptPubKey, i, SigHash.All, spent,
                                           HashVersion.WitnessV0);
            tx.Inputs[i].WitScript = PayToWitPubKeyHashTemplate.Instance.GenerateWitScript(
                new TransactionSignature(s_key.Sign(hash), SigHash.All), s_key.PubKey);
        }

        return Task.FromResult(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()));
    }

    public Task ReleaseAsync(IReadOnlyList<AnchorFeeInput> feeInputs, CancellationToken cancellationToken)
    {
        Released.AddRange(feeInputs);
        foreach (var input in feeInputs)
            Reserved.Remove(input);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Alice's real signer, except that <c>SignLocalHtlcTransaction</c> of a combined anchors HTLC transaction (more than
/// one input) is signed here with her HTLC key for the commitment's point, as the signer is to do once it accepts a
/// combined transaction (the signer seam of O7-T3: <c>LocalLightningSigner</c> still refuses more than one input).
/// </summary>
internal class CombinedHtlcSigningProxy : DispatchProxy
{
    private ILightningSigner _inner = null!;
    private Func<(RealSigningNode Node, IKeyDerivationService KeyDerivation)> _resolve = null!;

    /// <summary>How many combined transactions it signed.</summary>
    public int CombinedSignatures { get; private set; }

    /// <param name="inner">The real signer.</param>
    /// <param name="resolve">The node whose keys sign and the key derivation, read at the first combined signature
    /// (the harness builds its container before they exist).</param>
    public static ILightningSigner Create(ILightningSigner inner,
                                          Func<(RealSigningNode Node, IKeyDerivationService KeyDerivation)> resolve)
    {
        var proxy = Create<ILightningSigner, CombinedHtlcSigningProxy>();
        var self = (CombinedHtlcSigningProxy)(object)proxy;
        self._inner = inner;
        self._resolve = resolve;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(ILightningSigner.SignLocalHtlcTransaction)
         && args is [ChannelId, HtlcSigningContext context]
         && Transaction.Load(context.HtlcTransaction.Transaction.RawTxBytes, Network.Main).Inputs.Count > 1)
            return SignCombined(context);

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private CompactSignature SignCombined(HtlcSigningContext context)
    {
        CombinedSignatures++;
        var (node, keyDerivation) = _resolve();

        // The node's channel key m/i' and its HTLC basepoint secret m/i'/4' (LocalLightningSigner's layout)
        var rootKey = new ExtKey(new Key(Enumerable.Repeat((byte)node.KeyIndex, 32).ToArray()), new byte[32]);
        using var htlcBasepointSecret = rootKey.Derive((int)node.KeyIndex, true).Derive(4, true).PrivateKey;
        Assert.Equal((byte[])node.Basepoints.HtlcBasepoint, htlcBasepointSecret.PubKey.ToBytes());

        var built = context.HtlcTransaction;
        var tx = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var witnessScript = new Script((byte[])built.SpentWitnessScript);
        var spent = new TxOut(Money.Satoshis(built.SpentAmount.Satoshi), witnessScript.WitHash.ScriptPubKey);
        var hash = tx.GetSignatureHash(witnessScript, 0, SigHash.All, spent, HashVersion.WitnessV0);
        using var htlcKey = new Key(keyDerivation.DerivePrivateKey(htlcBasepointSecret.ToBytes(),
                                                                   context.PerCommitmentPoint));
        return htlcKey.Sign(hash, new SigningOptions(SigHash.All, false)).Signature.MakeCanonical().ToCompact();
    }
}