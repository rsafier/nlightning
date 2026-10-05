using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Harness;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Quiescence;
using Application.Channels.Splicing;
using Application.Channels.Splicing.Handlers;
using Application.Channels.Splicing.Interfaces;
using Application.InteractiveTx;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin.InteractiveTx;
using InteractiveTx.TestDoubles;
using Splicing;

/// <summary>
/// The splicing services of a <see cref="SwitchNode"/> (opt-in, <see cref="ThreeNodeHarness.CreateAsync"/>'s
/// <c>splicing</c>): as <see cref="SpliceHarness"/> registers them in its real-engine mode, on the node's own SQLite
/// database (the production <c>EngineSpliceStatePort</c>, <c>ChannelFundings</c> and <c>InteractiveTxSessions</c>
/// repositories and <c>LocalLightningSigner</c>): quiescence, the interactive-tx driver with the real Appendix G
/// builder, <see cref="SpliceService"/>, and the <c>stfu</c>/<c>tx_*</c>/<c>splice_*</c> handlers. Wallet inputs come
/// from <see cref="Contributor"/>, kept across restarts.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SwitchNodeSplicing
{
    public FakeInteractiveTxContributor Contributor { get; } = new();
    public FakePrevTxInspector Inspector { get; } = new();
    public FakeSpliceOutDestination Destination { get; } = new();

    /// <summary>A confirmed wallet output of <paramref name="satoshis"/> the node can splice in.</summary>
    public WalletUtxo Fund(long satoshis)
    {
        var utxo = WalletUtxo.Create(satoshis);
        Contributor.Utxos.Add(utxo);
        return utxo;
    }

    public void Configure(IServiceCollection services)
    {
        services.AddQuiescenceServices();
        services.AddSingleton<IInteractiveTxBuilder, InteractiveTxBuilder>();
        services.AddSingleton<IPrevTxInspector>(Inspector);
        services.AddSingleton<IInteractiveTxContributor>(Contributor);
        services.AddInteractiveTxServices();
        services.AddSingleton<ISpliceOutDestination>(Destination);
        services.AddSpliceServices();

        services.AddScoped<IChannelMessageHandler<StfuMessage>, StfuMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAddInputMessage>, TxAddInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAddOutputMessage>, TxAddOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveInputMessage>, TxRemoveInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveOutputMessage>, TxRemoveOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxCompleteMessage>, TxCompleteMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxSignaturesMessage>, TxSignaturesMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxInitRbfMessage>, TxInitRbfMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAckRbfMessage>, TxAckRbfMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAbortMessage>, TxAbortMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceInitMessage>, SpliceInitMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceAckMessage>, SpliceAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceLockedMessage>, SpliceLockedMessageHandler>();
    }

    /// <summary>Completes once the node's quiescence and splice work started off a message is done.</summary>
    public static async Task WhenIdleAsync(IServiceProvider services)
    {
        await services.GetRequiredService<QuiescenceService>().WhenIdleAsync();
        await services.GetRequiredService<SpliceService>().WhenIdleAsync();
        await services.GetRequiredService<SpliceDepthWatcher>().WhenIdleAsync();
    }
}