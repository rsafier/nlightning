using System.Text;
using Lnrpc;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Options;
using Fixtures;
using Utils;

/// <summary>
/// The ABCD topology (ABCD roadmap §3): LND Alice → NLightning Bob → NLightning Carol → LND David, built once per
/// fixture (<see cref="GetAsync"/>) and shared by every ABCD test class of the <c>regtest</c> collection. Bob and
/// Carol run in this process with their own port, key and SQLite file and the policies of <see cref="AbcdPolicy"/>.
/// </summary>
/// <remarks>
/// All three channels are funded by NLightning (so we never receive an <c>update_fee</c>, plan D9): Bob → Alice
/// 2,000,000 sat with 1,000,000 pushed to Alice, Bob → Carol 2,000,000 sat, Carol → David 2,000,000 sat, all at
/// 10,000 sat/kw. Tests assert deltas and first call <see cref="PrepareForPaymentAsync"/>, so their order does not
/// matter. Blocks are only mined here while no HTLC is in flight (while the channels confirm).
/// </remarks>
public sealed class AbcdNetwork : IAsyncDisposable
{
    public const string FixtureKey = "abcd-network";

    public static readonly LightningMoney ChannelCapacity = LightningMoney.Satoshis(2_000_000);
    public static readonly LightningMoney AlicePush = LightningMoney.Satoshis(1_000_000);
    public static readonly LightningMoney FeeRatePerKw = LightningMoney.Satoshis(10_000);

    /// <summary>
    /// How long channels get to become active (roadmap §3: 90 s).
    /// </summary>
    public static readonly TimeSpan ActiveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long the HTLCs of a finished payment get to leave every commitment.
    /// </summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_confirmPollInterval = TimeSpan.FromSeconds(1);
    private const int NodeLogTailLines = 300;

    /// <summary>
    /// The <c>to_self_delay</c> LND 0.20 asks for on a 2,000,000 sat channel.
    /// </summary>
    private const ushort LndToSelfDelayFor2MSatChannel = 240;

    private readonly LightningRegtestNetworkFixture _fixture;

    public LNDNodeConnection Alice { get; }
    public LNDNodeConnection David { get; }
    public NLightningTestNode Bob { get; }
    public NLightningTestNode Carol { get; }

    /// <summary>
    /// Every channel message Bob sent while he was running (re-attached on every start).
    /// </summary>
    public ChannelMessageRecorder BobSent { get; } = new("bob");

    /// <summary>
    /// Every channel message Carol sent while she was running (re-attached on every start).
    /// </summary>
    public ChannelMessageRecorder CarolSent { get; } = new("carol");

    public AbcdChannel AliceBob { get; private set; } = null!;
    public AbcdChannel BobCarol { get; private set; } = null!;
    public AbcdChannel CarolDavid { get; private set; } = null!;

    public IReadOnlyList<NLightningTestNode> Nodes => [Bob, Carol];

    private AbcdNetwork(LightningRegtestNetworkFixture fixture, LNDNodeConnection alice, LNDNodeConnection david,
                        NLightningTestNode bob, NLightningTestNode carol)
    {
        _fixture = fixture;
        Alice = alice;
        David = david;
        Bob = bob;
        Carol = carol;
    }

    /// <summary>
    /// The fixture's ABCD network, built by the first caller. A failed build is not cached; the next test tries
    /// again on a fresh pair of nodes.
    /// </summary>
    public static Task<AbcdNetwork> GetAsync(LightningRegtestNetworkFixture fixture,
                                             CancellationToken cancellationToken) =>
        fixture.GetOrCreateAsync(FixtureKey, () => CreateAsync(fixture, cancellationToken));

    /// <summary>
    /// The precondition of every ABCD test: both nodes running, every channel usable on both ends, no HTLC pending
    /// anywhere and every node at the chain tip.
    /// </summary>
    public async Task PrepareForPaymentAsync(CancellationToken cancellationToken)
    {
        await EnsureRunningAsync(cancellationToken);
        await WaitUntilUsableAsync(cancellationToken);
        await WaitNoPendingHtlcsAsync(cancellationToken);
        await ChainSync.WaitAllAtTipAsync(_fixture, Nodes, cancellationToken);
    }

    /// <summary>
    /// Starts a node an earlier (failed) test left stopped.
    /// </summary>
    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        foreach (var node in Nodes.Where(n => !n.IsRunning))
        {
            Console.WriteLine($"[abcd] {node.Name} was left stopped; starting it");
            await StartNodeAsync(node, cancellationToken);
        }
    }

    /// <summary>
    /// Stops <paramref name="node"/> (gracefully, or with <see cref="NLightningTestNode.CrashAsync"/>). Its key and
    /// database stay.
    /// </summary>
    public async Task StopNodeAsync(NLightningTestNode node, bool crash)
    {
        RecorderFor(node).Detach();
        if (crash)
            await node.CrashAsync();
        else
            await node.StopAsync();
    }

    /// <summary>
    /// Starts <paramref name="node"/> again on the same key and database and re-attaches its recorder. The node
    /// reconnects to its channel peers by itself.
    /// </summary>
    public async Task StartNodeAsync(NLightningTestNode node, CancellationToken cancellationToken)
    {
        await node.StartAsync(cancellationToken);
        RecorderFor(node).Attach(node);
    }

    public ChannelMessageRecorder RecorderFor(NLightningTestNode node) =>
        node == Bob ? BobSent : node == Carol ? CarolSent : throw new ArgumentException($"{node.Name} is not in ABCD");

    /// <summary>
    /// Waits until every channel is usable on both ends: ours <c>Open</c>, peer connected, reestablished and with a
    /// real SCID; LND's <c>Active</c>. Mines nothing.
    /// </summary>
    public Task WaitUntilUsableAsync(CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        WaitForAsync(() => CheckUsableAsync(cancellationToken), timeout ?? ActiveTimeout,
                     "every ABCD channel usable on both ends", cancellationToken);

    /// <summary>
    /// Waits until no HTLC is pending on any of the six channel ends.
    /// </summary>
    public Task WaitNoPendingHtlcsAsync(CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        WaitForAsync(async () =>
        {
            var snapshot = await SnapshotAsync(cancellationToken);
            return (snapshot.PendingHtlcCount == 0, snapshot.ToString());
        }, timeout ?? SettleTimeout, "no pending HTLC on any ABCD channel", cancellationToken);

    /// <summary>
    /// Every channel end as its owner lists it now.
    /// </summary>
    public async Task<AbcdSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var aliceBob = await LndTestHelpers.GetChannelByPointAsync(Alice, AliceBob.ChannelPoint, cancellationToken);
        var davidCarol = await LndTestHelpers.GetChannelByPointAsync(David, CarolDavid.ChannelPoint, cancellationToken);
        Assert.NotNull(aliceBob);
        Assert.NotNull(davidCarol);

        return new AbcdSnapshot(await Bob.GetChannelAsync(AliceBob.ChannelId, cancellationToken),
                                await Bob.GetChannelAsync(BobCarol.ChannelId, cancellationToken),
                                await Carol.GetChannelAsync(BobCarol.ChannelId, cancellationToken),
                                await Carol.GetChannelAsync(CarolDavid.ChannelId, cancellationToken),
                                aliceBob, davidCarol);
    }

    /// <summary>
    /// David's route hint through Bob and Carol: <c>[{Bob, scid(B–C), Bob's policy}, {Carol, scid(C–D), Carol's
    /// policy}]</c>.
    /// </summary>
    public RouteHint HintThroughBobAndCarol() =>
        LndTestHelpers.RouteHint(HopHintFor(Bob, BobCarol, AbcdPolicy.Bob), HopHintFor(Carol, CarolDavid, AbcdPolicy.Carol));

    /// <summary>
    /// David's route hint through Carol only: <c>[{Carol, scid(C–D), Carol's policy}]</c> (Bob pays).
    /// </summary>
    public RouteHint HintThroughCarol() => LndTestHelpers.RouteHint(HopHintFor(Carol, CarolDavid, AbcdPolicy.Carol));

    /// <summary>
    /// Alice pays <paramref name="paymentRequest"/> over A–B only, in one part, after forgetting earlier failures.
    /// Not awaiting the task leaves the payment in flight.
    /// </summary>
    public async Task<Payment> PayFromAliceAsync(string paymentRequest, CancellationToken cancellationToken,
                                                 TimeSpan? timeout = null)
    {
        await LndTestHelpers.ResetMissionControlAsync(Alice, cancellationToken);
        var request = LndTestHelpers.PinnedPayment(paymentRequest, [AliceBob.ShortChannelId],
                                                   timeoutSeconds: (int)LndTestHelpers.DefaultPaymentTimeout
                                                                                      .TotalSeconds);
        return await LndTestHelpers.SendPaymentV2Async(Alice, request, cancellationToken, timeout);
    }

    /// <summary>
    /// Whether every ABCD connection is up on both ends (Alice–Bob, Bob–Carol, Carol–David).
    /// </summary>
    public async Task<bool> AllPeersConnectedAsync(CancellationToken cancellationToken) =>
        Bob.IsConnectedTo(Alice.LocalNodePubKeyBytes)
     && Bob.IsConnectedTo(Carol.NodeId)
     && Carol.IsConnectedTo(Bob.NodeId)
     && Carol.IsConnectedTo(David.LocalNodePubKeyBytes)
     && await LndTestHelpers.IsConnectedToAsync(Alice, Bob.NodeIdHex, cancellationToken)
     && await LndTestHelpers.IsConnectedToAsync(David, Carol.NodeIdHex, cancellationToken);

    /// <summary>
    /// Writes what a failed test needs: every channel end, the tail of both nodes' logs and the <c>alice</c> and
    /// <c>david</c> container logs.
    /// </summary>
    public async Task DumpDiagnosticsAsync()
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var (_, status) = await CheckUsableAsync(timeoutCts.Token);
            Console.WriteLine($"===== ABCD channels: {status} =====");
        }
        catch (Exception e)
        {
            Console.WriteLine($"===== ABCD channels: unavailable ({e.Message}) =====");
        }

        foreach (var node in Nodes)
        {
            Console.WriteLine($"===== {node} log (last {NodeLogTailLines} lines, running={node.IsRunning}) =====");
            foreach (var line in node.NodeLog.TakeLast(NodeLogTailLines))
                Console.WriteLine(line);
        }

        await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);
    }

    public async ValueTask DisposeAsync()
    {
        BobSent.Dispose();
        CarolSent.Dispose();
        await Bob.DisposeAsync();
        await Carol.DisposeAsync();
    }

    /// <summary>
    /// Polls <paramref name="check"/> until it reports ready.
    /// </summary>
    /// <exception cref="TimeoutException">Not ready in time; the message carries the last status.</exception>
    public static async Task WaitForAsync(Func<Task<(bool Ready, string Status)>> check, TimeSpan timeout,
                                          string description, CancellationToken cancellationToken,
                                          Func<Task>? betweenPolls = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var (ready, status) = await check();
            if (ready)
                return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out after {timeout} waiting for: {description}. Last: {status}");

            if (betweenPolls is not null)
                await betweenPolls();
            else
                await Task.Delay(s_pollInterval, cancellationToken);
        }
    }

    private static async Task<AbcdNetwork> CreateAsync(LightningRegtestNetworkFixture fixture,
                                                       CancellationToken cancellationToken)
    {
        var alice = fixture.GetLndNode("alice");
        var david = fixture.GetLndNode("david");
        foreach (var lnd in new[] { alice, david })
            Console.WriteLine($"[abcd] LND {lnd.LocalAlias}: {await LndTestHelpers.GetVersionAsync(lnd, cancellationToken)}");

        var bob = await NLightningTestNode.CreateAsync(fixture, "bob",
                                                       configureNodeOptions: o => Configure(o, AbcdPolicy.Bob));
        var carol = await NLightningTestNode.CreateAsync(fixture, "carol",
                                                         configureNodeOptions: o => Configure(o, AbcdPolicy.Carol));
        var network = new AbcdNetwork(fixture, alice, david, bob, carol);
        try
        {
            await network.BuildAsync(cancellationToken);
            return network;
        }
        catch
        {
            await network.DumpDiagnosticsAsync();
            await network.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The node options of Bob and Carol: their forwarding policy, and a <c>to_self_delay</c> that lets them accept
    /// LND's. LND 0.20 scales the delay it asks for with the capacity (240 blocks for a 2,000,000 sat channel), and
    /// our open validator refuses more than 1.5 × our own (1.5 × 144 = 216 by default).
    /// </summary>
    private static void Configure(NodeOptions options, AbcdPolicy policy)
    {
        policy.ApplyTo(options.Routing);
        options.ToSelfDelay = LndToSelfDelayFor2MSatChannel;
    }

    private async Task BuildAsync(CancellationToken cancellationToken)
    {
        await StartNodeAsync(Bob, cancellationToken);
        await StartNodeAsync(Carol, cancellationToken);
        Console.WriteLine($"[abcd] bob {Bob}, carol {Carol}");
        await ChainSync.WaitAllAtTipAsync(_fixture, Nodes, cancellationToken);

        // Bob funds two channels: two coins, so the second open never waits for the first one's change
        await Bob.FundWalletAsync(LightningMoney.Satoshis(2_500_000), AddressType.P2Wpkh, cancellationToken);
        await Bob.FundWalletAsync(LightningMoney.Satoshis(2_500_000), AddressType.P2Wpkh, cancellationToken);
        await Carol.FundWalletAsync(LightningMoney.Satoshis(2_500_000), AddressType.P2Wpkh, cancellationToken);
        await ChainSync.WaitAllAtTipAsync(_fixture, Nodes, cancellationToken);

        var aliceAddress = await Bob.ConnectToAsync(Alice, cancellationToken);
        await Bob.ConnectToAsync(Carol, cancellationToken);
        var davidAddress = await Carol.ConnectToAsync(David, cancellationToken);

        AliceBob = await OpenAsync("A-B", Bob, aliceAddress, AlicePush, cancellationToken);
        BobCarol = await OpenAsync("B-C", Bob, Carol.Address, null, cancellationToken);
        CarolDavid = await OpenAsync("C-D", Carol, davidAddress, null, cancellationToken);

        // Mine one block at a time (nothing is in flight yet) until LND and we agree every channel is usable
        await WaitForAsync(() => CheckUsableAsync(cancellationToken), ActiveTimeout,
                           "every ABCD channel usable on both ends after opening", cancellationToken,
                           async () =>
                           {
                               await ChainSync.MineAndWaitAsync(_fixture, 1, _fixture.LndNodes, Nodes,
                                                                cancellationToken);
                               await Task.Delay(s_confirmPollInterval, cancellationToken);
                           });

        foreach (var (channel, owner) in new[] { (AliceBob, Bob), (BobCarol, Bob), (CarolDavid, Carol) })
        {
            var ours = await owner.GetChannelAsync(channel.ChannelId, cancellationToken);
            Assert.NotNull(ours.ShortChannelId);
            channel.ShortChannelId = ours.ShortChannelId.Value.ToUInt64();
        }

        // The SCIDs we put in route hints must be the ones LND knows the channels by
        var aliceChannel = await LndTestHelpers.GetChannelByPointAsync(Alice, AliceBob.ChannelPoint, cancellationToken);
        var davidChannel = await LndTestHelpers.GetChannelByPointAsync(David, CarolDavid.ChannelPoint,
                                                                       cancellationToken);
        Assert.NotNull(aliceChannel);
        Assert.NotNull(davidChannel);
        Assert.Equal(aliceChannel.ChanId, AliceBob.ShortChannelId);
        Assert.Equal(davidChannel.ChanId, CarolDavid.ShortChannelId);
        Assert.Equal((await Carol.GetChannelAsync(BobCarol.ChannelId, cancellationToken)).ShortChannelId?.ToUInt64(),
                     BobCarol.ShortChannelId);

        await ChainSync.WaitAllAtTipAsync(_fixture, Nodes, cancellationToken);
        Console.WriteLine($"[abcd] network ready: {AliceBob}; {BobCarol}; {CarolDavid}");
    }

    private static async Task<AbcdChannel> OpenAsync(string name, NLightningTestNode funder, string peerAddress,
                                                      LightningMoney? push, CancellationToken cancellationToken)
    {
        var channel = await funder.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, ChannelCapacity)
        {
            PushAmount = push,
            FeeRatePerKw = FeeRatePerKw
        }, cancellationToken);
        var abcdChannel = new AbcdChannel(name, channel.ChannelId, channel.ChannelPoint());
        Console.WriteLine($"[abcd] opened {abcdChannel} from {funder.Name}, state {channel.ChannelState}");
        return abcdChannel;
    }

    private async Task<(bool Ready, string Status)> CheckUsableAsync(CancellationToken cancellationToken)
    {
        var ready = true;
        var status = new StringBuilder();
        foreach (var (node, channel) in new[] { (Bob, AliceBob), (Bob, BobCarol), (Carol, BobCarol), (Carol, CarolDavid) })
        {
            if (channel is null)
            {
                ready = false;
                continue;
            }

            status.Append(node.Name).Append(' ').Append(channel.Name).Append(": ");
            if (!node.IsRunning)
            {
                ready = false;
                status.Append("stopped; ");
                continue;
            }

            var ours = (await node.ListChannelsAsync(cancellationToken)).Channels
                                                                        .FirstOrDefault(c => c.ChannelId
                                                                                          == channel.ChannelId);
            ready &= ours is not null && ours.IsUsable() && ours.ShortChannelId is not null;
            status.Append(ours?.Describe() ?? "not listed").Append("; ");
        }

        foreach (var (lnd, channel) in new[] { (Alice, AliceBob), (David, CarolDavid) })
        {
            if (channel is null)
            {
                ready = false;
                continue;
            }

            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channel.ChannelPoint, cancellationToken);
            ready &= theirs is { Active: true };
            status.Append(lnd.LocalAlias).Append(' ').Append(channel.Name).Append(": ")
                  .Append(theirs is null
                              ? "not listed"
                              : $"active={theirs.Active} local={theirs.LocalBalance} remote={theirs.RemoteBalance} "
                              + $"pending={theirs.PendingHtlcs.Count}")
                  .Append("; ");
        }

        return (ready, status.ToString());
    }

    private static HopHint HopHintFor(NLightningTestNode node, AbcdChannel channel, AbcdPolicy policy) =>
        LndTestHelpers.HopHint(node.NodeIdHex, channel.ShortChannelId, policy.FeeBaseMsat,
                               policy.FeeProportionalMillionths, policy.CltvExpiryDelta);
}

/// <summary>
/// One ABCD channel: our channel id, LND's <c>txid:index</c> channel point and (once confirmed) the real SCID, which
/// is also LND's <c>chan_id</c>.
/// </summary>
public sealed class AbcdChannel(string name, ChannelId channelId, string channelPoint)
{
    public string Name { get; } = name;
    public ChannelId ChannelId { get; } = channelId;
    public string ChannelPoint { get; } = channelPoint;
    public ulong ShortChannelId { get; internal set; }

    public override string ToString() => $"{Name} {ChannelId} ({ChannelPoint}, scid {ShortChannelId})";
}

/// <summary>
/// Every ABCD channel end at one moment: ours through <c>listchannels</c> (msat, gross balances), LND's through
/// <c>ListChannels</c> (sat).
/// </summary>
public sealed record AbcdSnapshot(ChannelInfoClientResponse BobAliceBob, ChannelInfoClientResponse BobBobCarol,
                                  ChannelInfoClientResponse CarolBobCarol, ChannelInfoClientResponse CarolCarolDavid,
                                  Channel AliceAliceBob, Channel DavidCarolDavid)
{
    public int PendingHtlcCount =>
        new[] { BobAliceBob, BobBobCarol, CarolBobCarol, CarolCarolDavid }.Sum(c => c.OfferedHtlcCount
                                                                                   + c.ReceivedHtlcCount)
      + AliceAliceBob.PendingHtlcs.Count + DavidCarolDavid.PendingHtlcs.Count;

    public override string ToString() =>
        $"bob A-B {BobAliceBob.Describe()}; bob B-C {BobBobCarol.Describe()}; carol B-C {CarolBobCarol.Describe()}; "
      + $"carol C-D {CarolCarolDavid.Describe()}; alice A-B local={AliceAliceBob.LocalBalance} "
      + $"pending={AliceAliceBob.PendingHtlcs.Count}; david C-D local={DavidCarolDavid.LocalBalance} "
      + $"pending={DavidCarolDavid.PendingHtlcs.Count}";
}