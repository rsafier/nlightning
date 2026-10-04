using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Application.Channels.Close.Handlers;
using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.Interfaces;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
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