using Google.Protobuf;
using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Gossip.Capture;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// One-off capture of the BOLT 7 messages LND 0.20 (the fixture's <c>custom_lnd</c>) sends, for
/// <c>Tests.Utils/Vectors/Bolt7Vectors.cs</c> (plan G0-T5). <c>Explicit</c>: run it on purpose with the in-container
/// runner and copy the <c>VECTOR</c> lines it prints; the vector tests then check each one parses, re-serializes
/// byte-exact and that its signatures verify.
/// </summary>
/// <remarks>
/// LND sends graph gossip only to a peer that sent <c>gossip_timestamp_filter</c> (BOLT 7 B7-RL-01), so the test sends
/// one covering every timestamp; alice's fixture channels are public and deep, so she answers with their
/// <c>channel_announcement</c>s, <c>channel_update</c>s and the <c>node_announcement</c>s. The second test has alice
/// open a <b>public</b> channel to us and records the <c>announcement_signatures</c> she sends at 6 confirmations (we do
/// not answer it yet: G1).
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public sealed class LndGossipCaptureTests : IAsyncLifetime
{
    private static readonly TimeSpan s_captureTimeout = TimeSpan.FromMinutes(2);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly RawGossipRecorder _recorder = new();
    private NLightningTestNode? _node;

    public LndGossipCaptureTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "gossip-capture");
        _node.ConfigureServices = _recorder.Install;
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
            await _node.DisposeAsync();
    }

    [Fact(Explicit = true)]
    public async Task Given_TimestampFilterSentToLnd_When_LndDumpsItsGraph_Then_AnnouncementsAndUpdatesRecorded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(alice, ct)}");
        await ChainSync.WaitAllAtTipAsync(_fixture, [node], ct);
        await node.ConnectToAsync(alice, ct);

        // Act: ask for every gossip message alice has
        await SendTimestampFilterAsync(node, alice.LocalNodePubKeyBytes, ct);

        // Assert
        await Poll.UntilAsync(() => Task.FromResult(_recorder.OfType(256).Count > 0 && _recorder.OfType(257).Count > 0
                                                 && _recorder.OfType(258).Count > 0),
                              s_captureTimeout, "LND sent 256, 257 and 258", ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        PrintVectors("lnd");
        Assert.True(node.IsConnectedTo(alice.LocalNodePubKeyBytes));
    }

    [Fact(Explicit = true)]
    public async Task Given_LndOpensPublicChannelToUs_When_SixConfirmations_Then_AnnouncementSignaturesRecorded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        await ChainSync.WaitAllAtTipAsync(_fixture, [node], ct);
        await node.ConnectToAsync(alice, ct);

        // Act: a public channel from alice, then 6 blocks
        var point = await alice.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])node.NodeId),
            LocalFundingAmount = 1_000_000,
            Private = false
        }, cancellationToken: ct);
        Console.WriteLine($"alice opened a public channel to us: {Convert.ToHexStringLower(point.FundingTxidBytes.ToByteArray())}");
        await Poll.UntilAsync(async () => (await node.ListChannelsAsync(ct)).Channels.Count == 1, s_captureTimeout,
                              "our end of alice's public channel", ct);
        for (var i = 0; i < 8 && _recorder.OfType(259).Count == 0; i++)
        {
            await ChainSync.MineAndWaitAsync(_fixture, 1, _fixture.LndNodes, [node], ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // Assert
        await Poll.UntilAsync(() => Task.FromResult(_recorder.OfType(259).Count > 0), s_captureTimeout,
                              "LND sent announcement_signatures", ct);
        PrintVectors("lnd");
    }

    private static async Task SendTimestampFilterAsync(NLightningTestNode node, byte[] peerId, CancellationToken ct)
    {
        var peer = await Poll.ForAsync(() => Task.FromResult(node.PeerManager.GetPeer(peerId)), s_captureTimeout,
                                       "peer registered", ct);
        Assert.True(peer.TryGetPeerService(out var peerService));
        await peerService.SendGossipMessageAsync(
            new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 0,
                                                                              uint.MaxValue)));
    }

    private void PrintVectors(string source)
    {
        foreach (var recorded in _recorder.Received)
            Console.WriteLine($"VECTOR {source} {recorded.Type} {recorded.PayloadHex}");
    }
}