using System.Security.Cryptography;
using NBitcoin;

namespace NLightning.Application.Tests.InteractiveTx.TestDoubles;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;

/// <summary>A wallet output the fake contributor can spend (a real regtest transaction as its prevtx).</summary>
internal sealed record WalletUtxo(byte[] PrevTx, TxId TxId, uint Vout, LightningMoney Amount, BitcoinScript Script)
{
    public static WalletUtxo Create(long satoshis)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(RandomNumberGenerator.GetBytes(32)), 0));
        var script = new Key().PubKey.WitHash.ScriptPubKey;
        transaction.Outputs.Add(Money.Satoshis(satoshis), script);
        return new WalletUtxo(transaction.ToBytes(), new TxId(transaction.GetHash().ToBytes()), 0,
                              LightningMoney.Satoshis(satoshis), new BitcoinScript(script.ToBytes()));
    }
}

/// <summary>Parses prevtx with NBitcoin; every txid is confirmed unless listed in <see cref="Unconfirmed"/>.</summary>
internal sealed class FakePrevTxInspector : IPrevTxInspector
{
    public HashSet<TxId> Unconfirmed { get; } = [];

    public PrevTxInspection Inspect(ReadOnlyMemory<byte> prevTx, uint prevTxVout)
    {
        Transaction transaction;
        try
        {
            transaction = Transaction.Load(prevTx.ToArray(), Network.RegTest);
        }
        catch (Exception e)
        {
            return new PrevTxInspection(false, null, 0, null, null, false, $"invalid prevtx: {e.Message}");
        }

        var txId = new TxId(transaction.GetHash().ToBytes());
        if (prevTxVout >= transaction.Outputs.Count)
            return new PrevTxInspection(false, txId, transaction.Outputs.Count, null, null, false,
                                        "prevtx_vout out of range");

        var output = transaction.Outputs[(int)prevTxVout];
        var script = output.ScriptPubKey.ToBytes();
        return new PrevTxInspection(true, txId, transaction.Outputs.Count, LightningMoney.Satoshis(output.Value.Satoshi),
                                    new BitcoinScript(script), IsWitnessProgram(script), null);
    }

    public Task<bool> IsConfirmedAsync(TxId txId, CancellationToken cancellationToken = default) =>
        Task.FromResult(!Unconfirmed.Contains(txId));

    private static bool IsWitnessProgram(byte[] script) =>
        script.Length is >= 4 and <= 42 && (script[0] == 0x00 || script[0] is >= 0x51 and <= 0x60)
     && script[1] == script.Length - 2 && script[1] is >= 2 and <= 40;
}

/// <summary>
/// Builds a deterministic stand-in transaction (sorted by serial id; txid = SHA256d of the serialization). The real
/// builder is lane IT-B's, proven against BOLT 3 Appendix G.
/// </summary>
internal sealed class FakeInteractiveTxBuilder : IInteractiveTxBuilder
{
    public ConstructedInteractiveTx Build(uint locktime, IReadOnlyList<InteractiveTxInput> inputs,
                                          IReadOnlyList<InteractiveTxOutput> outputs)
    {
        if (inputs.Count == 0 || outputs.Count == 0)
            throw new ArgumentException("No input or no output");
        if (inputs.Select(i => i.SerialId).Distinct().Count() != inputs.Count
         || outputs.Select(o => o.SerialId).Distinct().Count() != outputs.Count)
            throw new ArgumentException("Duplicate serial_id");

        var sortedInputs = inputs.OrderBy(i => i.SerialId).ToList();
        var sortedOutputs = outputs.OrderBy(o => o.SerialId).ToList();
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(locktime));
        foreach (var input in sortedInputs)
        {
            stream.Write((byte[])input.PrevTxId);
            stream.Write(BitConverter.GetBytes(input.PrevTxVout));
            stream.Write(BitConverter.GetBytes(input.Sequence));
        }

        foreach (var output in sortedOutputs)
        {
            stream.Write(BitConverter.GetBytes(output.Amount.Satoshi));
            stream.Write((byte[])output.ScriptPubKey);
        }

        var bytes = stream.ToArray();
        var sharedIndex = sortedOutputs.FindIndex(o => o.IsShared);
        return new ConstructedInteractiveTx(new TxId(SHA256.HashData(SHA256.HashData(bytes))), bytes, locktime,
                                            sortedInputs, sortedOutputs,
                                            44 + (sortedInputs.Count * 272) + (sortedOutputs.Count * 172),
                                            sharedIndex < 0 ? null : (uint)sharedIndex);
    }

    public SignedTransaction Finalize(ConstructedInteractiveTx transaction,
                                      IReadOnlyDictionary<ulong, Witness> witnessesBySerialId)
    {
        var bytes = new List<byte>(transaction.UnsignedTx);
        foreach (var input in transaction.Inputs)
        {
            if (!witnessesBySerialId.TryGetValue(input.SerialId, out var witness) || witness.Length == 0)
                throw new ArgumentException($"Input {input.SerialId} has no witness");
            bytes.AddRange((byte[])witness);
        }

        return new SignedTransaction(transaction.TxId, [.. bytes]);
    }
}

/// <summary>
/// A wallet contributor over a list of <see cref="WalletUtxo"/>s: one input covering the amount plus a generous fee
/// (so a stricter engine's feerate check also passes), change back to the wallet, a reservation per contribution.
/// </summary>
internal sealed class FakeInteractiveTxContributor : IInteractiveTxContributor
{
    private const int InputWeight = 300;
    private const int OutputWeight = 200;

    private readonly Dictionary<Guid, List<WalletUtxo>> _reservations = [];

    public List<WalletUtxo> Utxos { get; } = [];
    public HashSet<TxId> Unconfirmed { get; } = [];
    public List<Guid> Released { get; } = [];
    public int SignCalls { get; private set; }
    public BitcoinScript ChangeScript { get; } = new(new Key().PubKey.WitHash.ScriptPubKey.ToBytes());

    /// <summary>
    /// Outpoints a confirmed transaction spent (an RBF sibling of the funding, NL-867): signing a contribution with one
    /// of them fails as the wallet contributor does, its reservation still held.
    /// </summary>
    public HashSet<(TxId TxId, uint Vout)> SpentOnChain { get; } = [];

    public IReadOnlyCollection<Guid> ActiveReservations => _reservations.Keys;

    public Task<InteractiveTxContribution> ContributeAsync(InteractiveTxContributionRequest request,
                                                           CancellationToken cancellationToken = default)
    {
        var reserved = _reservations.Values.SelectMany(u => u).ToHashSet();
        var fee = Fee(request, 1);
        var utxo = Utxos.FirstOrDefault(u => !reserved.Contains(u)
                                          && (!request.RequireConfirmedInputs || !Unconfirmed.Contains(u.TxId))
                                          && u.Amount >= request.WalletAmount + fee + LightningMoney.Satoshis(1_000))
                ?? throw new InsufficientFundsException(request.WalletAmount + fee, LightningMoney.Zero);

        var reservationId = Guid.NewGuid();
        _reservations[reservationId] = [utxo];
        return Task.FromResult(Build([utxo], request, reservationId));
    }

    /// <summary>An RBF contribution that re-adds the previous inputs (double-spend, IT-RBF-01) at a new feerate.</summary>
    public InteractiveTxContribution ContributeReusing(InteractiveTxContribution previous,
                                                       InteractiveTxContributionRequest request)
    {
        var utxos = previous.Inputs.Select(i => Utxos.Single(u => u.TxId == i.PrevTxId && u.Vout == i.PrevTxVout))
                            .ToList();
        return Build(utxos, request, previous.ReservationId ?? Guid.NewGuid());
    }

    public Task ReleaseAsync(InteractiveTxContribution contribution, CancellationToken cancellationToken = default)
    {
        if (contribution.ReservationId is { } id)
        {
            Released.Add(id);
            _reservations.Remove(id);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Witness>> SignAsync(ConstructedInteractiveTx transaction,
                                                  InteractiveTxContribution contribution,
                                                  IReadOnlyList<SpentOutput> otherSpentOutputs,
                                                  CancellationToken cancellationToken = default)
    {
        SignCalls++;
        if (contribution.Inputs.Any(i => SpentOnChain.Contains((i.PrevTxId, i.PrevTxVout))))
            throw new InteractiveTxInputsSpentException(
                $"An input of interactive transaction {transaction.TxId} was spent on chain");

        IReadOnlyList<Witness> witnesses = contribution.Inputs.Select(i => CreateP2WpkhWitness(i.PrevTxId)).ToList();
        return Task.FromResult(witnesses);
    }

    /// <summary>
    /// A well-formed P2WPKH witness (BIP 141 stack: a DER signature ending in SIGHASH_ALL and a 33-byte compressed
    /// key), as the real engine checks it (IT-SIG-02); the signature is over a hash of the outpoint, not the
    /// transaction, since the test transaction is never broadcast.
    /// </summary>
    internal static Witness CreateP2WpkhWitness(TxId prevTxId)
    {
        using var key = new Key(SHA256.HashData((byte[])prevTxId));
        var signature = new TransactionSignature(key.Sign(new uint256(SHA256.HashData((byte[])prevTxId))),
                                                 SigHash.All);
        return new Witness(new WitScript(new[] { signature.ToBytes(), key.PubKey.ToBytes() }).ToBytes());
    }

    public Task<bool> ConfirmAsync(InteractiveTxContribution contribution,
                                   CancellationToken cancellationToken = default) => Task.FromResult(true);

    private InteractiveTxContribution Build(List<WalletUtxo> utxos, InteractiveTxContributionRequest request,
                                            Guid reservationId)
    {
        var total = utxos.Aggregate(LightningMoney.Zero, (sum, u) => sum + u.Amount);
        var change = total - request.WalletAmount - Fee(request, utxos.Count);
        var inputs = utxos.Select(u => new ContributedInput(u.TxId, u.Vout, u.PrevTx,
                                                            InteractiveTransactionConstants.MaxSequence, u.Amount,
                                                            u.Script, InputWeight)).ToList();
        List<ContributedOutput> outputs = [.. request.Outputs, new ContributedOutput(change, ChangeScript, true)];
        return new InteractiveTxContribution(inputs, outputs, reservationId);
    }

    private static LightningMoney Fee(InteractiveTxContributionRequest request, int inputCount)
    {
        var weight = (inputCount * InputWeight) + ((request.Outputs.Count + 1) * OutputWeight) + request.ExtraWeight;
        return LightningMoney.Satoshis(weight * (long)request.FeeratePerKw / 1000 + 1);
    }
}

/// <summary>Stages writes and applies them on <see cref="Commit"/> (called by the unit of work's save).</summary>
internal sealed class InMemoryInteractiveTxSessionRepository : IInteractiveTxSessionDbRepository
{
    private readonly List<InteractiveTxSessionModel> _staged = [];

    public Dictionary<(ChannelId, Guid), InteractiveTxSessionModel> Committed { get; } = [];

    public void Add(InteractiveTxSessionModel session)
    {
        if (Committed.ContainsKey((session.ChannelId, session.SessionId)))
            throw new InvalidOperationException("Duplicate session");
        _staged.Add(session);
    }

    public Task UpdateAsync(InteractiveTxSessionModel session)
    {
        if (!Committed.ContainsKey((session.ChannelId, session.SessionId))
         && !_staged.Any(s => s.ChannelId == session.ChannelId && s.SessionId == session.SessionId))
            throw new KeyNotFoundException("No such session");
        _staged.Add(session);
        return Task.CompletedTask;
    }

    public Task<InteractiveTxSessionModel?> GetByIdAsync(ChannelId channelId, Guid sessionId) =>
        Task.FromResult(Committed.GetValueOrDefault((channelId, sessionId)));

    public Task<IReadOnlyList<InteractiveTxSessionModel>> GetByChannelIdAsync(ChannelId channelId) =>
        Task.FromResult<IReadOnlyList<InteractiveTxSessionModel>>(
            Committed.Values.Where(s => s.ChannelId == channelId).OrderBy(s => s.CreatedAt).ToList());

    public Task<IReadOnlyList<InteractiveTxSessionModel>> GetUnresolvedAsync() =>
        Task.FromResult<IReadOnlyList<InteractiveTxSessionModel>>(
            Committed.Values.Where(s => s.State != Domain.Protocol.InteractiveTx.Enums.InteractiveTxSessionState.Aborted
                                     && s.ResolvedAt is null).ToList());

    public Task<bool> DeleteAsync(ChannelId channelId, Guid sessionId) =>
        Task.FromResult(Committed.Remove((channelId, sessionId)));

    public Task<int> DeleteByChannelIdAsync(ChannelId channelId)
    {
        var keys = Committed.Keys.Where(k => k.Item1 == channelId).ToList();
        foreach (var key in keys)
            Committed.Remove(key);
        return Task.FromResult(keys.Count);
    }

    public void Commit()
    {
        foreach (var session in _staged)
            Committed[(session.ChannelId, session.SessionId)] = session;
        _staged.Clear();
    }

    public void DiscardStaged() => _staged.Clear();
}

/// <summary>A stand-in for the host's commitment_signed of the new funding (the harness routes it by type).</summary>
internal sealed class TestCommitmentSignedMessage(ChannelId channelId) : IChannelMessage
{
    public MessageTypes Type => MessageTypes.CommitmentSigned;
    public IChannelMessagePayload Payload { get; } = new TestPayload(channelId);
    IMessagePayload IMessage.Payload => Payload;
    public Domain.Protocol.Models.TlvStream? Extension => null;

    private sealed class TestPayload(ChannelId channelId) : IChannelMessagePayload
    {
        public ChannelId ChannelId { get; } = channelId;
    }
}