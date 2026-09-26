using Docker.DotNet;
using Docker.DotNet.Models;

namespace NLightning.Integration.Tests.Docker.Gossip.Capture;

using Domain.Crypto.ValueObjects;
using Domain.Gossip.Queries;
using Domain.Money;
using Domain.Node.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Fixtures;
using Interop.Cln;
using Utils;

/// <summary>
/// One-off capture of Core Lightning's answer to a <c>query_channel_range</c> with <c>query_option</c> timestamps and
/// checksums (BOLT 7 plan G3-T4), for <c>Tests.Utils/Vectors/Bolt7QueryVectors.cs</c>: the <c>reply_channel_range</c>
/// with its <c>timestamps_tlv</c> and <c>checksums_tlv</c>, and the <c>channel_update</c>s they describe.
/// <c>Explicit</c>: run it on purpose and copy the <c>VECTOR</c> lines it prints.
/// </summary>
/// <remarks>
/// As in <see cref="ClnGossipCaptureTests"/>, a second CLN makes the fixture's CLN announce a public channel. Our node
/// runs with its own sync off (<c>Gossip:SyncEnabled=false</c>), so the only query is the test's: a
/// <c>gossip_timestamp_filter</c> for every timestamp (the channel's announcement and updates), then
/// <c>query_channel_range(0, tip + 1)</c> with <c>query_option</c> = 3.
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnGossipQueryCaptureTests : IAsyncLifetime
{
    private const string SecondClnContainerName = "nltg-cln2";
    private static readonly TimeSpan s_captureTimeout = TimeSpan.FromMinutes(3);

    private readonly ClnFixture _fixture;
    private readonly RawGossipRecorder _recorder = new();
    private readonly DockerClient _docker = new DockerClientConfiguration().CreateClient();
    private NLightningTestNode? _node;

    public ClnGossipQueryCaptureTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "gossip-query-capture-cln");
        _node.ExtraConfiguration["Gossip:SyncEnabled"] = "false";
        _node.ConfigureServices = _recorder.Install;
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
            await _node.DisposeAsync();

        await DockerContainerUtils.RemoveContainerAsync(_docker, SecondClnContainerName);
        _docker.Dispose();
    }

    [Fact(Explicit = true)]
    public async Task Given_ClnWithAnnouncedChannel_When_RangeQueriedWithChecksums_Then_ReplyAndUpdatesRecorded()
    {
        // Arrange: a public CLN -> CLN2 channel, announced
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var cln = _fixture.Cln;
        Console.WriteLine($"CLN version: {(await cln.GetInfoAsync(ct))["version"]}");
        var cln2 = await StartSecondClnAsync(ct);
        var cln2Id = (await cln2.GetInfoAsync(ct))["id"]!.GetValue<string>();
        await cln.CallAsync("connect", ct, ("id", $"{cln2Id}@{SecondClnContainerName}:9735"));
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(3_000_000), [node], ct);
        var funded = await cln.CallAsync("fundchannel", ct, ("id", cln2Id), ("amount", 1_000_000L),
                                         ("announce", true));
        Console.WriteLine($"CLN funded a public channel to CLN2: {funded.ToJsonString()}");
        await Poll.UntilAsync(async () =>
        {
            await _fixture.MineAndWaitAsync(1, [node], ct);
            // Both directions known to CLN: its dump then carries both updates
            var channels = (await cln.CallAsync("listchannels", ct))["channels"]!.AsArray();
            return channels.Count(c => c?["public"]?.GetValue<bool>() == true) >= 2;
        }, s_captureTimeout, "CLN knows both directions of its public channel", ct);
        var tip = await _fixture.WaitAllAtTipAsync([node], ct);

        // Act: the channel's gossip, then the range query with timestamps and checksums
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnAddress)).WaitAsync(ct);
        CompactPubKey clnId = Convert.FromHexString(_fixture.ClnNodeId);
        var peer = await Poll.ForAsync(() => Task.FromResult(node.PeerManager.GetPeer(clnId)), s_captureTimeout,
                                       "peer registered", ct);
        Assert.True(peer.TryGetPeerService(out var peerService));
        await peerService.SendGossipMessageAsync(
            new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 0,
                                                                              uint.MaxValue)));
        await Poll.UntilAsync(() => Task.FromResult(_recorder.OfType(258).Select(r => r.PayloadHex).Distinct()
                                                             .Count() >= 2),
                              s_captureTimeout, "CLN sent both channel_updates", ct);
        var option = GossipQueryCodec.QueryOptionTimestamps | GossipQueryCodec.QueryOptionChecksums;
        await peerService.SendGossipMessageAsync(
            new QueryChannelRangeMessage(new QueryChannelRangePayload(ChainConstants.Regtest, 0, tip + 1),
                                         new BaseTlv(TlvConstants.QueryOption,
                                                     GossipQueryCodec.EncodeQueryOption(option))));

        // Assert
        await Poll.UntilAsync(() => Task.FromResult(_recorder.OfType(264).Count > 0), s_captureTimeout,
                              "CLN sent reply_channel_range", ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        foreach (var recorded in _recorder.Received.Where(r => r.Type is 258 or 264).DistinctBy(r => r.PayloadHex))
            Console.WriteLine($"VECTOR cln {recorded.Type} {recorded.PayloadHex}");
        Assert.True(node.IsConnectedTo(clnId));
    }

    private async Task<ClnClient> StartSecondClnAsync(CancellationToken ct)
    {
        await DockerContainerUtils.RemoveContainerAsync(_docker, SecondClnContainerName);
        var container = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{ClnFixture.ClnImage}:{ClnFixture.ClnTag}",
            Name = SecondClnContainerName,
            Hostname = SecondClnContainerName,
            Env = ["LIGHTNINGD_NETWORK=regtest"],
            Cmd =
            [
                $"--bitcoin-rpcconnect={ClnFixture.BitcoinContainerName}", "--bitcoin-rpcport=18443",
                "--bitcoin-rpcuser=nltg", "--bitcoin-rpcpassword=nltg", "--bind-addr=0.0.0.0:9735",
                "--alias=nltg-cln2", "--log-level=debug", "--developer", "--dev-bitcoind-poll=1"
            ],
            HostConfig = new HostConfig { NetworkMode = ClnFixture.NetworkName }
        }, ct);
        await _docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
        var cln2 = new ClnClient(_docker, SecondClnContainerName);
        await DockerContainerUtils.WaitUntilReadyAsync(SecondClnContainerName,
                                                       async token => await cln2.GetInfoAsync(token),
                                                       TimeSpan.FromMinutes(2));
        return cln2;
    }
}