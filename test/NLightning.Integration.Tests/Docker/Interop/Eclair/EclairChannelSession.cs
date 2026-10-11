using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Abcd;
using Cln;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Payments.Enums;
using Fixtures;
using Utils;

/// <summary>
/// One channel between an in-process node and Eclair (<see cref="EclairFixture"/>), as <see cref="ClnChannelSession"/>
/// does for CLN: the shared channel we fund (<see cref="GetAsync"/>, 1M sat, a plain <c>openchannel</c> that goes
/// dual-funded, NL-551) and separate ones built
/// by <see cref="BuildOurFundedAsync"/> and <see cref="BuildEclairFundedAsync"/>, followed until both ends are usable.
/// </summary>
public sealed partial class EclairChannelSession : IAsyncDisposable
{
    public static readonly LightningMoney Capacity = LightningMoney.Satoshis(1_000_000);
    public static readonly TimeSpan UsableTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    private const string CacheKey = "eclair-channel-session";

    private readonly EclairFixture _fixture;
    private readonly EclairEndpoint? _eclair;

    private EclairChannelSession(EclairFixture fixture, NLightningTestNode node, EclairEndpoint? eclair = null)
    {
        _fixture = fixture;
        _eclair = eclair;
        Node = node;
        Sent = new ChannelMessageRecorder(node.Name);
    }

    public NLightningTestNode Node { get; }

    public ChannelMessageRecorder Sent { get; }

    public ChannelId ChannelId { get; set; }

    public string ChannelIdHex => ChannelId.ToString();

    /// <summary>The Eclair of the session: the fixture's, or the one given at creation (e.g. its liquidity seller).</summary>
    public EclairClient Eclair => _eclair?.Client ?? _fixture.Eclair;

    public CompactPubKey EclairPubKey => Convert.FromHexString(EclairPubKeyHex);

    public string EclairPubKeyHex => _eclair?.NodeId ?? _fixture.EclairNodeId;

    /// <summary>The session's Eclair as <c>pubkey@127.0.0.1:port</c>.</summary>
    public string EclairAddress => _eclair?.Address ?? _fixture.EclairAddress;

    /// <summary>The shared channel we fund, built once per fixture on its own token.</summary>
    public static Task<EclairChannelSession> GetAsync(EclairFixture fixture, CancellationToken cancellationToken) =>
        ClnChannelSession.GetOrBuildDetachedAsync(factory => fixture.GetOrCreateAsync(CacheKey, factory),
                                                  ct => BuildOurFundedAsync(fixture, "nltg", Capacity, null, ct),
                                                  BuildTimeout, "Eclair interop channel", cancellationToken);

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

    public async Task<ChannelInfoClientResponse> GetOurChannelAsync(CancellationToken cancellationToken) =>
        await Node.GetChannelAsync(ChannelId, cancellationToken);

    public async Task<JsonNode> GetEclairChannelAsync(CancellationToken cancellationToken) =>
        await Eclair.ChannelAsync(ChannelIdHex, cancellationToken)
     ?? throw new InvalidOperationException($"Eclair does not know the channel {ChannelIdHex}");

    /// <summary>Waits until our end is usable and Eclair's is <c>NORMAL</c> with us connected.</summary>
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
            }, UsableTimeout, "the Eclair channel usable on both ends", cancellationToken,
                                  TimeSpan.FromMilliseconds(500));
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}. Last status: {status}", e);
        }
    }

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
            theirs = DescribeEclair(await Eclair.ChannelAsync(ChannelIdHex, cancellationToken));
        }
        catch (Exception e)
        {
            theirs = $"unavailable: {e.Message}";
        }

        return $"ours {ours}; eclair {theirs}";
    }

    public static string DescribeEclair(JsonNode? channel)
    {
        if (channel is null)
            return "not listed";

        var active = channel["data"]?["commitments"]?["active"]?[0];
        var spec = active?["localCommit"]?["spec"];
        return $"{channel["state"]} toLocal={spec?["toLocal"]} toRemote={spec?["toRemote"]} "
             + $"feerate={spec?["commitTxFeerate"]} htlcs={spec?["htlcs"]?.AsArray().Count} "
             + $"format={active?["commitmentFormat"] ?? channel["data"]?["commitments"]?["params"]?["channelFeatures"]}";
    }

    /// <summary>Eclair's channel id from its <c>open</c> answer (<c>created channel &lt;id&gt; ...</c>).</summary>
    public static ChannelId ParseOpenedChannelId(string openAnswer)
    {
        var match = CreatedChannelRegex().Match(openAnswer);
        Assert.True(match.Success, $"Unexpected Eclair open answer: {openAnswer}");
        return Convert.FromHexString(match.Groups["id"].Value);
    }

    public async ValueTask DisposeAsync()
    {
        Sent.Dispose();
        await Node.DisposeAsync();
    }

    /// <summary>
    /// A node <paramref name="nodeName"/> that funds a private channel of <paramref name="capacity"/> to Eclair at our
    /// fee estimate, followed until both ends are usable. The caller disposes the session.
    /// </summary>
    /// <param name="push">
    /// Null for a plain <c>openchannel</c> (no <c>--dual-fund</c>) from a node with our default features, which opens
    /// v2 (<c>open_channel2</c>) because <c>option_dual_fund</c> is negotiated (NL-551). With a push amount the open is
    /// v1 (<c>open_channel</c>; v2 has no push) from a node with <c>option_dual_fund</c> off: Eclair refuses a v1 open
    /// from a peer with which it negotiated <c>option_dual_fund</c> (NL-557,
    /// <c>EclairInteropTests.Given_DefaultFeatures_When_WeOpenWithAPush_Then_EclairRefusesTheV1Open</c>).
    /// </param>
    /// <param name="configureNodeOptions">Runs on the node options (after the push rule above).</param>
    /// <param name="configureNode">Runs before the node starts.</param>
    /// <param name="isPublic">An announced channel (<c>openchannel --public</c>).</param>
    public static async Task<EclairChannelSession> BuildOurFundedAsync(EclairFixture fixture, string nodeName,
                                                                       LightningMoney capacity, LightningMoney? push,
                                                                       CancellationToken cancellationToken,
                                                                       Action<NodeOptions>? configureNodeOptions = null,
                                                                       Action<NLightningTestNode>? configureNode = null,
                                                                       bool isPublic = false)
    {
        var node = await NLightningTestNode.CreateAsync(
                       fixture.Bitcoin, nodeName,
                       configureNodeOptions: o =>
                       {
                           if (push is not null)
                               o.Features.DualFund = FeatureSupport.No;
                           configureNodeOptions?.Invoke(o);
                       });
        configureNode?.Invoke(node);
        var session = new EclairChannelSession(fixture, node);
        try
        {
            await session.StartNodeAsync(cancellationToken);
            await node.FundWalletAsync(LightningMoney.Satoshis(capacity.Satoshi * 5 / 2), AddressType.P2Wpkh,
                                       cancellationToken);
            await fixture.WaitAllAtTipAsync([node], cancellationToken);
            await session.ConnectAsync(cancellationToken);

            if (push is null)
            {
                using var scope = node.Services.CreateScope();
                var handler = scope.ServiceProvider
                                   .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                        OpenChannelClientResponse>>();
                // The daemon's plain openchannel (NLightningTestNode.OpenChannelAsync would force v1)
                var response = await handler.HandleAsync(new OpenChannelClientRequest(fixture.EclairAddress, capacity)
                {
                    IsPublic = isPublic
                }, cancellationToken);
                session.ChannelId = response.ChannelId;
                Console.WriteLine($"[eclair] {nodeName} opened {response.ChannelId} with a plain openchannel");
            }
            else
            {
                var channel = await node.OpenChannelAsync(
                                  new OpenChannelClientRequest(fixture.EclairAddress, capacity)
                                  {
                                      PushAmount = push,
                                      IsPublic = isPublic
                                  }, cancellationToken);
                session.ChannelId = channel.ChannelId;
                Console.WriteLine($"[eclair] {nodeName} opened {channel.ChannelId} ({channel.ChannelPoint()}), "
                                + $"state {channel.ChannelState}");
            }

            await session.MineUntilUsableAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A node <paramref name="nodeName"/> to which Eclair opens a private anchors channel of
    /// <paramref name="capacity"/> from its own wallet (<c>open_channel2</c> when both offer <c>option_dual_fund</c>,
    /// our default), followed until both ends are usable. <paramref name="configureNode"/> runs before the node starts;
    /// <paramref name="channelType"/> is the channel type Eclair's <c>open</c> asks for (<c>simple_taproot_channel</c> for
    /// a taproot channel, NL-877).
    /// </summary>
    public static async Task<EclairChannelSession> BuildEclairFundedAsync(
        EclairFixture fixture, string nodeName, LightningMoney capacity, CancellationToken cancellationToken,
        Action<NodeOptions>? configureNodeOptions = null, Action<NLightningTestNode>? configureNode = null,
        bool announce = false, string channelType = "anchor_outputs_zero_fee_htlc_tx")
    {
        var node = await NLightningTestNode.CreateAsync(fixture.Bitcoin, nodeName,
                                                        configureNodeOptions: configureNodeOptions);
        configureNode?.Invoke(node);
        var session = new EclairChannelSession(fixture, node);
        try
        {
            await session.StartNodeAsync(cancellationToken);
            // The on-chain reserve we keep as fundee of an anchors channel (NL-379), and any dual-fund contribution
            await node.FundWalletAsync(LightningMoney.Satoshis(500_000), AddressType.P2Wpkh, cancellationToken);
            await fixture.FundEclairWalletAsync(LightningMoney.Satoshis(capacity.Satoshi * 2), [node],
                                                cancellationToken);
            await session.ConnectAsync(cancellationToken);

            var answer = await fixture.Eclair.OpenAsync(node.NodeIdHex, (long)capacity.Satoshi, cancellationToken,
                                                        channelType: channelType, announce: announce);
            Console.WriteLine($"[eclair] Eclair opened to {nodeName}: {answer}");
            session.ChannelId = ParseOpenedChannelId(answer);

            await session.MineUntilUsableAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A node <paramref name="nodeName"/> with <paramref name="walletSat"/> in its wallet, connected to Eclair, and no
    /// channel yet (the caller opens one and sets <see cref="ChannelId"/>). <paramref name="configureNode"/> runs before
    /// the node starts; the wallet is funded with one output of <paramref name="addressType"/>. The caller disposes the
    /// session.
    /// </summary>
    public static async Task<EclairChannelSession> CreateConnectedAsync(
        EclairFixture fixture, string nodeName, LightningMoney walletSat, CancellationToken cancellationToken,
        Action<NodeOptions>? configureNodeOptions = null, Action<NLightningTestNode>? configureNode = null,
        EclairEndpoint? eclair = null, AddressType addressType = AddressType.P2Wpkh)
    {
        var node = await NLightningTestNode.CreateAsync(fixture.Bitcoin, nodeName,
                                                        configureNodeOptions: configureNodeOptions);
        configureNode?.Invoke(node);
        var session = new EclairChannelSession(fixture, node, eclair);
        try
        {
            await session.StartNodeAsync(cancellationToken);
            await node.FundWalletAsync(walletSat, addressType, cancellationToken);
            await fixture.WaitAllAtTipAsync([node], cancellationToken);
            await session.ConnectAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Eclair pays our invoice (<c>payinvoice blocking=true</c>): the payment is sent with our preimage, our invoice
    /// is settled and our balance grows by the amount.
    /// </summary>
    public async Task AssertEclairPaysUsAsync(LightningMoney amount, CancellationToken ct)
    {
        var before = await GetOurChannelAsync(ct);
        var invoice = await Node.CreateInvoiceAsync(amount, $"eclair pays nltg {Guid.NewGuid():N}", ct);

        var result = await Eclair.PayInvoiceAsync(invoice.Bolt11!, ct);

        Console.WriteLine($"[eclair] Eclair payinvoice: {result.ToJsonString()}");
        Assert.Equal("payment-sent", result["type"]?.GetValue<string>());
        var preimage = Convert.FromHexString(result["paymentPreimage"]!.GetValue<string>());
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(preimage));
        var ours = await Poll.ForAsync(async () =>
        {
            var current = await Node.GetInvoiceAsync(invoice.PaymentHash, ct);
            return current?.Status == InvoiceStatus.Settled ? current : null;
        }, SettleTimeout, "our invoice settled", ct);
        Assert.Equal(amount, ours.AmountReceived);
        await WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi + amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }

    /// <summary>
    /// We pay Eclair's invoice: our payment succeeds with Eclair's preimage and Eclair lists it as received.
    /// </summary>
    public async Task AssertWePayEclairAsync(LightningMoney amount, CancellationToken ct)
    {
        var before = await GetOurChannelAsync(ct);
        var invoice = await Eclair.CreateInvoiceAsync((long)amount.MilliSatoshi, "nltg pays eclair", ct);
        var bolt11 = invoice["serialized"]!.GetValue<string>();
        var paymentHash = invoice["paymentHash"]!.GetValue<string>();

        var payment = await Node.PayInvoiceAsync(bolt11, ct);

        Console.WriteLine($"[eclair] our payment: {payment.Status}, failure {payment.FailureCode} at "
                        + $"{payment.FailureSourceIndex}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.NotNull(payment.Preimage);
        Assert.Equal(Convert.FromHexString(paymentHash), SHA256.HashData((byte[])payment.Preimage.Value));
        Assert.Equal(amount, payment.Amount);
        Assert.Equal(LightningMoney.Zero, payment.Fee);
        var received = await Poll.ForAsync(async () =>
        {
            var info = await Eclair.GetReceivedInfoAsync(paymentHash, ct);
            return info?["status"]?["type"]?.GetValue<string>() == "received" ? info : null;
        }, SettleTimeout, "Eclair lists the payment as received", ct);
        Console.WriteLine($"[eclair] Eclair received: {received["status"]!.ToJsonString()}");
        await WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi - amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }

    private async Task<(bool Ready, string Status)> CheckUsableAsync(bool requireNoHtlcs,
                                                                     CancellationToken cancellationToken)
    {
        if (!Node.IsRunning)
            return (false, "node stopped");

        var ours = (await Node.ListChannelsAsync(cancellationToken)).Channels
                                                                    .FirstOrDefault(c => c.ChannelId == ChannelId);
        var theirs = await Eclair.ChannelAsync(ChannelIdHex, cancellationToken);
        var ready = ours is not null
                 && ours.IsUsable()
                 && ours.ShortChannelId is not null
                 && theirs?["state"]?.GetValue<string>() == "NORMAL"
                 && await Eclair.IsConnectedAsync(Node.NodeIdHex, cancellationToken);
        if (requireNoHtlcs)
        {
            var htlcs = theirs?["data"]?["commitments"]?["active"]?[0]?["localCommit"]?["spec"]?["htlcs"]?.AsArray();
            ready &= ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } && (htlcs is null || htlcs.Count == 0);
        }

        return (ready, $"ours {ours?.Describe() ?? "not listed"}; eclair {DescribeEclair(theirs)}");
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(EclairAddress))
                  .WaitAsync(cancellationToken);
        await Poll.UntilAsync(async () => Node.IsConnectedTo(EclairPubKey)
                                       && await Eclair.IsConnectedAsync(Node.NodeIdHex, cancellationToken),
                              TimeSpan.FromSeconds(30), $"{Node.Name} and Eclair connected", cancellationToken);
    }

    /// <summary>Mines one block at a time until both ends agree the channel is usable.</summary>
    public async Task MineUntilUsableAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + UsableTimeout;
        while (true)
        {
            var (ready, status) = await CheckUsableAsync(true, cancellationToken);
            if (ready)
                break;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The Eclair channel was not usable on both ends in time: {status}");

            await _fixture.MineAndWaitAsync(1, [Node], cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        Console.WriteLine($"[eclair] channel ready: {await DescribeAsync(cancellationToken)}");
    }

    /// <summary>The funding txid (display order) in Eclair's <c>open</c> answer (<c>fundingTxId=...</c>).</summary>
    public static string ParseOpenedFundingTxId(string openAnswer)
    {
        var match = FundingTxIdRegex().Match(openAnswer);
        Assert.True(match.Success, $"No fundingTxId in Eclair's open answer: {openAnswer}");
        return match.Groups["txid"].Value;
    }

    [GeneratedRegex("channel (?<id>[0-9a-f]{64})")]
    private static partial Regex CreatedChannelRegex();

    [GeneratedRegex("fundingTxId=(?<txid>[0-9a-f]{64})")]
    private static partial Regex FundingTxIdRegex();
}