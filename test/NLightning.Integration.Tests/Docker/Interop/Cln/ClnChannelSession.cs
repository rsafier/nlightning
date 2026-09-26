using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.ValueObjects;
using Fixtures;
using Utils;

/// <summary>
/// The CLN interop topology, built once per <see cref="ClnFixture"/>: our node <c>nltg</c> with a channel it funded to
/// CLN (<see cref="Capacity"/>, <see cref="Push"/> pushed so CLN can pay us from the start). Test classes get it
/// through <see cref="GetAsync"/> and call <see cref="PrepareAsync"/> before each test.
/// </summary>
/// <remarks>
/// A failed build is cached (<see cref="OnceOnlyBuild{T}"/>): every later test fails fast with the original error
/// instead of opening more channels.
/// </remarks>
public sealed class ClnChannelSession : IAsyncDisposable
{
    public static readonly LightningMoney Capacity = LightningMoney.Satoshis(1_000_000);
    public static readonly LightningMoney Push = LightningMoney.Satoshis(300_000);

    /// <summary>
    /// The feerate of the channel we fund, our default: 10 sat/vB (the test node's fixed fee answer) x 250 = sat/kw.
    /// </summary>
    public static readonly LightningMoney OpenFeeRatePerKw = LightningMoney.Satoshis(2_500);

    public static readonly TimeSpan UsableTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The limit of the shared build (fund, open, confirm), which runs on its own token (see <see cref="GetAsync"/>).
    /// </summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    private const string CacheKey = "cln-channel-session";

    private readonly ClnFixture _fixture;

    private ClnChannelSession(ClnFixture fixture, NLightningTestNode node)
    {
        _fixture = fixture;
        Node = node;
        Sent = new ChannelMessageRecorder(node.Name);
    }

    public NLightningTestNode Node { get; }

    /// <summary>
    /// What <see cref="Node"/> sent through <c>OnResponseMessageReady</c>; re-attached by <see cref="StartNodeAsync"/>.
    /// </summary>
    public ChannelMessageRecorder Sent { get; }

    public ChannelId ChannelId { get; private set; }

    public string ChannelIdHex => ChannelId.ToString();

    public ClnClient Cln => _fixture.Cln;

    public string ClnNodeId => _fixture.ClnNodeId;

    public CompactPubKey ClnPubKey => Convert.FromHexString(_fixture.ClnNodeId);

    /// <summary>
    /// The shared channel, built by the first caller. The build runs on its own token (<see cref="BuildTimeout"/>), not
    /// the caller's: its outcome is cached for every test, so the first test's timeout or cancellation must not become
    /// the cached failure. The caller's token only stops the caller's wait.
    /// </summary>
    public static Task<ClnChannelSession> GetAsync(ClnFixture fixture, CancellationToken cancellationToken) =>
        GetOrBuildDetachedAsync(factory => fixture.GetOrCreateAsync(CacheKey, factory),
                                ct => BuildAsync(fixture, ct), BuildTimeout, "CLN interop channel",
                                cancellationToken);

    /// <summary>
    /// Returns the cached build (<paramref name="getOrCreate"/>), running <paramref name="build"/> once on a token of
    /// its own that is cancelled only after <paramref name="timeout"/>. <paramref name="cancellationToken"/> stops only
    /// this caller's wait, never the shared build.
    /// </summary>
    internal static async Task<T> GetOrBuildDetachedAsync<T>(
        Func<Func<Task<OnceOnlyBuild<T>>>, Task<OnceOnlyBuild<T>>> getOrCreate,
        Func<CancellationToken, Task<T>> build, TimeSpan timeout, string what, CancellationToken cancellationToken)
        where T : class, IAsyncDisposable
    {
        var result = await getOrCreate(() => OnceOnlyBuild<T>.RunAsync(async () =>
                                           {
                                               using var cts = new CancellationTokenSource(timeout);
                                               return await build(cts.Token);
                                           }))
                        .WaitAsync(cancellationToken);
        return result.GetOrThrow(what);
    }

    /// <summary>
    /// Starts <see cref="Node"/> if a failed test left it stopped, then waits until the channel is usable on both
    /// ends with no HTLC in flight.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (!Node.IsRunning)
            await StartNodeAsync(cancellationToken);

        await WaitUsableAsync(cancellationToken, requireNoHtlcs: true);
    }

    public async Task StartNodeAsync(CancellationToken cancellationToken)
    {
        await Node.StartAsync(cancellationToken);
        Sent.Attach(Node);
    }

    public async Task StopNodeAsync()
    {
        Sent.Detach();
        await Node.StopAsync();
    }

    /// <summary>
    /// Waits until our end is usable (Open, peer connected, reestablished) and CLN's end is <c>CHANNELD_NORMAL</c>
    /// with the peer connected.
    /// </summary>
    public async Task WaitUsableAsync(CancellationToken cancellationToken, bool requireNoHtlcs = false)
    {
        var status = string.Empty;
        try
        {
            await Poll.UntilAsync(async () =>
            {
                var (ready, s) = await CheckUsableAsync(requireNoHtlcs, cancellationToken);
                status = s;
                return ready;
            }, UsableTimeout, "the CLN channel usable on both ends", cancellationToken,
                                  TimeSpan.FromMilliseconds(500));
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}. Last status: {status}", e);
        }
    }

    public async Task<ChannelInfoClientResponse> GetOurChannelAsync(CancellationToken cancellationToken) =>
        await Node.GetChannelAsync(ChannelId, cancellationToken);

    public async Task<JsonNode> GetClnChannelAsync(CancellationToken cancellationToken) =>
        await Cln.GetPeerChannelAsync(Node.NodeIdHex, ChannelIdHex, cancellationToken)
     ?? throw new InvalidOperationException($"CLN does not list the channel {ChannelIdHex}");

    /// <summary>
    /// One line describing both ends (for failure messages and the output).
    /// </summary>
    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var ours = Node.IsRunning
                       ? (await Node.ListChannelsAsync(cancellationToken)).Channels
                                                                           .FirstOrDefault(c => c.ChannelId
                                                                                             == ChannelId)
                                                                          ?.Describe() ?? "not listed"
                       : "node stopped";
        string theirs;
        try
        {
            theirs = await Cln.GetPeerChannelAsync(Node.NodeIdHex, ChannelIdHex, cancellationToken) is { } c
                         ? DescribeCln(c)
                         : "not listed";
        }
        catch (Exception e)
        {
            theirs = $"unavailable: {e.Message}";
        }

        return $"ours {ours}; cln {theirs}";
    }

    public static string DescribeCln(JsonNode channel) =>
        $"{channel["state"]} connected={channel["peer_connected"]} scid={channel["short_channel_id"]} "
      + $"to_us={channel["to_us_msat"]} total={channel["total_msat"]} htlcs={channel["htlcs"]?.AsArray().Count} "
      + $"feerate={channel["feerate"]?["perkw"]} type={channel["channel_type"]?["names"]?.ToJsonString()}";

    public async ValueTask DisposeAsync()
    {
        Sent.Dispose();
        await Node.DisposeAsync();
    }

    private async Task<(bool Ready, string Status)> CheckUsableAsync(bool requireNoHtlcs,
                                                                     CancellationToken cancellationToken)
    {
        if (!Node.IsRunning)
            return (false, "node stopped");

        var ours = (await Node.ListChannelsAsync(cancellationToken)).Channels
                                                                    .FirstOrDefault(c => c.ChannelId == ChannelId);
        var theirs = await Cln.GetPeerChannelAsync(Node.NodeIdHex, ChannelIdHex, cancellationToken);
        var ready = ours is not null
                 && ours.IsUsable()
                 && ours.ShortChannelId is not null
                 && theirs?["state"]?.GetValue<string>() == "CHANNELD_NORMAL"
                 && theirs["peer_connected"]?.GetValue<bool>() == true;
        if (requireNoHtlcs)
            ready &= ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } && theirs?["htlcs"]?.AsArray().Count == 0;

        return (ready, $"ours {ours?.Describe() ?? "not listed"}; cln {(theirs is null ? "not listed" : DescribeCln(theirs))}");
    }

    /// <summary>
    /// A separate node <paramref name="nodeName"/> to which CLN opens a private channel of
    /// <paramref name="capacity"/> (CLN funds it from its own wallet, at <paramref name="clnFeerate"/>, e.g.
    /// <c>opening</c> for CLN's own estimate or <c>10000perkw</c>), followed until both ends are usable. The caller disposes the session (and so the node).
    /// </summary>
    public static async Task<ClnChannelSession> BuildClnFundedAsync(ClnFixture fixture, string nodeName,
                                                                    LightningMoney capacity, string clnFeerate,
                                                                    CancellationToken cancellationToken)
    {
        var node = await NLightningTestNode.CreateAsync(fixture.Bitcoin, nodeName);
        var session = new ClnChannelSession(fixture, node);
        try
        {
            await session.StartNodeAsync(cancellationToken);
            await fixture.FundClnWalletAsync(LightningMoney.Satoshis(capacity.Satoshi * 2), [node], cancellationToken);
            await session.ConnectAsync(cancellationToken);

            var funded = await fixture.Cln.CallAsync("fundchannel", cancellationToken, ("id", node.NodeIdHex),
                                                     ("amount", capacity.Satoshi), ("announce", false),
                                                     ("feerate", clnFeerate));
            session.ChannelId = Convert.FromHexString(funded["channel_id"]!.GetValue<string>());
            Console.WriteLine($"[cln] CLN opened {session.ChannelId} to {nodeName}: {funded.ToJsonString()}");

            await session.MineUntilUsableAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static async Task<ClnChannelSession> BuildAsync(ClnFixture fixture, CancellationToken cancellationToken)
    {
        var node = await NLightningTestNode.CreateAsync(fixture.Bitcoin, "nltg");
        var session = new ClnChannelSession(fixture, node);
        try
        {
            await session.StartNodeAsync(cancellationToken);
            await node.FundWalletAsync(LightningMoney.Satoshis(2_500_000), AddressType.P2Wpkh, cancellationToken);
            await fixture.WaitAllAtTipAsync([node], cancellationToken);
            await session.ConnectAsync(cancellationToken);

            // No explicit feerate: our estimate, the test node's fixed fee answer (fastestFee 10 sat/vB) as sat/kw
            // (OpenFeeRatePerKw), inside CLN's acceptable range (NL-288)
            var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(fixture.ClnAddress, Capacity)
            {
                PushAmount = Push
            }, cancellationToken);
            session.ChannelId = channel.ChannelId;
            Console.WriteLine($"[cln] opened {channel.ChannelId} ({channel.ChannelPoint()}), state {channel.ChannelState}");

            await session.MineUntilUsableAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnAddress))
                  .WaitAsync(cancellationToken);
        await Poll.UntilAsync(async () => Node.IsConnectedTo(ClnPubKey)
                                       && await Cln.IsConnectedAsync(Node.NodeIdHex, cancellationToken),
                              TimeSpan.FromSeconds(30), $"{Node.Name} and CLN connected", cancellationToken);
    }

    /// <summary>
    /// Mines one block at a time (nothing is in flight yet) until both ends agree the channel is usable.
    /// </summary>
    private async Task MineUntilUsableAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + UsableTimeout;
        while (true)
        {
            var (ready, status) = await CheckUsableAsync(true, cancellationToken);
            if (ready)
                break;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The CLN channel was not usable on both ends in time: {status}");

            await _fixture.MineAndWaitAsync(1, [Node], cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        Console.WriteLine($"[cln] channel ready: {await DescribeAsync(cancellationToken)}");
    }
}