
namespace NLightning.Integration.Tests.Docker.Gossip.Capture;

using Domain.Bitcoin.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Fixtures;
using Fixtures.Cln;
using Interop.Cln;
using Utils;

/// <summary>
/// One-off capture of the BOLT 7 messages Core Lightning (<see cref="ClnFixture.ClnTag"/>) sends, for
/// <c>Tests.Utils/Vectors/Bolt7Vectors.cs</c> (plan G0-T5). <c>Explicit</c>: run it on purpose and copy the
/// <c>VECTOR</c> lines it prints.
/// </summary>
/// <remarks>
/// CLN announces a channel only with another announcing peer, so the test starts a second CLN (<c>nltg-cln2</c>) on the
/// fixture's network, has the fixture's CLN fund a public channel to it and waits until both are announced. Our node
/// then connects and sends <c>gossip_timestamp_filter</c> covering every timestamp, and CLN answers with the
/// <c>channel_announcement</c>, both <c>channel_update</c>s and the <c>node_announcement</c>s. The second test has CLN
/// fund a public channel to us and records the <c>announcement_signatures</c> it sends at 6 confirmations.
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnGossipCaptureTests : IAsyncLifetime
{
    private const string SecondClnContainerName = "nltg-cln2";
    private static readonly TimeSpan s_captureTimeout = TimeSpan.FromMinutes(3);

    private readonly ClnFixture _fixture;
    private readonly RawGossipRecorder _recorder = new();
    private ExtraClnNode? _cln2;
    private NLightningTestNode? _node;

    public ClnGossipCaptureTests(ClnFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "gossip-capture-cln");
        _node.ConfigureServices = _recorder.Install;
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
            await _node.DisposeAsync();

        if (_cln2 is not null)
            await _cln2.DisposeAsync();
    }

    [Fact(Explicit = true)]
    public async Task Given_ClnWithAnnouncedChannel_When_TimestampFilterSent_Then_AnnouncementsAndUpdatesRecorded()
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
            var channels = (await cln.CallAsync("listchannels", ct, ("source", _fixture.ClnNodeId)))["channels"]!
               .AsArray();
            var nodes = (await cln.CallAsync("listnodes", ct, ("id", _fixture.ClnNodeId)))["nodes"]!.AsArray();
            return channels.Any(c => c?["public"]?.GetValue<bool>() == true)
                && nodes.Any(n => n?["alias"] is not null);
        }, s_captureTimeout, "CLN announced its channel and node", ct);

        // Act: connect and ask for every gossip message CLN has
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnAddress)).WaitAsync(ct);
        CompactPubKey clnId = Convert.FromHexString(_fixture.ClnNodeId);
        await SendTimestampFilterAsync(node, clnId, ct);

        // Assert
        await Poll.UntilAsync(() => Task.FromResult(_recorder.OfType(256).Count > 0 && _recorder.OfType(257).Count > 0
                                                 && _recorder.OfType(258).Count > 0),
                              s_captureTimeout, "CLN sent 256, 257 and 258", ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        PrintVectors();
        Assert.True(node.IsConnectedTo(clnId));
    }

    [Fact(Explicit = true)]
    public async Task Given_ClnFundsPublicChannelToUs_When_SixConfirmations_Then_AnnouncementSignaturesRecorded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var cln = _fixture.Cln;
        // The on-chain reserve we keep as fundee of an anchors channel (NL-379), CLN's default type with us
        await node.FundWalletAsync(LightningMoney.Satoshis(200_000), AddressType.P2Wpkh, ct);
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(2_000_000), [node], ct);
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnAddress)).WaitAsync(ct);
        await Poll.UntilAsync(async () => await cln.IsConnectedAsync(node.NodeIdHex, ct), s_captureTimeout,
                              "CLN lists us", ct);

        // Act: a public channel from CLN, then blocks until CLN sends its signatures
        var funded = await cln.CallAsync("fundchannel", ct, ("id", node.NodeIdHex), ("amount", 1_000_000L),
                                         ("announce", true));
        Console.WriteLine($"CLN funded a public channel to us: {funded.ToJsonString()}");
        await Poll.UntilAsync(async () =>
        {
            await _fixture.MineAndWaitAsync(1, [node], ct);
            return _recorder.OfType(259).Count > 0;
        }, s_captureTimeout, "CLN sent announcement_signatures", ct);

        // Assert
        PrintVectors();
    }

    private static async Task SendTimestampFilterAsync(NLightningTestNode node, CompactPubKey peerId,
                                                       CancellationToken ct)
    {
        var peer = await Poll.ForAsync(() => Task.FromResult(node.PeerManager.GetPeer(peerId)), s_captureTimeout,
                                       "peer registered", ct);
        Assert.True(peer.TryGetPeerService(out var peerService));
        await peerService.SendGossipMessageAsync(
            new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 0,
                                                                              uint.MaxValue)));
    }

    /// <summary>
    /// The second CLN (<see cref="SecondClnContainerName"/>, the fixture CLN's flags without
    /// <c>--ignore-fee-limits=false</c>), reached only by the fixture's CLN.
    /// </summary>
    private async Task<ClnClient> StartSecondClnAsync(CancellationToken ct)
    {
        _cln2 = await _fixture.StartClnAsync(new ClnNodeSpec(SecondClnContainerName)
        {
            EnforceFeeLimits = false,
            ReachableFromTests = false
        }, ct);
        return _cln2.Client;
    }

    private void PrintVectors()
    {
        foreach (var recorded in _recorder.Received)
            Console.WriteLine($"VECTOR cln {recorded.Type} {recorded.PayloadHex}");
    }
}