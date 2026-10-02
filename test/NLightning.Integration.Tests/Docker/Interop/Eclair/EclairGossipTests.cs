using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Abcd;
using Application.Gossip.Sync;
using Application.Gossip.Sync.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Fixtures;
using Gossip;
using Utils;

/// <summary>
/// BOLT 7 against Eclair 0.14.3 (NL-554, NL-407): (a) the mainnet day-0 channel shape, a dual-funded public channel we
/// open with a plain <c>openchannel --public</c>: <c>announcement_signatures</c> both ways at 6 confirmations, Eclair's
/// graph lists the channel, our <c>channel_update</c> and our <c>node_announcement</c>, and ours lists the channel with
/// both policies and Eclair's node; (b) and (c) NL-407: Eclair answers at most 5 gossip queries per second per
/// connection (<c>router.sync.max-queries-per-second</c>, <c>query_channel_range</c> and
/// <c>query_short_channel_ids</c> together) and drops the rest without a <c>reply_short_channel_ids_end</c>. With our
/// default <c>Gossip:MinQueryInterval</c> (250 ms) twelve <c>query_short_channel_ids</c> in a row are all answered;
/// with it at zero (the behavior before the fix) one is left unanswered and ends our querying of that connection.
/// </summary>
/// <remarks>Run with <c>scripts/run-interop.sh eclair Release -class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairGossipTests</c>.</remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairGossipTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 10 * 60 * 1_000;
    private const string OurAlias = "nltg-eclair-public";

    /// <summary>More queries in a row than Eclair's 5 per second (plus the range query at the connection's start).</summary>
    private const int QueriesInARow = 12;

    private static readonly TimeSpan s_gossipTimeout = TimeSpan.FromMinutes(4);

    /// <summary>The reply timeout of the NL-407 proofs: an unanswered query is given up after it.</summary>
    private static readonly TimeSpan s_replyTimeout = TimeSpan.FromSeconds(15);

    private readonly EclairFixture _fixture;
    private readonly List<EclairChannelSession> _sessions = [];

    public EclairGossipTests(EclairFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var session in _sessions)
                Console.WriteLine($"[eclair] channel at failure: {await session.DescribeAsync(CancellationToken.None)}");
            await DockerDiagnostics.DumpContainerLogsAsync([EclairFixture.EclairContainerName], 400);
        }

        foreach (var session in _sessions)
            await session.DisposeAsync();
    }

    /// <summary>
    /// (a) A dual-funded public channel to Eclair (plain <c>openchannel --public</c>, 1M sat; v2 since NL-551): at 6
    /// confirmations both announce it; Eclair's <c>allchannels</c> lists it, its <c>allupdates</c> has our
    /// <c>channel_update</c> and its <c>nodes</c> our <c>node_announcement</c> with our alias; our graph has the channel
    /// with both policies and Eclair's node announcement; a payment each way works on the announced channel.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ADualFundedPublicChannel_When_Confirmed_Then_BothGraphsHaveTheChannelAndBothNodes()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        var session = await EclairChannelSession.BuildOurFundedAsync(
                          _fixture, "nltg-eclair-public", LightningMoney.Satoshis(1_000_000), null, ct,
                          configureNode: n =>
                          {
                              n.ExtraConfiguration["Node:Alias"] = OurAlias;
                              n.ExtraConfiguration["Node:Color"] = GossipTestNodes.Color;
                          },
                          isPublic: true);
        _sessions.Add(session);
        var ours = await session.GetOurChannelAsync(ct);
        Assert.NotNull(ours.ShortChannelId);
        var scid = ours.ShortChannelId.Value.ToUInt64();
        Assert.True(session.Node.ChannelMemoryRepository.TryGetChannel(session.ChannelId, out var model));
        Assert.Equal(Domain.Channels.Enums.ChannelVersion.V2, model.Version);
        Assert.True(model.AnnounceChannel);

        // Assert: Eclair's graph
        await MineUntilAsync(session, async () =>
        {
            var channels = await session.Eclair.AllChannelsAsync(ct);
            return channels.Any(c => c?["shortChannelId"]?.GetValue<string>() is { } s
                                  && EclairJson.ParseShortChannelId(s) == scid);
        }, "Eclair's graph has our channel", ct);
        var update = await Poll.ForAsync(async () =>
        {
            var updates = await session.Eclair.AllUpdatesAsync(session.Node.NodeIdHex, ct);
            return updates.FirstOrDefault(u => u?["shortChannelId"]?.GetValue<string>() is { } s
                                            && EclairJson.ParseShortChannelId(s) == scid);
        }, s_gossipTimeout, "Eclair has our channel_update", ct);
        Console.WriteLine($"[proof] our channel_update in Eclair: {update.ToJsonString()}");
        var node = await Poll.ForAsync(async () =>
        {
            var nodes = await session.Eclair.NodesAsync(ct);
            return nodes.FirstOrDefault(n => n?["nodeId"]?.GetValue<string>() == session.Node.NodeIdHex);
        }, s_gossipTimeout, "Eclair has our node_announcement", ct);
        Console.WriteLine($"[proof] our node_announcement in Eclair: {node.ToJsonString()}");
        Assert.Equal(OurAlias, node["alias"]?.GetValue<string>());

        // ...and ours
        var stored = await Poll.ForAsync(async () =>
                                             await GossipGraphProbe.TryGetOurGraphChannelAsync(session.Node, scid) is
                                             { Policy1: not null, Policy2: not null } channel
                                                 ? channel
                                                 : null,
                                         s_gossipTimeout, "our graph has the channel with both policies", ct,
                                         GossipGraphProbe.PollInterval);
        Console.WriteLine($"[proof] our graph: {stored.ShortChannelId} capacity {stored.CapacitySat}");
        await Poll.ForAsync(() => GossipGraphProbe.TryGetOurGraphNodeAsync(session.Node,
                                                                           Convert.FromHexString(_fixture.EclairNodeId)),
                            s_gossipTimeout, "our graph has Eclair's node_announcement", ct,
                            GossipGraphProbe.PollInterval);

        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
    }

    /// <summary>
    /// (b) NL-407 fixed: a node with the default <c>Gossip:MinQueryInterval</c> asks Eclair for twelve short channel ids
    /// one after another (<c>IGossipSyncManager.QueryScidAsync</c>, each a <c>query_short_channel_ids</c> waiting for its
    /// <c>reply_short_channel_ids_end</c>); Eclair answers every one.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_PacedQueries_When_WeQueryEclairTwelveTimesInARow_Then_EveryQueryIsAnswered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await ConnectQueryNodeAsync("nltg-eclair-paced", null, ct);

        // Act
        var answers = await QueryInARowAsync(session, ct);

        // Assert
        Assert.All(answers, a => Assert.True(a.Answered, $"query {a.Index} was not answered ({a.Elapsed})"));
    }

    /// <summary>
    /// (c) NL-407 as it was: with <c>Gossip:MinQueryInterval</c> at zero the twelve queries go out as fast as Eclair
    /// answers, Eclair's rate limiter drops one without a <c>reply_short_channel_ids_end</c>, and that query is given up
    /// after the reply timeout (in the run that proved it: queries 0-4 answered within 3 ms each, query 5 given up after
    /// the 15 s timeout; a later query waits for the late end, NL-365, which Eclair never sends, for one more timeout
    /// and then the connection is asked nothing more, NL-718).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_UnpacedQueries_When_WeQueryEclairTwelveTimesInARow_Then_EclairLeavesOneUnanswered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await ConnectQueryNodeAsync("nltg-eclair-unpaced", TimeSpan.Zero, ct);

        // Act
        var answers = await QueryInARowAsync(session, ct);

        // Assert
        var firstUnanswered = answers.FirstOrDefault(a => !a.Answered);
        Assert.NotNull(firstUnanswered);
        Assert.True(firstUnanswered.Index >= 3,
                    $"query {firstUnanswered.Index} was dropped, before Eclair's 5 queries per second were used");
        Assert.True(firstUnanswered.Elapsed >= s_replyTimeout - TimeSpan.FromSeconds(1),
                    $"query {firstUnanswered.Index} failed after {firstUnanswered.Elapsed}, not at the reply timeout");
    }

    /// <summary>
    /// A node connected to Eclair (no channel) whose initial range sync with Eclair is done, with
    /// <c>Gossip:SyncReplyTimeout</c> at <see cref="s_replyTimeout"/> and <c>Gossip:MinQueryInterval</c> at
    /// <paramref name="minQueryInterval"/> (null: the default).
    /// </summary>
    private async Task<EclairChannelSession> ConnectQueryNodeAsync(string name, TimeSpan? minQueryInterval,
                                                                  CancellationToken ct)
    {
        var session = await EclairChannelSession.CreateConnectedAsync(
                          _fixture, name, LightningMoney.Satoshis(100_000), ct,
                          configureNode: n =>
                          {
                              n.ExtraConfiguration[$"{GossipSyncOptions.SectionName}:"
                                                 + nameof(GossipSyncOptions.SyncReplyTimeout)] =
                                  s_replyTimeout.ToString("c");
                              if (minQueryInterval is { } interval)
                                  n.ExtraConfiguration[$"{GossipSyncOptions.SectionName}:"
                                                     + nameof(GossipSyncOptions.MinQueryInterval)] =
                                      interval.ToString("c");
                          });
        _sessions.Add(session);
        var options = session.Node.Services
                             .GetRequiredService<Microsoft.Extensions.Options.IOptions<GossipSyncOptions>>().Value;
        Assert.Equal(minQueryInterval ?? GossipSyncOptions.DefaultMinQueryInterval, options.MinQueryInterval);
        Assert.Equal(s_replyTimeout, options.SyncReplyTimeout);
        var sync = session.Node.Services.GetRequiredService<IGossipSyncManager>();
        await Poll.UntilAsync(() => Task.FromResult(sync.HasCompletedInitialSync), TimeSpan.FromSeconds(60),
                              "the initial range sync with Eclair", ct);
        // Eclair's limiter counts the range query too: start the burst a second later
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        return session;
    }

    /// <summary>
    /// <see cref="QueriesInARow"/> one-channel queries for short channel ids Eclair does not know (it still answers each
    /// with <c>reply_short_channel_ids_end</c>), each started when the previous one completed.
    /// </summary>
    private static async Task<IReadOnlyList<QueryAnswer>> QueryInARowAsync(EclairChannelSession session,
                                                                         CancellationToken ct)
    {
        var sync = session.Node.Services.GetRequiredService<IGossipSyncManager>();
        var answers = new List<QueryAnswer>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < QueriesInARow; i++)
        {
            var at = started.Elapsed;
            var answered = await sync.QueryScidAsync(new ShortChannelId(500_000 + (uint)i, 1, 0), ct);
            answers.Add(new QueryAnswer(i, answered, started.Elapsed - at));

            // After an unanswered query the next one first waits for the late end (NL-365), which Eclair never sends,
            // for another reply timeout (NL-718)
            if (!answered)
                break;
        }

        Console.WriteLine($"[proof] {QueriesInARow} queries in {started.Elapsed}: "
                        + string.Join(", ", answers.Select(a => $"{a.Index}={(a.Answered ? "answered" : "FAILED")} "
                                                                + $"{a.Elapsed.TotalMilliseconds:F0} ms")));
        return answers;
    }

    private async Task MineUntilAsync(EclairChannelSession session, Func<Task<bool>> condition, string what,
                                      CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_gossipTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Not in time: {what}");

            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private sealed record QueryAnswer(int Index, bool Answered, TimeSpan Elapsed);
}