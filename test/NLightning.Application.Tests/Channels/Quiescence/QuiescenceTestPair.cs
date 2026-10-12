using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Quiescence;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Quiescence;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Harness;

/// <summary>
/// Two nodes of <see cref="TwoNodeHarness"/> with the production quiescence services (<c>AddQuiescenceServices</c>),
/// <c>option_quiesce</c> advertised, a manual clock, a recording disconnector and a switch that fulfills the HTLCs it
/// knows a preimage of (refusals are recorded, the replay after the quiescence retries them, as <c>HtlcSwitch</c>
/// does).
/// </summary>
/// <remarks>
/// Every message is delivered in wire order through the receiver's <see cref="ChannelManager"/>, whose <c>stfu</c> case
/// takes the channel's lock and dispatches it to the production <see cref="StfuMessageHandler"/>, as the peer manager
/// does (NL-470).
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class QuiescenceTestPair : IDisposable
{
    public const uint CltvExpiry = 700;

    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    public TwoNodeHarness Harness { get; }
    public ManualTimeProvider Clock { get; } = new();

    /// <summary>The preimages the nodes' switches know (payment hash → preimage), shared by both.</summary>
    public ConcurrentDictionary<Hash, Secret> Preimages { get; } = new();

    public HarnessNode Alice => Harness.Alice;
    public HarnessNode Bob => Harness.Bob;

    /// <summary>Warnings a node raised for a routed <c>stfu</c> (the stfu handler's exceptions).</summary>
    public List<(string Node, ChannelWarningException Warning)> StfuWarnings { get; } = [];

    /// <summary>Connections closed by the timeout monitor: (node, peer, channel, reason).</summary>
    public ConcurrentQueue<(string Node, CompactPubKey Peer, ChannelId ChannelId, string Reason)> Disconnects { get; } =
        new();

    /// <summary>Per node, the stfu it sent and whether its own updates were all committed at that moment.</summary>
    public ConcurrentQueue<(string Node, bool Initiator, bool OwnUpdatesPending)> StfuSent { get; } = new();

    /// <summary>Per node, whether its switch should hold (not fulfill) the HTLCs it gets.</summary>
    public ConcurrentDictionary<string, bool> HoldFulfills { get; } = new();

    /// <summary>Quiescence ends the services raised: (node, its peer, channel, reason).</summary>
    public ConcurrentQueue<(string Node, CompactPubKey Peer, ChannelId ChannelId, QuiescenceEndReason Reason)> Ends
    { get; } = new();

    /// <summary>Refusals the switches got when they tried to fulfill: (node, htlc id, exception).</summary>
    public ConcurrentQueue<(string Node, ulong HtlcId, CommitmentRefusedException Refusal)> FulfillRefusals { get; } =
        new();

    private readonly Action<HarnessNode, IServiceCollection>? _configureNode;

    /// <param name="hasAnchors">Open an anchors channel.</param>
    /// <param name="configureNode">Extra services for each node, added after the kit's own.</param>
    public QuiescenceTestPair(bool hasAnchors = false, Action<HarnessNode, IServiceCollection>? configureNode = null)
    {
        _configureNode = configureNode;
        Harness = new TwoNodeHarness(hasAnchors, configureServices: Configure);
        foreach (var node in new[] { Alice, Bob })
        {
            var name = node.Name;
            var nodeRef = node;
            node.ChannelManager.OnResponseMessageReady += (_, args) =>
            {
                if (args.ResponseMessage is StfuMessage stfu)
                    StfuSent.Enqueue((name, stfu.Payload.Initiator,
                                      QuiescenceRules.HasPendingLocalUpdates(nodeRef.State)));
            };
            Quiescence(nodeRef).QuiescenceEnded += (_, args) =>
                Ends.Enqueue((name, args.PeerPubKey, args.ChannelId, args.Reason));
        }
    }

    public static FeatureSet QuiesceFeatures
    {
        get
        {
            var features = new FeatureSet();
            features.SetFeature(Feature.OptionQuiesce, false);
            return features;
        }
    }

    public QuiescenceService Quiescence(HarnessNode node) => node.Services.GetRequiredService<QuiescenceService>();

    public QuiescenceTimeoutMonitor Monitor(HarnessNode node) =>
        node.Services.GetRequiredService<QuiescenceTimeoutMonitor>();

    public QuiescenceState State(HarnessNode node) => Quiescence(node).GetState(TwoNodeHarness.ChannelId);

    /// <summary>Offers an HTLC whose preimage the peer's switch knows.</summary>
    public Task<ulong> OfferAsync(HarnessNode node, ulong amountMsat, int tag)
    {
        var preimage = TwoNodeHarness.Preimage(tag);
        var hash = TwoNodeHarness.Hash(preimage);
        Preimages[hash] = preimage;
        return node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.MilliSatoshis(amountMsat), hash,
                                              CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                              TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Delivers every queued message (both ways, one each in turn) and waits for the commit schedulers, the stfu
    /// releases and the replays, until nothing moves.
    /// </summary>
    public async Task PumpAsync()
    {
        for (var steps = 0; steps < 10_000; steps++)
        {
            await WhenIdleAsync();
            var aliceSent = await DeliverNextAsync(Alice);
            var bobSent = await DeliverNextAsync(Bob);
            if (aliceSent || bobSent)
                continue;

            await WhenIdleAsync();
            if (Alice.OutboxIsEmpty && Bob.OutboxIsEmpty)
                return;
        }

        throw new InvalidOperationException("The message exchange did not converge");
    }

    /// <summary>
    /// Hands the oldest message <paramref name="from"/> queued to the peer's channel manager, stfu included: the
    /// manager's case locks the channel and dispatches it to the receiver's <see cref="StfuMessageHandler"/>, as the
    /// peer manager does.
    /// </summary>
    public async Task<bool> DeliverNextAsync(HarnessNode from)
    {
        var isStfu = from.PeekNext() is StfuMessage;
        try
        {
            return await from.DeliverNextAsync();
        }
        catch (ChannelWarningException warning) when (isStfu)
        {
            // The production peer manager turns the stfu handler's warning into a warning message and closes the
            // connection; the tests record it
            StfuWarnings.Add((from.Peer.Name, warning));
            return true;
        }
    }

    /// <summary>Ends the quiescence on both nodes, under each channel lock (the dependent protocol's end).</summary>
    public async Task TerminateAsync(QuiescenceEndReason reason)
    {
        foreach (var node in new[] { Alice, Bob })
        {
            var lockProvider = node.Services.GetRequiredService<IChannelLockProvider>();
            using (await lockProvider.AcquireAsync(TwoNodeHarness.ChannelId, TestContext.Current.CancellationToken))
                Quiescence(node).Terminate(TwoNodeHarness.ChannelId, reason);
        }
    }

    /// <summary>
    /// The link drops, as <see cref="TwoNodeHarness.DisconnectAsync"/> does: both channel managers tell their
    /// quiescence service (Q-R-04 through <c>ChannelManager.OnPeerDisconnectedAsync</c>, NL-470).
    /// </summary>
    public async Task DisconnectAsync() => await Harness.DisconnectAsync();

    public async Task WhenIdleAsync()
    {
        for (var round = 0; round < 3; round++)
        {
            await Alice.Scheduler.WhenIdleAsync();
            await Bob.Scheduler.WhenIdleAsync();
            await Quiescence(Alice).WhenIdleAsync();
            await Quiescence(Bob).WhenIdleAsync();
        }
    }

    public void Dispose() => Harness.Dispose();

    private void Configure(HarnessNode node, IServiceCollection services)
    {
        var options = new NodeOptions { EnableHtlcs = true };
        options.Features.OptionQuiesce = FeatureSupport.Optional;
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddQuiescenceServices();
        services.AddScoped<IChannelMessageHandler<StfuMessage>, StfuMessageHandler>();
        services.AddSingleton<IQuiescencePeerDisconnector>(new RecordingDisconnector(node.Name, Disconnects));
        services.AddSingleton<IHtlcSwitch>(sp => new FulfillingSwitch(node, sp, this));
        _configureNode?.Invoke(node, services);
    }

    private sealed class RecordingDisconnector(
        string node,
        ConcurrentQueue<(string, CompactPubKey, ChannelId, string)> disconnects) : IQuiescencePeerDisconnector
    {
        public void Disconnect(CompactPubKey peerPubKey, ChannelId channelId, string reason) =>
            disconnects.Enqueue((node, peerPubKey, channelId, reason));
    }

    /// <summary>
    /// Fulfills a locked-in incoming HTLC whose preimage is known (unless the node holds), records every event, and
    /// only records a refusal (the HTLC stays locked in; the replay of the channel's pending events retries it).
    /// </summary>
    private sealed class FulfillingSwitch(HarnessNode node, IServiceProvider services, QuiescenceTestPair pair)
        : IHtlcSwitch
    {
        public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
        {
            lock (node.Events)
                node.Events.Add(channelEvent);

            if (channelEvent is not IncomingHtlcLockedIn lockedIn
             || pair.HoldFulfills.GetValueOrDefault(node.Name)
             || !pair.Preimages.TryGetValue(lockedIn.Htlc.PaymentHash, out var preimage))
                return;

            // Idempotent like the real switch: only an HTLC still locked in and not being removed
            if (node.State.GetHtlc(HtlcDirection.Incoming, lockedIn.HtlcId) is not
                { State: HtlcState.RcvdAddAckRevocation })
                return;

            try
            {
                await services.GetRequiredService<IChannelOperations>()
                              .FulfillHtlcAsync(lockedIn.ChannelId, lockedIn.HtlcId, preimage, cancellationToken);
            }
            catch (CommitmentRefusedException refusal)
            {
                pair.FulfillRefusals.Enqueue((node.Name, lockedIn.HtlcId, refusal));
            }
        }
    }
}

/// <summary>A clock that only <see cref="Advance"/> moves; its timers never fire (tests call the checks).</summary>
[ExcludeFromCodeCoverage]
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new NeverTimer();

    private sealed class NeverTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}