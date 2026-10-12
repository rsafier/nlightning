using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Application.Channels.Close.Handlers;
using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Harness;
using Infrastructure.Bitcoin.Outputs;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="TwoNodeHarness"/> with the mutual close (BOLT2 plan N10) on both nodes: the production
/// <see cref="ChannelCloseCoordinator"/>, <see cref="IChannelCloseService"/> and the <c>shutdown</c>/
/// <c>closing_signed</c> handlers, a fixed feerate per node, a fixed P2WPKH shutdown script per node and a blockchain
/// monitor that records every closing transaction it is asked to publish.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class CloseHarness : IDisposable
{
    public static readonly BitcoinScript AliceScript = P2Wpkh(0xA1);
    public static readonly BitcoinScript BobScript = P2Wpkh(0xB0);

    private readonly Dictionary<string, List<SignedTransaction>> _published = new()
    {
        ["Alice"] = [],
        ["Bob"] = []
    };

    public TwoNodeHarness Harness { get; }

    /// <summary>The channel is a simple taproot channel (its funding output is the MuSig2 P2TR key).</summary>
    public bool IsSimpleTaproot { get; }
    public HarnessNode Alice => Harness.Alice;
    public HarnessNode Bob => Harness.Bob;

    /// <param name="aliceFeeratePerKw">Alice's fee estimate (she funds the channel).</param>
    /// <param name="bobFeeratePerKw">Bob's fee estimate.</param>
    /// <param name="aliceSendsFeeRange">Alice's <c>Node:Close:SendFeeRange</c>.</param>
    /// <param name="bobSendsFeeRange">Bob's <c>Node:Close:SendFeeRange</c>.</param>
    /// <param name="configure">More registrations per node (by name), applied last so they replace the defaults.
    /// </param>
    /// <param name="simpleClose">Both nodes negotiated <c>option_simple_close</c> (BOLT2 plan N11): the close uses
    /// <c>closing_complete</c>/<c>closing_sig</c>.</param>
    /// <param name="simpleTaproot">A simple taproot channel (NL-877 T5): MuSig2 closing signatures over the P2TR
    /// funding output.</param>
    public CloseHarness(uint aliceFeeratePerKw = 2_500, uint bobFeeratePerKw = 2_500, bool aliceSendsFeeRange = true,
                        bool bobSendsFeeRange = true, Action<string, IServiceCollection>? configure = null,
                        bool simpleClose = false, bool simpleTaproot = false)
    {
        IsSimpleTaproot = simpleTaproot;
        Harness = new TwoNodeHarness(simpleTaproot: simpleTaproot, configureServices: (node, services) =>
        {
            var isAlice = node.Name == "Alice";
            var published = _published[node.Name];

            var feeService = new Mock<IFeeService>();
            var feerate = LightningMoney.Satoshis(isAlice ? aliceFeeratePerKw : bobFeeratePerKw);
            feeService.Setup(f => f.GetCachedFeeRatePerKw()).Returns(feerate);
            feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>())).ReturnsAsync(feerate);
            services.AddSingleton(feeService.Object);

            var monitor = new Mock<IBlockchainMonitor>();
            monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(TwoNodeHarness.BlockHeight);
            monitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                   .Callback((SignedTransaction tx) =>
                    {
                        lock (published)
                            published.Add(tx);
                    })
                   .Returns(Task.CompletedTask);
            services.AddSingleton(monitor.Object);

            var script = isAlice ? AliceScript : BobScript;
            services.AddScoped<ShutdownScriptProvider>(_ => new FixedShutdownScriptProvider(script));
            services.Configure<ChannelCloseOptions>(o => o.SendFeeRange = isAlice ? aliceSendsFeeRange : bobSendsFeeRange);
            services.AddChannelCloseServices();
            services.AddScoped<IChannelMessageHandler<ShutdownMessage>, ShutdownMessageHandler>();
            services.AddScoped<IChannelMessageHandler<ClosingSignedMessage>, ClosingSignedMessageHandler>();
            services.AddScoped<IChannelMessageHandler<ClosingCompleteMessage>, ClosingCompleteMessageHandler>();
            services.AddScoped<IChannelMessageHandler<ClosingSigMessage>, ClosingSigMessageHandler>();
            configure?.Invoke(node.Name, services);
        });

        // The default negotiated features carry option_simple_close since taproot plan D-T1, so the legacy harness
        // pins it off: its cases prove the closing_signed negotiation a peer without bits 60/61 gets
        Alice.NegotiatedFeatures = simpleClose ? SimpleCloseFeatures() : LegacyCloseFeatures();
        Bob.NegotiatedFeatures = simpleClose ? SimpleCloseFeatures() : LegacyCloseFeatures();
    }

    /// <summary>Negotiated features with <c>option_simple_close</c> (and its dependency, anysegwit).</summary>
    public static FeatureOptions SimpleCloseFeatures() => new()
    {
        OptionSimpleClose = FeatureSupport.Optional,
        BeyondSegwitShutdown = FeatureSupport.Optional
    };

    /// <summary>Negotiated features without <c>option_simple_close</c>: the legacy <c>closing_signed</c> close.</summary>
    public static FeatureOptions LegacyCloseFeatures() => new() { OptionSimpleClose = FeatureSupport.No };

    public IChannelCloseService CloseService(HarnessNode node) =>
        node.Services.GetRequiredService<IChannelCloseService>();

    /// <summary>The closing transactions <paramref name="node"/> asked the monitor to publish.</summary>
    public IReadOnlyList<SignedTransaction> Published(HarnessNode node)
    {
        var published = _published[node.Name];
        lock (published)
            return published.ToList();
    }

    /// <summary>
    /// The funding output both nodes spend: the P2WSH 2-of-2, or for a simple taproot channel the BIP 86 key path of
    /// <c>KeyAgg(KeySort(both funding keys))</c>.
    /// </summary>
    public TxOut FundingTxOut()
    {
        if (!IsSimpleTaproot)
            return new FundingOutput(LightningMoney.Satoshis(TwoNodeHarness.FundingSatoshis),
                                     new PubKey(Alice.Basepoints.FundingPubKey),
                                     new PubKey(Bob.Basepoints.FundingPubKey)).ToTxOut();

        var aggregate = Alice.Services.GetRequiredService<IMusig2Service>().AggregateTaprootKeyPath(Alice.Basepoints.FundingPubKey,
                                                                    Bob.Basepoints.FundingPubKey);
        return new TxOut(Money.Satoshis(TwoNodeHarness.FundingSatoshis),
                         new Script(aggregate.GetTaprootScriptPubKey()));
    }

    /// <summary>Script execution of <paramref name="tx"/>'s only input against <see cref="FundingTxOut"/>.</summary>
    public void AssertSpendsFunding(Transaction tx)
    {
        var error = tx.CreateValidator([FundingTxOut()]).ValidateInput(0).Error;
        Assert.True(error is null, error?.ToString());
    }

    /// <summary>Asserts both nodes closed with the same, fully signed transaction that spends the funding output.
    /// </summary>
    public Transaction AssertClosedTogether()
    {
        Assert.Equal(ChannelState.Closing, Alice.Channel.State);
        Assert.Equal(ChannelState.Closing, Bob.Channel.State);
        var aliceTx = Assert.IsType<SignedTransaction>(Alice.Channel.ClosingTransaction);
        var bobTx = Assert.IsType<SignedTransaction>(Bob.Channel.ClosingTransaction);
        Assert.Equal(aliceTx.TxId, bobTx.TxId);
        Assert.Equal(aliceTx.RawTxBytes, bobTx.RawTxBytes);
        Assert.Equal(aliceTx.TxId, Assert.Single(Published(Alice)).TxId);
        Assert.Equal(bobTx.TxId, Assert.Single(Published(Bob)).TxId);

        var tx = Transaction.Load(aliceTx.RawTxBytes, Network.RegTest);
        AssertSpendsFunding(tx);
        return tx;
    }

    /// <summary>
    /// Starts the close from <paramref name="initiator"/> and delivers the messages both ways until both outboxes are
    /// empty, except the first one <paramref name="holdFrom"/> sends that matches <paramref name="hold"/>: it is taken
    /// off the link and returned, for <see cref="DeliverLateAsync"/> (NL-983).
    /// </summary>
    public async Task<IChannelMessage> CloseHoldingAsync(HarnessNode initiator, HarnessNode holdFrom,
                                                         Func<IChannelMessage, bool> hold)
    {
        await CloseService(initiator).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                        TestContext.Current.CancellationToken);
        IChannelMessage? held = null;
        for (var steps = 0; steps < 1_000; steps++)
        {
            await Alice.Scheduler.WhenIdleAsync();
            await Bob.Scheduler.WhenIdleAsync();
            var aliceSent = await DeliverOrHoldAsync(Alice);
            var bobSent = await DeliverOrHoldAsync(Bob);
            if (!aliceSent && !bobSent)
                return held ?? throw new InvalidOperationException($"{holdFrom.Name} sent no message to hold");
        }

        throw new InvalidOperationException("The message exchange did not converge");

        async Task<bool> DeliverOrHoldAsync(HarnessNode node)
        {
            if (held is null && node == holdFrom && node.PeekNext() is { } next && hold(next))
                return node.TryTakeNext(out held);

            return await node.DeliverNextAsync();
        }
    }

    /// <summary>Hands <paramref name="message"/>, sent earlier by <paramref name="from"/>, to its peer now.</summary>
    public static Task DeliverLateAsync(HarnessNode from, IChannelMessage message) =>
        from.Peer.ChannelManager.HandleChannelMessageAsync(message, from.NegotiatedFeatures, from.NodeId);

    /// <summary>
    /// <paramref name="node"/>'s chain monitor processed a block holding <paramref name="spend"/> at
    /// <paramref name="height"/>: the funding output's watch records the spend and the spend is raised, as
    /// <c>BlockchainMonitorService</c> does; returns once the channel manager handled it (its channel lock is free).
    /// </summary>
    public static async Task FundingSpentInBlockAsync(HarnessNode node, SignedTransaction spend, uint height)
    {
        var funding = node.Channel.FundingOutput!;
        var fundingTxId = funding.TransactionId!.Value;
        var fundingIndex = funding.Index!.Value;
        var blockHash = new Hash(Enumerable.Repeat((byte)height, 32).ToArray());
        var watch = new WatchedOutpointModel(fundingTxId, fundingIndex, TwoNodeHarness.ChannelId,
                                             WatchedOutpointPurpose.FundingOutput);
        watch.MarkSpent(spend.TxId, height, blockHash);
        node.WatchedOutpoints.Setup(r => r.GetAsync(fundingTxId, fundingIndex)).ReturnsAsync(watch);
        node.WatchedTransactions.Setup(r => r.GetByTransactionIdAsync(spend.TxId))
            .ReturnsAsync(() =>
             {
                 var seen = new WatchedTransactionModel(TwoNodeHarness.ChannelId, spend.TxId, 6);
                 seen.SetHeightAndIndex(height, 1);
                 return seen;
             });
        node.ChainMonitor.Raise(m => m.OnWatchedOutpointSpent += null, node.ChainMonitor.Object,
                                new OutpointSpentEventArgs(TwoNodeHarness.ChannelId, spend, height, 1, fundingTxId,
                                                           fundingIndex, blockHash));
        using (await node.Services.GetRequiredService<IChannelLockProvider>()
                         .AcquireAsync(TwoNodeHarness.ChannelId, TestContext.Current.CancellationToken))
        {
        }
    }

    /// <summary>
    /// <paramref name="txId"/>'s watch on <paramref name="node"/> reached its depth (6 blocks above
    /// <paramref name="height"/>): the confirmation is raised to the channel manager.
    /// </summary>
    public static void RaiseConfirmed(HarnessNode node, TxId txId, uint height)
    {
        var watched = new WatchedTransactionModel(TwoNodeHarness.ChannelId, txId, 6);
        watched.SetHeightAndIndex(height, 1);
        watched.MarkAsCompleted();
        node.ChainMonitor.Raise(m => m.OnTransactionConfirmed += null, node.ChainMonitor.Object,
                                new TransactionConfirmedEventArgs(watched, height + 5));
    }

    public static long OutputTo(Transaction tx, BitcoinScript script) =>
        tx.Outputs.Where(o => o.ScriptPubKey.ToBytes().SequenceEqual((byte[])script)).Sum(o => o.Value.Satoshi);

    public void Dispose() => Harness.Dispose();

    private static BitcoinScript P2Wpkh(byte fill) => new([0x00, 0x14, .. Enumerable.Repeat(fill, 20)]);

    /// <summary>A fixed shutdown script instead of a wallet address.</summary>
    private sealed class FixedShutdownScriptProvider(BitcoinScript script)
        : ShutdownScriptProvider(Options.Create(new NodeOptions()), new Mock<IBitcoinWalletService>().Object)
    {
        public override Task<BitcoinScript> GetLocalScriptAsync(ChannelModel channel) => Task.FromResult(script);
    }
}