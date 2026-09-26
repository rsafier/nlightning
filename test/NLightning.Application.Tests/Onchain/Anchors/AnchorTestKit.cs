using System.Reflection;
using System.Runtime.ExceptionServices;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Anchors;

using Application.Onchain.Anchors;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// An in-memory <c>BroadcastTransactions</c> table for the anchor CPFP tests: writes apply at once, and
/// <see cref="Saves"/> counts <c>SaveChangesAsync</c> calls.
/// </summary>
internal sealed class InMemoryBroadcasts : IBroadcastTransactionDbRepository
{
    public List<BroadcastTransactionModel> Rows { get; } = [];
    public int Saves { get; set; }

    public void Add(BroadcastTransactionModel transaction) => Rows.Add(transaction);

    public Task<BroadcastTransactionModel?> GetByTransactionIdAsync(TxId transactionId) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.TransactionId == transactionId));

    public Task<IReadOnlyList<BroadcastTransactionModel>> GetByChannelIdAsync(ChannelId channelId) =>
        Task.FromResult<IReadOnlyList<BroadcastTransactionModel>>(Rows.Where(r => r.ChannelId == channelId).ToList());

    public Task<bool> MarkAbandonedAsync(TxId transactionId) =>
        Change(transactionId, BroadcastState.Pending, r => r.MarkAbandoned());

    public Task<bool> MarkReplacedAsync(TxId transactionId) =>
        Change(transactionId, BroadcastState.Pending, r => r.MarkReplaced());

    public Task<bool> MarkPendingAsync(TxId transactionId) =>
        Change(transactionId, BroadcastState.Abandoned, r => r.MarkPending());

    public Task<IReadOnlyList<BroadcastTransactionModel>> GetPendingAsync() =>
        Task.FromResult<IReadOnlyList<BroadcastTransactionModel>>(
            Rows.Where(r => r.State == BroadcastState.Pending).ToList());

    public Task MarkConfirmedAsync(TxId transactionId, uint height, Hash blockHash)
    {
        Rows.FirstOrDefault(r => r.TransactionId == transactionId)?.MarkConfirmed(height, blockHash);
        return Task.CompletedTask;
    }

    public Task<int> UnconfirmAboveAsync(uint height) => Task.FromResult(0);

    public IReadOnlyList<BroadcastTransactionModel> Children =>
        Rows.Where(r => r.Purpose == BroadcastPurpose.AnchorCpfp).ToList();

    private Task<bool> Change(TxId transactionId, BroadcastState from, Action<BroadcastTransactionModel> change)
    {
        var row = Rows.FirstOrDefault(r => r.TransactionId == transactionId);
        if (row is null || row.State != from)
            return Task.FromResult(false);

        change(row);
        return Task.FromResult(true);
    }
}

/// <summary>
/// A wallet of P2WPKH outputs for the anchor CPFP tests: <see cref="IAnchorFeeInputSource"/> (largest first, per
/// channel reservations) and the wallet half of the signer (<see cref="SignWalletInputs"/>).
/// </summary>
internal sealed class FakeAnchorWallet : IAnchorFeeInputSource
{
    private readonly List<(AnchorWalletInput Input, Key Key)> _utxos = [];
    private readonly Dictionary<ChannelId, List<AnchorWalletInput>> _reserved = [];

    public int ReleaseCount { get; private set; }

    public IReadOnlyList<AnchorWalletInput> Reserved(ChannelId channelId) =>
        _reserved.TryGetValue(channelId, out var inputs) ? inputs : [];

    public AnchorWalletInput AddUtxo(byte tag, ulong amountSat)
    {
        var key = new Key(Enumerable.Repeat(tag, 32).ToArray());
        var input = new AnchorWalletInput(new TxId(Enumerable.Repeat((byte)(tag ^ 0xFF), 32).ToArray()), tag,
                                          amountSat, key.PubKey.WitHash.ScriptPubKey.ToBytes(), 273);
        _utxos.Add((input, key));
        return input;
    }

    public Task<IReadOnlyList<AnchorWalletInput>?> ReserveAsync(ChannelId channelId, ulong amountSat,
                                                               uint feeratePerKw, CancellationToken cancellationToken)
    {
        var taken = _reserved.Values.SelectMany(r => r).ToHashSet();
        var picked = new List<AnchorWalletInput>();
        var sum = 0UL;
        var needed = amountSat;
        foreach (var (input, _) in _utxos.OrderByDescending(u => u.Input.AmountSat))
        {
            if (sum >= needed)
                break;
            if (taken.Contains(input))
                continue;

            picked.Add(input);
            sum += input.AmountSat;
            needed += (ulong)feeratePerKw * (ulong)input.InputWeight / 1000;
        }

        if (sum < needed)
            return Task.FromResult<IReadOnlyList<AnchorWalletInput>?>(null);

        if (!_reserved.TryGetValue(channelId, out var reserved))
            _reserved[channelId] = reserved = [];
        reserved.AddRange(picked);
        return Task.FromResult<IReadOnlyList<AnchorWalletInput>?>(picked);
    }

    public Task<IReadOnlyList<AnchorWalletInput>> GetReservedAsync(ChannelId channelId,
                                                                  CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AnchorWalletInput>>(Reserved(channelId).ToList());

    public Task ReleaseAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        ReleaseCount++;
        _reserved.Remove(channelId);
        return Task.CompletedTask;
    }

    public TxOut? GetSpentOutput(OutPoint outPoint) =>
        _utxos.Where(u => new OutPoint(new uint256(u.Input.TxId), u.Input.OutputIndex) == outPoint)
              .Select(u => new TxOut(Money.Satoshis(u.Input.AmountSat), new Script(u.Input.ScriptPubKey)))
              .FirstOrDefault();

    /// <summary>Signs every input spending one of our outputs (P2WPKH, <c>SIGHASH_ALL</c>), leaves the others.</summary>
    public bool SignWalletInputs(SignedTransaction transaction)
    {
        var tx = Transaction.Load(transaction.RawTxBytes, Network.Main);
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var match = _utxos.FirstOrDefault(u => new OutPoint(new uint256(u.Input.TxId), u.Input.OutputIndex)
                                                == tx.Inputs[i].PrevOut);
            if (match.Key is null)
                continue;

            var prevOut = new TxOut(Money.Satoshis(match.Input.AmountSat), new Script(match.Input.ScriptPubKey));
            var hash = tx.GetSignatureHash(match.Key.PubKey.Hash.ScriptPubKey, i, SigHash.All, prevOut,
                                           HashVersion.WitnessV0);
            var signature = match.Key.Sign(hash, new SigningOptions(SigHash.All, false));
            tx.Inputs[i].WitScript = new WitScript(Op.GetPushOp(signature.ToBytes()),
                                                   Op.GetPushOp(match.Key.PubKey.ToBytes()));
        }

        transaction.RawTxBytes = tx.ToBytes();
        return true;
    }
}

/// <summary>
/// A real <see cref="ILightningSigner"/> whose <see cref="ILightningSigner.SignWalletTransaction"/> is replaced (the
/// wallet signing of O7-T1 is another lane's), everything else forwarded.
/// </summary>
public class WalletSigningProxy : DispatchProxy
{
    private ILightningSigner _inner = null!;
    private Func<SignedTransaction, bool>? _signWallet;

    public static ILightningSigner Create(ILightningSigner inner, Func<SignedTransaction, bool>? signWallet)
    {
        var proxy = Create<ILightningSigner, WalletSigningProxy>();
        var self = (WalletSigningProxy)(object)proxy;
        self._inner = inner;
        self._signWallet = signWallet;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(ILightningSigner.SignWalletTransaction) && _signWallet is not null)
            return _signWallet((SignedTransaction)args![0]!);

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
}

/// <summary>
/// The chain answers the anchor CPFP asks for: which outpoints are spent (<see cref="Spent"/>, for <c>gettxout</c> with
/// and without the mempool alike), bitcoind's tip, and a switch that makes every answer throw.
/// </summary>
internal sealed class FakeAnchorChain : IBitcoinChainService
{
    public HashSet<OutPoint> Spent { get; } = [];
    public uint Tip { get; set; } = 500;
    public bool Throws { get; set; }

    public Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint) => Answer(outPoint);

    public Task<(TxOut Output, uint Height)?> GetConfirmedUnspentOutputAsync(OutPoint outPoint) => Answer(outPoint);

    public Task<uint> GetCurrentBlockHeightAsync() =>
        Throws ? throw new InvalidOperationException("bitcoind down") : Task.FromResult(Tip);

    public Task<uint256> SendTransactionAsync(Transaction transaction) => throw new NotSupportedException();
    public Task<Transaction?> GetTransactionAsync(uint256 txId) => Task.FromResult<Transaction?>(null);
    public Task<Block?> GetBlockAsync(uint height) => Task.FromResult<Block?>(null);
    public Task<uint256> GetBlockHashAsync(uint height) => Task.FromResult(uint256.Zero);
    public Task<uint> GetTransactionConfirmationsAsync(uint256 txId) => Task.FromResult(0u);

    private Task<(TxOut Output, uint Height)?> Answer(OutPoint outPoint)
    {
        if (Throws)
            throw new InvalidOperationException("bitcoind down");

        return Task.FromResult<(TxOut Output, uint Height)?>(
            Spent.Contains(outPoint) ? null : (new TxOut(Money.Satoshis(330), Script.Empty), 1));
    }
}

/// <summary>Weights, fees and script checks of the transactions the tests look at.</summary>
internal static class AnchorTx
{
    public static long Weight(Transaction tx)
    {
        var stripped = tx.Clone();
        foreach (var input in stripped.Inputs)
            input.WitScript = WitScript.Empty;
        return 3L * stripped.ToBytes().Length + tx.ToBytes().Length;
    }

    public static ulong OutputsSat(Transaction tx) => tx.Outputs.Aggregate(0UL, (s, o) => s + (ulong)o.Value.Satoshi);

    /// <summary>Every input of <paramref name="child"/> executes against the output it spends.</summary>
    public static void AssertScriptsValid(Transaction child, Transaction commitment, FakeAnchorWallet wallet)
    {
        for (var i = 0; i < child.Inputs.Count; i++)
        {
            var prevOut = child.Inputs[i].PrevOut;
            var spent = prevOut.Hash == commitment.GetHash()
                            ? commitment.Outputs[prevOut.N]
                            : wallet.GetSpentOutput(prevOut)
                           ?? throw new InvalidOperationException($"Input {i} spends an unknown output");
            Assert.True(child.Inputs.AsIndexedInputs().ElementAt(i).VerifyScript(spent, out var error),
                        $"input {i}: {error}");
        }
    }

    /// <summary>The child's own fee: the anchor plus its wallet inputs minus its outputs.</summary>
    public static ulong ChildFee(Transaction child, FakeAnchorWallet wallet)
    {
        var inputs = child.Inputs.Skip(1).Aggregate(330UL, (s, i) => s + (ulong)wallet.GetSpentOutput(i.PrevOut)!
                                                                                     .Value.Satoshi);
        return inputs - OutputsSat(child);
    }
}