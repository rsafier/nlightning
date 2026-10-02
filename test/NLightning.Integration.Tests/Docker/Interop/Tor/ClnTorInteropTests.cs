using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Tor;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Payments.Enums;
using Fixtures;
using Infrastructure.Transport.Tor;
using Utils;

/// <summary>
/// Tor interop with Core Lightning (NL-572): our node in <see cref="TorMode.Hybrid"/> and <see cref="TorMode.TorOnly"/>
/// dials CLN's onion service through Tor's SOCKS5 port, opens a channel and pays both ways over that connection; our
/// Tor-only node's own onion service (registered through the control port) takes CLN's connection and a channel CLN
/// funds; and a Tor restart drops everything, after which our node redials CLN's onion, re-adds its onion service with
/// the same key, reestablishes the channel and pays again.
/// </summary>
/// <remarks>
/// CLN listens on <c>127.0.0.1</c> inside the Tor container's network namespace only (<see cref="TorInteropFixture"/>),
/// so a connection that reaches it came through its onion service: CLN lists such a peer at <c>127.0.0.1:&lt;port&gt;</c>
/// (Tor's side of the rendezvous). The onion services are on the public Tor network.
/// </remarks>
[Collection(TorInteropCollection.Name)]
[Trait("Category", TorInteropCollection.Category)]
public sealed class ClnTorInteropTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 15 * 60 * 1_000;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(500_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(150_000);
    private static readonly TimeSpan s_connectedTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_usableTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(90);

    private readonly TorInteropFixture _fixture;
    private readonly List<string> _keyFiles = [];

    public ClnTorInteropTests(TorInteropFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                        + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 100, "unusual"));
        if (DockerDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine("[tor] log tail:\n" + await _fixture.GetTorLogAsync(80, CancellationToken.None));
            await DockerDiagnostics.DumpContainerLogsAsync([TorInteropFixture.ClnContainerName], 300);
        }

        foreach (var keyFile in _keyFiles)
        {
            try
            {
                File.Delete(keyFile);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// A Hybrid node (no onion service of its own) dials CLN's onion through Tor, funds a channel to it and pays both
    /// ways over the onion connection.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AHybridNode_When_ItDialsClnsOnionService_Then_AChannelOpensAndPaysBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var node = await CreateNodeAsync("nltg-tor-hybrid", TorMode.Hybrid, onionService: false);
        await node.StartAsync(ct);
        Assert.Null(node.Services.GetRequiredService<ITorOnionService>().OnionHost);

        // Act
        await ConnectToClnOnionAsync(node, ct);
        var channelId = await OpenToClnAsync(node, ct);

        // Assert
        await AssertWePayClnAsync(node, channelId, LightningMoney.Satoshis(20_000), ct);
        await AssertClnPaysUsAsync(node, channelId, LightningMoney.Satoshis(15_000), ct);
    }

    /// <summary>
    /// A Tor-only node with its own onion service dials CLN's onion, funds a channel and pays both ways; then Tor is
    /// killed and starts again: our node redials CLN's onion (reconnect backoff), re-adds its onion service with the
    /// same key (the same address), reestablishes the channel, and payments work both ways again.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ATorOnlyNodeWithAChannelToCln_When_TorRestarts_Then_ItReconnectsAndPaysAgain()
    {
        // Arrange: Tor-only, onion service up
        var ct = TestContext.Current.CancellationToken;
        await using var node = await CreateNodeAsync("nltg-tor-only", TorMode.TorOnly, onionService: true);
        await node.StartAsync(ct);
        var onionHost = await WaitOurOnionServiceAsync(node, ct);
        await ConnectToClnOnionAsync(node, ct);
        var channelId = await OpenToClnAsync(node, ct);
        await AssertWePayClnAsync(node, channelId, LightningMoney.Satoshis(20_000), ct);
        await AssertClnPaysUsAsync(node, channelId, LightningMoney.Satoshis(15_000), ct);
        var upLine = $"Tor onion service {onionHost}:{TorInteropFixture.OnionPort} is up";
        Assert.Equal(1, node.CountLogLines(upLine));

        // Act
        await _fixture.RestartTorAsync(ct);

        // Assert: the onion service is back under the same address, and the channel with it
        await Poll.UntilAsync(() => node.CountLogLines(upLine) >= 2, s_usableTimeout,
                              "our onion service re-added after the Tor restart", ct, TimeSpan.FromSeconds(1));
        Assert.Equal(onionHost, node.Services.GetRequiredService<ITorOnionService>().OnionHost);
        await WaitUsableAsync(node, channelId, ct);
        Assert.StartsWith("127.0.0.1:", await GetClnNetAddressAsync(node, ct));
        await AssertWePayClnAsync(node, channelId, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(node, channelId, LightningMoney.Satoshis(16_000), ct);
    }

    /// <summary>
    /// CLN dials our Tor-only node's onion service (registered through Tor's control port), funds a channel to us over
    /// that connection, and payments work both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ATorOnlyNodesOnionService_When_ClnDialsItAndFundsAChannel_Then_PaymentsWorkBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var node = await CreateNodeAsync("nltg-tor-inbound", TorMode.TorOnly, onionService: true);
        await node.StartAsync(ct);
        var onionHost = await WaitOurOnionServiceAsync(node, ct);
        await _fixture.WaitOnionReachableAsync(onionHost, ct);
        // The on-chain reserve we keep as fundee of an anchors channel (NL-379), CLN's default type with us
        await node.FundWalletAsync(LightningMoney.Satoshis(200_000), AddressType.P2Wpkh, ct);
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(s_capacity.Satoshi * 2), [node], ct);
        CompactPubKey clnId = Convert.FromHexString(_fixture.ClnNodeId);

        // Act: CLN connects to our onion through its Tor proxy
        JsonNode? connect = null;
        await Poll.UntilAsync(async () =>
        {
            try
            {
                connect = await _fixture.Cln.CallAsync("connect", ct, ("id", node.NodeIdHex), ("host", onionHost),
                                                       ("port", TorInteropFixture.OnionPort));
                return true;
            }
            catch (ClnRpcException e)
            {
                Console.WriteLine($"[cln] connect to our onion failed, retrying: {e.Message}");
                return false;
            }
        }, s_connectedTimeout, "CLN connected to our onion", ct, TimeSpan.FromSeconds(3));
        Console.WriteLine($"[cln] CLN connect: {connect!.ToJsonString()}");
        await Poll.UntilAsync(async () => node.IsConnectedTo(clnId) && await _fixture.Cln.IsConnectedAsync(
                                                                           node.NodeIdHex, ct),
                              s_connectedTimeout, "both ends list each other", ct);
        var funded = await _fixture.Cln.CallAsync("fundchannel", ct, ("id", node.NodeIdHex),
                                                  ("amount", s_capacity.Satoshi), ("announce", false));
        ChannelId channelId = Convert.FromHexString(funded["channel_id"]!.GetValue<string>());
        Console.WriteLine($"[cln] CLN opened {channelId} to us over our onion: {funded.ToJsonString()}");
        await MineUntilUsableAsync(node, channelId, ct);

        // Assert: CLN reached us at our onion, and the channel pays both ways
        Assert.Equal("out", connect["direction"]!.GetValue<string>());
        Assert.Contains(onionHost, await GetClnNetAddressAsync(node, ct), StringComparison.OrdinalIgnoreCase);
        Assert.False((await node.GetChannelAsync(channelId, ct)).IsInitiator);
        await AssertClnPaysUsAsync(node, channelId, LightningMoney.Satoshis(30_000), ct);
        await AssertWePayClnAsync(node, channelId, LightningMoney.Satoshis(10_000), ct);
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, TorMode mode, bool onionService)
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"nltg_{name}_{Guid.NewGuid():N}.onion.key");
        _keyFiles.Add(keyFile);
        NLightningTestNode? node = null;
        // The options are applied at start, once the node (and its port) exists
        node = await NLightningTestNode.CreateAsync(
                   _fixture.Bitcoin, name,
                   configureNodeOptions: o => _fixture.ConfigureTor(o, mode, onionService, node!.Port, keyFile));
        return node;
    }

    /// <summary>Waits until Tor accepted our onion service and returns its host name.</summary>
    private static async Task<string> WaitOurOnionServiceAsync(NLightningTestNode node, CancellationToken ct)
    {
        var onion = node.Services.GetRequiredService<ITorOnionService>();
        var host = await Poll.ForAsync(() => Task.FromResult(onion.OnionHost), s_connectedTimeout,
                                       $"{node.Name}'s onion service up", ct);
        Console.WriteLine($"[tor] {node.Name} is {node.NodeIdHex}@{host}:{onion.OnionPort}");
        return host;
    }

    private async Task ConnectToClnOnionAsync(NLightningTestNode node, CancellationToken ct)
    {
        CompactPubKey clnId = Convert.FromHexString(_fixture.ClnNodeId);
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnOnionAddress)).WaitAsync(ct);
        await Poll.UntilAsync(async () => node.IsConnectedTo(clnId)
                                       && await _fixture.Cln.IsConnectedAsync(node.NodeIdHex, ct),
                              s_connectedTimeout, $"{node.Name} and CLN connected over Tor", ct);
        // CLN listens only inside Tor's network namespace: we came in through its onion service
        Assert.StartsWith("127.0.0.1:", await GetClnNetAddressAsync(node, ct));
    }

    /// <summary>Where CLN sees <paramref name="node"/> (<c>listpeers</c> <c>netaddr</c>).</summary>
    private async Task<string> GetClnNetAddressAsync(NLightningTestNode node, CancellationToken ct)
    {
        var peer = await _fixture.Cln.GetPeerAsync(node.NodeIdHex, ct)
                ?? throw new InvalidOperationException("CLN does not list us");
        var address = peer["netaddr"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? string.Empty;
        Console.WriteLine($"[cln] CLN sees {node.Name} at {address}");
        return address;
    }

    private async Task<ChannelId> OpenToClnAsync(NLightningTestNode node, CancellationToken ct)
    {
        await node.FundWalletAsync(LightningMoney.Satoshis(s_capacity.Satoshi * 5 / 2), AddressType.P2Wpkh, ct);
        await _fixture.WaitAllAtTipAsync([node], ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(_fixture.ClnOnionAddress, s_capacity)
        {
            PushAmount = s_push
        }, ct);
        Console.WriteLine($"[tor] {node.Name} opened {channel.ChannelId} to CLN over Tor, state {channel.ChannelState}");
        await MineUntilUsableAsync(node, channel.ChannelId, ct);
        return channel.ChannelId;
    }

    /// <summary>Mines one block at a time until both ends agree the channel is usable.</summary>
    private async Task MineUntilUsableAsync(NLightningTestNode node, ChannelId channelId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_usableTimeout;
        while (true)
        {
            var (ready, status) = await CheckUsableAsync(node, channelId, ct);
            if (ready)
            {
                Console.WriteLine($"[tor] channel ready: {status}");
                return;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The channel was not usable on both ends in time: {status}");

            await _fixture.MineAndWaitAsync(1, [node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task WaitUsableAsync(NLightningTestNode node, ChannelId channelId, CancellationToken ct)
    {
        var status = string.Empty;
        try
        {
            await Poll.UntilAsync(async () =>
            {
                var (ready, s) = await CheckUsableAsync(node, channelId, ct);
                status = s;
                return ready;
            }, s_usableTimeout, "the channel usable on both ends", ct, TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}. Last status: {status}", e);
        }
    }

    private async Task<(bool Ready, string Status)> CheckUsableAsync(NLightningTestNode node, ChannelId channelId,
                                                                     CancellationToken ct)
    {
        var ours = (await node.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
        var theirs = await _fixture.Cln.GetPeerChannelAsync(node.NodeIdHex, channelId.ToString(), ct);
        var ready = ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0, ShortChannelId: not null }
                 && ours.IsUsable()
                 && theirs?["state"]?.GetValue<string>() == "CHANNELD_NORMAL"
                 && theirs["peer_connected"]?.GetValue<bool>() == true
                 && theirs["htlcs"]?.AsArray().Count == 0;
        return (ready, $"ours {ours?.Describe() ?? "not listed"}; cln {theirs?["state"]} "
                     + $"connected={theirs?["peer_connected"]} htlcs={theirs?["htlcs"]?.AsArray().Count}");
    }

    private async Task AssertWePayClnAsync(NLightningTestNode node, ChannelId channelId, LightningMoney amount,
                                           CancellationToken ct)
    {
        var before = await node.GetChannelAsync(channelId, ct);
        var label = $"nltg-tor-pays-cln-{Guid.NewGuid():N}";
        var invoice = await _fixture.Cln.CallAsync("invoice", ct, ("amount_msat", (long)amount.MilliSatoshi),
                                                   ("label", label), ("description", "nltg pays cln over tor"));

        var payment = await node.PayInvoiceAsync(invoice["bolt11"]!.GetValue<string>(), ct, 120);

        Console.WriteLine($"[tor] our payment: {payment.Status}, failure {payment.FailureCode}: "
                        + $"{payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var clnInvoice = (await _fixture.Cln.CallAsync("listinvoices", ct, ("label", label)))["invoices"]!
                        .AsArray().Single()!;
        Assert.Equal("paid", clnInvoice["status"]!.GetValue<string>());
        Assert.NotNull(payment.Preimage);
        Assert.Equal(clnInvoice["payment_preimage"]!.GetValue<string>(),
                     Convert.ToHexString((byte[])payment.Preimage.Value).ToLowerInvariant());
        await WaitUsableAsync(node, channelId, ct);
        var after = await node.GetChannelAsync(channelId, ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi - amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }

    private async Task AssertClnPaysUsAsync(NLightningTestNode node, ChannelId channelId, LightningMoney amount,
                                            CancellationToken ct)
    {
        var before = await node.GetChannelAsync(channelId, ct);
        var invoice = await node.CreateInvoiceAsync(amount, $"cln pays nltg over tor {Guid.NewGuid():N}", ct);

        var result = await _fixture.Cln.CallAsync("pay", ct, ("bolt11", invoice.Bolt11!), ("retry_for", 60));

        Console.WriteLine($"[cln] CLN pay: {result.ToJsonString()}");
        Assert.Equal("complete", result["status"]!.GetValue<string>());
        var preimage = Convert.FromHexString(result["payment_preimage"]!.GetValue<string>());
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(preimage));
        var ours = await Poll.ForAsync(async () =>
        {
            var current = await node.GetInvoiceAsync(invoice.PaymentHash, ct);
            return current?.Status == InvoiceStatus.Settled ? current : null;
        }, s_settleTimeout, "our invoice settled", ct);
        Assert.Equal(amount, ours.AmountReceived);
        await WaitUsableAsync(node, channelId, ct);
        var after = await node.GetChannelAsync(channelId, ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi + amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }
}