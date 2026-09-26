using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Remote;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Onchain.Interfaces;

/// <summary>
/// A fake chain around <see cref="RemoteCommitResolver"/> (BOLT 5 plan O4 proofs): two engines with real signers
/// (<see cref="RealSigningCommitmentPair"/>; Alice is us, Bob the peer that force-closes), the production mapper, sweep
/// builder and signer, an in-memory store with EF visibility, and recording broadcaster, outpoint watcher and switch.
/// Every round is saved and completed the way the watcher does it (stage, save, then <c>CompleteAsync</c>).
/// </summary>
internal sealed class RemoteResolutionTestContext : IDisposable
{
    public const uint CloseHeight = 500;
    public const ulong FeeratePerKw = 1_000;

    public static readonly byte[] Destination =
        new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    private readonly ServiceProvider _provider;

    public RealSigningCommitmentPair Pair { get; }
    public ChannelModel Channel => Pair.Alice.Channel;
    public InMemoryOnchainStore Store { get; } = new();
    public List<BroadcastTransactionModel> Published { get; } = [];
    public List<WatchedOutpointModel> Tracked { get; } = [];
    public List<IChannelDomainEvent> SwitchEvents { get; } = [];
    public RemoteResolutionMemory Memory { get; private set; } = new();
    public ICommitmentOutputMapper Mapper { get; }

    /// <summary>The peer commitment on chain (set by <see cref="CloseWith"/>).</summary>
    public Transaction CommitmentTx { get; private set; } = null!;

    public ChannelCloseModel Close { get; private set; } = null!;

    public RemoteResolutionTestContext(bool hasAnchors = false)
    {
        Pair = new RealSigningCommitmentPair(hasAnchors);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton(Pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        _provider = services.BuildServiceProvider();
        Mapper = _provider.GetRequiredService<ICommitmentOutputMapper>();
    }

    /// <summary>Alice's snapshot as the channel's (the engine is stopped once the commitment is on chain).</summary>
    public void UseSnapshot(ChannelCommitments? snapshot = null) =>
        Channel.UpdateCommitments(snapshot ?? Pair.Alice.State);

    /// <summary>Bob broadcasts <paramref name="commit"/> and it confirms at <see cref="CloseHeight"/>.</summary>
    public ChainTx CloseWith(RemoteCommit commit, ChannelCloseKind kind)
    {
        var factory = _provider.GetRequiredService<ICommitmentTransactionModelFactory>();
        var builder = _provider.GetRequiredService<ICommitmentTransactionBuilder>();
        var model = factory.CreateCommitmentTransactionModel(Channel, CommitmentTxSpec.FromCommitmentSpec(commit.Spec),
                                                             CommitmentSide.Remote, commit.Number,
                                                             commit.PerCommitmentPoint);
        var built = builder.BuildWithOutputMap(model);
        CommitmentTx = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var chainTx = ChainTxMapper.FromTransaction(CommitmentTx);
        Close = new ChannelCloseModel(Channel.ChannelId, kind, chainTx.TxId, commit.Number, CloseHeight,
                                      new Hash(new byte[32]), DateTimeOffset.UtcNow);
        return chainTx;
    }

    /// <summary>A resolver as the watcher's scope builds it (a fresh one per round).</summary>
    public RemoteCommitResolver CreateResolver()
    {
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(FeeratePerKw));
        var destination = new Mock<IRemoteSweepDestination>();
        destination.Setup(d => d.GetScriptAsync(It.IsAny<Domain.Channels.ValueObjects.ChannelId>(),
                                                It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Destination);
        var broadcaster = new Mock<IChainBroadcaster>();
        broadcaster.Setup(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                   .Callback<BroadcastTransactionModel>(Published.Add)
                   .ReturnsAsync(true);
        var watcher = new Mock<IOutpointWatcher>();
        watcher.Setup(w => w.TrackWatchedOutpoint(It.IsAny<WatchedOutpointModel>()))
               .Callback<WatchedOutpointModel>(Tracked.Add);
        var htlcSwitch = new Mock<IHtlcSwitch>();
        htlcSwitch.Setup(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()))
                  .Callback<IChannelDomainEvent, CancellationToken>((e, _) => SwitchEvents.Add(e))
                  .Returns(Task.CompletedTask);

        return new RemoteCommitResolver(Mapper, _provider.GetRequiredService<ISweepTransactionBuilder>(),
                                        Pair.Alice.Signer, feeService.Object, destination.Object, broadcaster.Object,
                                        watcher.Object, htlcSwitch.Object, Memory);
    }

    /// <summary>A process restart: the in-memory state of the resolution is gone, the store is kept.</summary>
    public void Restart() => Memory = new RemoteResolutionMemory();

    public Task<RemoteResolutionRound> BeginAsync(ChainTx commitment, uint tip) =>
        RunAsync((resolver, unitOfWork) => resolver.BeginAsync(Channel, Close, commitment, tip, unitOfWork,
                                                               TestContext.Current.CancellationToken));

    public Task<RemoteResolutionRound> ResolveAsync(uint tip) =>
        RunAsync((resolver, unitOfWork) => resolver.ResolveAsync(Channel, Close, tip, unitOfWork,
                                                                 TestContext.Current.CancellationToken));

    public Task<RemoteResolutionRound> SpendAsync(uint vout, ChainTx spender, uint height, uint? tip = null) =>
        RunAsync((resolver, unitOfWork) => resolver.OnOutputSpentAsync(Channel, Close, Close.CommitmentTransactionId,
                                                                       vout, spender, height, tip ?? height,
                                                                       unitOfWork,
                                                                       TestContext.Current.CancellationToken));

    /// <summary>One of our broadcasts confirms at <paramref name="height"/>: the spend reaches the resolver.</summary>
    public Task<RemoteResolutionRound> MineAsync(BroadcastTransactionModel ours, uint height)
    {
        var tx = ChainTxMapper.FromTransaction(Transaction.Load(ours.RawTransaction, Network.Main));
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(Close.CommitmentTransactionId, input.PreviousTxId);
        return SpendAsync(input.PreviousVout, tx, height);
    }

    /// <summary>The row of <paramref name="vout"/> as saved.</summary>
    public OutputResolutionModel Row(uint vout) => Store.Outputs[(Close.CommitmentTransactionId, vout)];

    /// <summary>The saved row of the HTLC output of <paramref name="htlcId"/>.</summary>
    public OutputResolutionModel HtlcRow(ulong htlcId) =>
        Store.Outputs.Values.Single(o => o.HtlcId == htlcId);

    /// <summary>The saved <c>to_remote</c> row.</summary>
    public OutputResolutionModel ToRemoteRow() =>
        Store.Outputs.Values.Single(o => o.Descriptor == OutputDescriptorKind.PaymentToRemote);

    /// <summary>Runs input 0 of <paramref name="broadcast"/> against the commitment output it spends.</summary>
    public bool Verifies(BroadcastTransactionModel broadcast, out ScriptError error)
    {
        var tx = Transaction.Load(broadcast.RawTransaction, Network.Main);
        var spent = CommitmentTx.Outputs[(int)tx.Inputs[0].PrevOut.N];
        return tx.Inputs.AsIndexedInputs().First().VerifyScript(spent, out error);
    }

    /// <summary>A peer transaction spending <paramref name="vout"/> of the commitment with <paramref name="witness"/>.
    /// </summary>
    public ChainTx PeerSpend(uint vout, params byte[][] witness) =>
        new(new TxId(Enumerable.Repeat((byte)0xEE, 32).ToArray()), 2, 0,
            [new ChainTxInput(Close.CommitmentTransactionId, vout, 0, witness)],
            [new ChainTxOutput(1_000, Destination)]);

    /// <summary>A DER-looking signature with the sighash byte (the resolver never checks signatures).</summary>
    public static byte[] FakeSignature() => [0x30, .. Enumerable.Repeat((byte)0x01, 70)];

    private async Task<RemoteResolutionRound> RunAsync(
        Func<RemoteCommitResolver, Domain.Persistence.Interfaces.IUnitOfWork, Task<RemoteResolutionRound>> step)
    {
        var resolver = CreateResolver();
        var (unitOfWork, save) = Store.CreateUnitOfWork();
        var round = await step(resolver, unitOfWork);
        await save();
        await resolver.CompleteAsync(round, TestContext.Current.CancellationToken);
        return round;
    }

    public void Dispose()
    {
        _provider.Dispose();
        Pair.Dispose();
    }
}