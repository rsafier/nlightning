using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Onchain.Resolvers.Local;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Onchain.Models;

/// <summary>
/// A stand-in for the wallet behind <see cref="IAnchorFeeInputProvider"/> (O7-T1's selector and
/// <c>SignWalletTransaction</c>): P2WPKH outputs of one key, each in its own funding transaction, reserved by
/// <see cref="SelectAsync"/> (largest first, until they pay the fee at the asked rate plus a dust-free change) and signed
/// with <c>SIGHASH_ALL</c> by <see cref="SignAsync"/>. Reservations are keyed by owner and replaced by the owner's next
/// selection, as the port's contract requires.
/// </summary>
internal sealed class AnchorTestWallet : IAnchorFeeInputProvider
{
    /// <summary>The signed weight of a P2TR key-path input: <c>164 + (1 + 1 + 64)</c>.</summary>
    public const long P2TrInputWeight = 230;

    private static readonly Key s_key = new(Enumerable.Repeat((byte)0x5C, 32).ToArray());
    private static readonly byte[] s_script = s_key.PubKey.WitHash.ScriptPubKey.ToBytes();
    private static readonly byte[] s_taprootScript = s_key.PubKey.GetTaprootFullPubKey().ScriptPubKey.ToBytes();
    private readonly List<AnchorFeeInput> _available = [];

    public AnchorTestWallet(params ulong[] amounts) : this(false, amounts)
    {
    }

    /// <param name="taproot">P2TR (BIP 86 key path) outputs instead of P2WPKH ones: their signatures commit to every
    /// spent output, the HTLC input's included.</param>
    /// <param name="amounts">One output per amount.</param>
    public AnchorTestWallet(bool taproot, params ulong[] amounts)
    {
        var script = taproot ? s_taprootScript : s_script;
        for (var i = 0; i < amounts.Length; i++)
        {
            var funding = Transaction.Create(Network.Main);
            funding.Inputs.Add(new OutPoint(new uint256((ulong)(i + 1)), 0));
            funding.Outputs.Add(new TxOut(Money.Satoshis(amounts[i]), new Script(script)));
            FundingTransactions.Add(funding);
            _available.Add(new AnchorFeeInput(funding.GetHash().ToBytes(), 0, amounts[i], script,
                                              taproot ? P2TrInputWeight : AnchorFeeInput.P2WpkhInputWeight));
        }
    }

    /// <summary>The HTLC input each <see cref="SignAsync"/> was given (what a P2TR signature commits to).</summary>
    public List<SpentOutput> SignedHtlcInputs { get; } = [];

    /// <summary>The transactions that hold the wallet's outputs (for the chain's script checks).</summary>
    public List<Transaction> FundingTransactions { get; } = [];

    /// <summary>The change script every selection returns.</summary>
    public byte[] ChangeScript { get; } =
        new Key(Enumerable.Repeat((byte)0x5D, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    public List<(AnchorFeeInputOwner Owner, long BaseWeight, uint FeeratePerKw)> Selections { get; } = [];
    public Dictionary<AnchorFeeInputOwner, List<AnchorFeeInput>> Reservations { get; } = [];
    public IReadOnlyList<AnchorFeeInput> Reserved => Reservations.Values.SelectMany(r => r).ToList();
    public List<AnchorFeeInputOwner> Released { get; } = [];
    public int SignCount { get; private set; }

    /// <summary>The output was spent by another transaction (the wallet no longer has it).</summary>
    public void MarkSpent(AnchorFeeInput input) => _available.Remove(input);

    /// <summary>The wallet's output held by <paramref name="index"/>'s funding transaction.</summary>
    public AnchorFeeInput Output(int index) =>
        _available.Concat(Reserved).First(i => i.TxId == new TxId(FundingTransactions[index].GetHash().ToBytes()));

    public Task<AnchorFeeInputSelection?> SelectAsync(AnchorFeeInputOwner owner, long baseWeight, uint feeratePerKw,
                                                      CancellationToken cancellationToken)
    {
        // The contract: a new selection for the same owner replaces its earlier reservation
        Reservations.Remove(owner);
        Selections.Add((owner, baseWeight, feeratePerKw));
        var chosen = new List<AnchorFeeInput>();
        ulong total = 0;
        foreach (var input in _available.Except(Reserved).OrderByDescending(i => i.AmountSat))
        {
            chosen.Add(input);
            total += input.AmountSat;
            var weight = baseWeight + chosen.Sum(i => i.InputWeight);
            if (total >= (ulong)feeratePerKw * (ulong)weight / 1000 + 294)
            {
                Reservations[owner] = chosen;
                return Task.FromResult<AnchorFeeInputSelection?>(new AnchorFeeInputSelection(chosen, ChangeScript));
            }
        }

        return Task.FromResult<AnchorFeeInputSelection?>(null);
    }

    public Task<SignedTransaction> SignAsync(SignedTransaction transaction, IReadOnlyList<AnchorFeeInput> feeInputs,
                                             SpentOutput htlcInput, CancellationToken cancellationToken)
    {
        SignCount++;
        SignedHtlcInputs.Add(htlcInput);
        var tx = Transaction.Load(transaction.RawTxBytes, Network.Main);
        var allSpent = new List<TxOut>
        {
            new(Money.Satoshis(htlcInput.Amount.Satoshi), new Script((byte[])htlcInput.ScriptPubKey))
        };
        for (var i = 1; i < tx.Inputs.Count; i++)
        {
            var input = feeInputs.Single(f => new OutPoint(new uint256((byte[])f.TxId), f.Vout) == tx.Inputs[i].PrevOut);
            allSpent.Add(new TxOut(Money.Satoshis(input.AmountSat), new Script(input.ScriptPubKey)));
        }

        for (var i = 1; i < tx.Inputs.Count; i++)
        {
            var input = feeInputs.Single(f => new OutPoint(new uint256((byte[])f.TxId), f.Vout) == tx.Inputs[i].PrevOut);
            var spent = new TxOut(Money.Satoshis(input.AmountSat), new Script(input.ScriptPubKey));
            if (spent.ScriptPubKey.IsScriptType(ScriptType.Taproot))
            {
                var taprootHash = tx.GetSignatureHashTaproot(allSpent.ToArray(), new TaprootExecutionData(i));
                tx.Inputs[i].WitScript = new WitScript(
                    Op.GetPushOp(s_key.CreateTaprootKeyPair().SignTaprootKeySpend(taprootHash, TaprootSigHash.Default)
                                      .ToBytes()));
                continue;
            }

            var hash = tx.GetSignatureHash(s_key.PubKey.Hash.ScriptPubKey, i, SigHash.All, spent,
                                           HashVersion.WitnessV0);
            tx.Inputs[i].WitScript = PayToWitPubKeyHashTemplate.Instance.GenerateWitScript(
                new TransactionSignature(s_key.Sign(hash), SigHash.All), s_key.PubKey);
        }

        return Task.FromResult(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()));
    }

    public Task ReleaseAsync(AnchorFeeInputOwner owner, CancellationToken cancellationToken)
    {
        Released.Add(owner);
        Reservations.Remove(owner);
        return Task.CompletedTask;
    }
}