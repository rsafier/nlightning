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
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Onchain.Interfaces;

/// <summary>
/// A fake chain around <see cref="RemoteCommitResolver"/> (BOLT 5 plan O4 proofs): two engines with real signers
/// (<see cref="RealSigningCommitmentPair"/>; Alice is us, Bob the peer that force-closes), the production mapper, sweep
/// builder and signer, an in-memory store with EF visibility, and a minimal on-chain resolution executor as the
/// <c>IOutputResolver</c> contract describes it: it applies each round's actions in one save, then records the
/// broadcasts, watches, switch events and alerts; it marks a spent output Resolved (before calling
/// <c>OnOutputSpentAsync</c>) and Irrevocable once the spend is 100 blocks deep.
/// </summary>
internal sealed class RemoteResolutionTestContext : IDisposable
{
    public const uint CloseHeight = 500;
    public const ulong FeeratePerKw = 1_000;
    public const uint IrrevocableDepth = 100;

    public static readonly byte[] Destination =
        new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    private readonly ServiceProvider _provider;

    public RealSigningCommitmentPair Pair { get; }
    public ChannelModel Channel => Pair.Alice.Channel;
    public InMemoryOnchainStore Store { get; } = new();
    public List<BroadcastTransactionModel> Published { get; } = [];
    public List<WatchedOutpointModel> Tracked { get; } = [];
    public List<IChannelDomainEvent> SwitchEvents { get; } = [];
    public List<AlertAction> Alerts { get; } = [];
    public ICommitmentOutputMapper Mapper { get; }

    /// <summary>The fee policy the resolver uses (a high floor makes every output uneconomic).</summary>
    public SweepFeePolicy? FeePolicy { get; set; }

    /// <summary>How many scripts the sweep destination handed out.</summary>
    public int DestinationCalls { get; private set; }

    /// <summary>The peer commitment on chain (set by <see cref="CloseWith"/>).</summary>
    public Transaction CommitmentTx { get; private set; } = null!;

    public ChainTx ChainCommitment { get; private set; } = null!;

    public ChannelCloseModel Close { get; private set; } = null!;

    public RemoteResolutionTestContext(bool hasAnchors = false)
    {
        Pair = new RealSigningCommitmentPair(hasAnchors);
        Store.Channels[Channel.ChannelId] = Channel;

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton(Pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddScoped<IUnitOfWork>(_ => Store.CreateUnitOfWork().UnitOfWork);
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
        ChainCommitment = ChainTxMapper.FromTransaction(CommitmentTx);
        Close = new ChannelCloseModel(Channel.ChannelId, kind, ChainCommitment.TxId, commit.Number, CloseHeight,
                                      new Hash(new byte[32]), DateTimeOffset.UtcNow);
        return ChainCommitment;
    }

    /// <summary>A resolver as the node registers it (a singleton reading through scopes of its own).</summary>
    public RemoteCommitResolver CreateResolver()
    {
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(FeeratePerKw));
        var destination = new Mock<IRemoteSweepDestination>();
        destination.Setup(d => d.GetScriptAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
                   .Callback(() => DestinationCalls++)
                   .ReturnsAsync(Destination);
        var source = new Mock<IRemoteCommitmentSource>();
        source.Setup(s => s.GetCommitmentAsync(It.IsAny<ChannelCloseModel>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => ChainCommitment);

        return new RemoteCommitResolver(Mapper, _provider.GetRequiredService<ISweepTransactionBuilder>(),
                                        Pair.Alice.Signer, feeService.Object, destination.Object, source.Object,
                                        _provider.GetRequiredService<IServiceScopeFactory>(), feePolicy: FeePolicy);
    }

    /// <summary>The first round, right after the funding spend is classified at <paramref name="tip"/>.</summary>
    public Task<ResolutionRound> BeginAsync(uint tip) => ResolveAsync(tip);

    /// <summary>A processed block at <paramref name="tip"/>: the executor's per-block updates, then the resolver.</summary>
    public async Task<ResolutionRound> ResolveAsync(uint tip)
    {
        var (unitOfWork, save) = Store.CreateUnitOfWork();
        foreach (var row in SavedRows().Where(r => r is
        {
            State: OutputResolutionState.Resolved,
            ResolvedHeight: { } resolvedAt
        }
                                                && tip >= resolvedAt
                                                && tip - resolvedAt + 1 >= IrrevocableDepth))
            await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(
                row with { State = OutputResolutionState.Irrevocable });
        await save();

        var actions = await CreateResolver().ResolveAsync(Close, SavedRows(), tip,
                                                          TestContext.Current.CancellationToken);
        return await ApplyAsync(actions);
    }

    /// <summary>
    /// <paramref name="spender"/> spends output <paramref name="vout"/> of the commitment in the block at
    /// <paramref name="height"/>: the monitor records it, the executor marks the row Resolved, asks the resolver about
    /// the spend, then runs the block's round at <paramref name="tip"/>.
    /// </summary>
    public async Task<ResolutionRound> SpendAsync(uint vout, ChainTx spender, uint height, uint? tip = null)
    {
        Store.MarkSpent(Close.CommitmentTransactionId, vout, spender.TxId, height);
        var row = Row(vout);
        if (row.State is not (OutputResolutionState.Resolved or OutputResolutionState.Irrevocable))
        {
            row = row with { State = OutputResolutionState.Resolved, ResolvedHeight = height, WaitUntilHeight = null };
            var (unitOfWork, save) = Store.CreateUnitOfWork();
            await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(row);
            await save();
        }

        var actions = await CreateResolver().OnOutputSpentAsync(Close, row, spender, height,
                                                                TestContext.Current.CancellationToken);
        var spent = await ApplyAsync(actions);
        var block = await ResolveAsync(tip ?? height);
        return new ResolutionRound([.. spent.Actions, .. block.Actions], block.AllIrrevocablyResolved);
    }

    /// <summary>One of our broadcasts confirms at <paramref name="height"/>: the spend reaches the resolver.</summary>
    public Task<ResolutionRound> MineAsync(BroadcastTransactionModel ours, uint height)
    {
        var tx = ChainTxMapper.FromTransaction(Transaction.Load(ours.RawTransaction, Network.Main));
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(Close.CommitmentTransactionId, input.PreviousTxId);
        return SpendAsync(input.PreviousVout, tx, height);
    }

    /// <summary>The saved rows of the channel.</summary>
    public IReadOnlyList<OutputResolutionModel> SavedRows() =>
        Store.Outputs.Values.Where(o => o.ChannelId == Channel.ChannelId).OrderBy(o => o.OutputIndex).ToList();

    /// <summary>The row of <paramref name="vout"/> as saved.</summary>
    public OutputResolutionModel Row(uint vout) => Store.Outputs[(Close.CommitmentTransactionId, vout)];

    /// <summary>The saved row of the HTLC output of <paramref name="htlcId"/>.</summary>
    public OutputResolutionModel HtlcRow(ulong htlcId) =>
        Store.Outputs.Values.Single(o => o.HtlcId == htlcId);

    /// <summary>The saved <c>to_remote</c> row.</summary>
    public OutputResolutionModel ToRemoteRow() =>
        Store.Outputs.Values.Single(o => o.Descriptor == OutputDescriptorKind.PaymentToRemote);

    /// <summary>Our saved record of an HTLC.</summary>
    public HtlcRecord? SavedHtlc(HtlcDirection direction, ulong id) =>
        Store.Channels[Channel.ChannelId].Commitments?.GetHtlc(direction, id);

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

    /// <summary>The witness of the peer's HTLC-success transaction for <paramref name="vout"/> (the resolver never
    /// checks signatures).</summary>
    public byte[][] SuccessWitness(uint vout, Secret preimage) =>
        [[], FakeSignature(), FakeSignature(), preimage, CommitmentTx.Outputs[(int)vout].ScriptPubKey.ToBytes()];

    /// <summary>A DER-looking signature with the sighash byte.</summary>
    public static byte[] FakeSignature() => [0x30, .. Enumerable.Repeat((byte)0x01, 70)];

    /// <summary>
    /// Another channel of this node (a forward's other side): a copy of <paramref name="source"/>'s static data under
    /// <paramref name="channelId"/>, with <paramref name="state"/> as its snapshot, stored in <see cref="Store"/>.
    /// </summary>
    public ChannelModel AddChannel(ChannelModel source, ChannelId channelId, ChannelCommitments state,
                                   ChannelState channelState = ChannelState.Open)
    {
        var channel = new ChannelModel(source.ChannelParams, channelId, source.CommitmentNumber, source.FundingOutput,
                                       source.IsInitiator, null, null, LightningMoney.Satoshis(500_000),
                                       source.LocalKeySet, 0, 0, LightningMoney.Satoshis(500_000), source.RemoteKeySet,
                                       0, source.RemoteNodeId, 0, channelState, source.Version);
        channel.UpdateCommitments(WithChannelId(state, channelId));
        Store.Channels[channelId] = channel;
        return channel;
    }

    /// <summary><paramref name="state"/> as the snapshot of <paramref name="channelId"/>.</summary>
    public static ChannelCommitments WithChannelId(ChannelCommitments state, ChannelId channelId) =>
        ChannelCommitments.Restore(channelId, state.Params, state.LocalBalanceMsat, state.RemoteBalanceMsat,
                                   state.Htlcs.Values, state.FeeUpdates, state.LocalNextHtlcId, state.RemoteNextHtlcId,
                                   state.LocalCommit, state.RemoteCommit, state.RemoteNextCommit,
                                   state.RemoteNextPerCommitmentPoint);

    /// <summary>Applies one round's actions as the executor does: stage everything, save once, then act.</summary>
    private async Task<ResolutionRound> ApplyAsync(IReadOnlyList<OutputResolverAction> actions)
    {
        var (unitOfWork, save) = Store.CreateUnitOfWork();
        var newWatches = new List<WatchedOutpointModel>();
        foreach (var action in actions)
        {
            switch (action)
            {
                case UpsertOutputAction upsert:
                    await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(upsert.Output);
                    break;
                case BroadcastAction broadcast:
                    if (await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(
                            broadcast.Transaction.TransactionId) is null)
                        unitOfWork.BroadcastTransactionDbRepository.Add(broadcast.Transaction);
                    break;
                case WatchOutpointAction watch:
                    if (await unitOfWork.WatchedOutpointDbRepository.GetAsync(watch.Watch.TransactionId,
                                                                            watch.Watch.OutputIndex) is null)
                    {
                        unitOfWork.WatchedOutpointDbRepository.Add(watch.Watch);
                        newWatches.Add(watch.Watch);
                    }

                    break;
                case StageWriteAction stage:
                    await stage.Stage(unitOfWork, TestContext.Current.CancellationToken);
                    break;
            }
        }

        await save();
        Published.AddRange(actions.OfType<BroadcastAction>().Select(b => b.Transaction));
        Tracked.AddRange(newWatches);
        SwitchEvents.AddRange(actions.OfType<RaiseChannelEventAction>().Select(r => r.Event));
        Alerts.AddRange(actions.OfType<AlertAction>());
        return new ResolutionRound(actions,
                                   SavedRows().All(r => r.State is OutputResolutionState.Irrevocable
                                                               or OutputResolutionState.Ignored));
    }

    public void Dispose()
    {
        _provider.Dispose();
        Pair.Dispose();
    }
}

/// <summary>What one executor round did.</summary>
/// <param name="Actions">The resolver's actions, as applied.</param>
/// <param name="AllIrrevocablyResolved">Every row is irrevocable or ignored after the round (the channel can close).
/// </param>
internal sealed record ResolutionRound(IReadOnlyList<OutputResolverAction> Actions, bool AllIrrevocablyResolved)
{
    public IEnumerable<BroadcastTransactionModel> Broadcasts =>
        Actions.OfType<BroadcastAction>().Select(b => b.Transaction);

    public IEnumerable<IChannelDomainEvent> Events => Actions.OfType<RaiseChannelEventAction>().Select(r => r.Event);

    public IEnumerable<AlertAction> Alerts => Actions.OfType<AlertAction>();
}