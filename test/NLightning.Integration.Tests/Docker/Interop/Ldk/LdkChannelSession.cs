using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Abcd;
using Cln;
using Domain.Bitcoin.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Payments.Enums;
using Fixtures;
using Onchain.Anchors;
using Utils;

/// <summary>
/// One channel between an in-process node and ldk-server (<see cref="LdkFixture"/>), as the Eclair and CLN sessions
/// do: the shared channel we fund (<see cref="GetAsync"/>, 1M sat, 300k pushed) and separate ones built by
/// <see cref="BuildOurFundedAsync"/> and <see cref="BuildLdkFundedAsync"/>, followed until both ends are usable. LDK
/// has no dual funding (NL-556), so every open is v1.
/// </summary>
public sealed class LdkChannelSession : IAsyncDisposable
{
    public static readonly LightningMoney Capacity = LightningMoney.Satoshis(1_000_000);
    public static readonly LightningMoney SharedPush = LightningMoney.Satoshis(300_000);
    public static readonly TimeSpan UsableTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    private const string CacheKey = "ldk-channel-session";
    private const int PayTimeoutSeconds = 60;

    private readonly LdkFixture _fixture;

    private LdkChannelSession(LdkFixture fixture, NLightningTestNode node)
    {
        _fixture = fixture;
        Node = node;
        Sent = new ChannelMessageRecorder(node.Name);
    }

    public NLightningTestNode Node { get; }

    public ChannelMessageRecorder Sent { get; }

    public ChannelId ChannelId { get; private set; }

    public string ChannelIdHex => ChannelId.ToString();

    /// <summary>LDK's <c>user_channel_id</c> of the channel (what <c>close-channel</c> takes).</summary>
    public string UserChannelId { get; private set; } = string.Empty;

    public LdkClient Ldk => _fixture.Ldk;

    public CompactPubKey LdkPubKey => Convert.FromHexString(_fixture.LdkNodeId);

    /// <summary>The shared channel we fund, built once per fixture on its own token.</summary>
    public static Task<LdkChannelSession> GetAsync(LdkFixture fixture, CancellationToken cancellationToken) =>
        ClnChannelSession.GetOrBuildDetachedAsync(factory => fixture.GetOrCreateAsync(CacheKey, factory),
                                                  ct => BuildOurFundedAsync(fixture, "nltg", Capacity, SharedPush, ct),
                                                  BuildTimeout, "LDK interop channel", cancellationToken);

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

    public async Task<JsonNode> GetLdkChannelAsync(CancellationToken cancellationToken) =>
        await Ldk.GetChannelAsync(ChannelIdHex, cancellationToken)
     ?? throw new InvalidOperationException($"LDK does not list the channel {ChannelIdHex}");

    /// <summary>Waits until our end is usable and LDK's <c>is_usable</c> (which needs us connected).</summary>
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
            }, UsableTimeout, "the LDK channel usable on both ends", cancellationToken, TimeSpan.FromMilliseconds(500));
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
            theirs = DescribeLdk(await Ldk.GetChannelAsync(ChannelIdHex, cancellationToken));
        }
        catch (Exception e)
        {
            theirs = $"unavailable: {e.Message}";
        }

        return $"ours {ours}; ldk {theirs}";
    }

    public static string DescribeLdk(JsonNode? channel) =>
        channel is null
            ? "not listed"
            : $"ready={channel["is_channel_ready"]} usable={channel["is_usable"]} "
            + $"outbound={channel["outbound_capacity_msat"]} inbound={channel["inbound_capacity_msat"]} "
            + $"confirmations={channel["confirmations"]}/{channel["confirmations_required"]} "
            + $"scid={channel["short_channel_id"]} channel_type={channel["channel_type"]?.ToJsonString()}";

    /// <summary>LDK's channel type (a map of feature bit to feature) has <c>option_anchors</c> (22/23).</summary>
    public static bool IsLdkAnchors(JsonNode channel) =>
        channel["channel_type"] is JsonObject type && (type.ContainsKey("22") || type.ContainsKey("23"));

    public async ValueTask DisposeAsync()
    {
        Sent.Dispose();
        await Node.DisposeAsync();
    }

    /// <summary>
    /// A node <paramref name="nodeName"/> that opens a private v1 channel of <paramref name="capacity"/> to LDK
    /// (<paramref name="push"/> pushed), followed until both ends are usable. LDK's wallet is funded first: it refuses
    /// an inbound anchors channel it cannot back with its on-chain reserve. <paramref name="configureNode"/> runs before
    /// the node starts (e.g. to set <see cref="NLightningTestNode.ConfigureServices"/>). <paramref name="isPublic"/>
    /// opens a public channel (<c>openchannel --public</c>; LDK accepts it since the fixture gives LDK an alias,
    /// NL-556).
    /// </summary>
    public static async Task<LdkChannelSession> BuildOurFundedAsync(LdkFixture fixture, string nodeName,
                                                                    LightningMoney capacity, LightningMoney? push,
                                                                    CancellationToken cancellationToken,
                                                                    Action<NLightningTestNode>? configureNode = null,
                                                                    bool isPublic = false)
    {
        var node = await NLightningTestNode.CreateAsync(fixture.Bitcoin, nodeName);
        configureNode?.Invoke(node);
        var session = new LdkChannelSession(fixture, node);
        try
        {
            await session.StartNodeAsync(cancellationToken);
            await node.FundWalletAsync(LightningMoney.Satoshis(capacity.Satoshi * 5 / 2), AddressType.P2Wpkh,
                                       cancellationToken);
            if (await fixture.SpendableSatAsync(cancellationToken) < 100_000)
                await fixture.FundLdkWalletAsync(LightningMoney.Satoshis(200_000), [node], cancellationToken);
            await fixture.WaitAllAtTipAsync([node], cancellationToken);
            await session.ConnectAsync(cancellationToken);

            var channel = await node.OpenChannelAsync(
                              new OpenChannelClientRequest(fixture.LdkAddress, capacity)
                              {
                                  PushAmount = push,
                                  IsPublic = isPublic
                              },
                              cancellationToken);
            session.ChannelId = channel.ChannelId;
            Console.WriteLine($"[ldk] {nodeName} opened {channel.ChannelId} ({channel.ChannelPoint()}), "
                            + $"state {channel.ChannelState}");
            var ldkChannel = await Poll.ForAsync(() => fixture.Ldk.GetChannelAsync(session.ChannelIdHex,
                                                                                   cancellationToken),
                                                 TimeSpan.FromSeconds(30), "LDK lists our channel",
                                                 cancellationToken);
            session.UserChannelId = ldkChannel["user_channel_id"]!.GetValue<string>();

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
    /// A node <paramref name="nodeName"/> (listening on every interface) to which LDK opens a private channel of
    /// <paramref name="capacity"/> from its own wallet (v1: LDK has no dual funding), followed until both ends are
    /// usable. <paramref name="configureNode"/> runs before the node starts. <paramref name="announce"/> makes it a public
    /// channel (<c>open-channel --announce-channel</c>, NL-556).
    /// </summary>
    public static async Task<LdkChannelSession> BuildLdkFundedAsync(LdkFixture fixture, string nodeName,
                                                                    LightningMoney capacity,
                                                                    CancellationToken cancellationToken,
                                                                    Action<NodeOptions>? configureNodeOptions = null,
                                                                    Action<NLightningTestNode>? configureNode = null,
                                                                    bool announce = false)
    {
        var node = await NLightningTestNode.CreateAsync(
                       fixture.Bitcoin, nodeName, configureNodeOptions: o =>
                       {
                           o.ListenAddresses = o.ListenAddresses.Select(a => a.Replace("127.0.0.1", "0.0.0.0"))
                                                .ToList();
                           configureNodeOptions?.Invoke(o);
                       });
        configureNode?.Invoke(node);
        var session = new LdkChannelSession(fixture, node);
        try
        {
            await session.StartNodeAsync(cancellationToken);
            // The on-chain reserve we keep as fundee of an anchors channel (NL-379)
            await node.FundWalletAsync(LightningMoney.Satoshis(300_000), AddressType.P2Wpkh, cancellationToken);
            await fixture.FundLdkWalletAsync(LightningMoney.Satoshis(capacity.Satoshi * 2), [node],
                                             cancellationToken);

            var ourAddress = $"{fixture.HostAddressForLdk}:{node.Port}";
            session.UserChannelId = announce
                                        ? await fixture.Ldk.OpenAnnouncedChannelAsync(
                                              node.NodeIdHex, ourAddress, (long)capacity.Satoshi, cancellationToken)
                                        : await fixture.Ldk.OpenChannelAsync(node.NodeIdHex, ourAddress,
                                                                             (long)capacity.Satoshi, null,
                                                                             cancellationToken);
            Console.WriteLine($"[ldk] LDK opened to {nodeName}: user_channel_id {session.UserChannelId}");
            // LDK lists the final channel id once the funding is created (before, the temporary one)
            var ldkChannel = await Poll.ForAsync(async () =>
            {
                var channels = await fixture.Ldk.ListChannelsAsync(cancellationToken);
                return channels.FirstOrDefault(c => c?["user_channel_id"]?.GetValue<string>()
                                                 == session.UserChannelId
                                                 && c["funding_txo"] is not null);
            }, TimeSpan.FromSeconds(60), "LDK's channel funded", cancellationToken);
            session.ChannelId = Convert.FromHexString(ldkChannel["channel_id"]!.GetValue<string>());

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
    /// LDK pays our invoice (<c>pay --wait</c>): the payment succeeds with our preimage, our invoice is settled and our
    /// balance grows by the amount.
    /// </summary>
    public async Task AssertLdkPaysUsAsync(LightningMoney amount, CancellationToken ct)
    {
        var before = await GetOurChannelAsync(ct);
        var invoice = await Node.CreateInvoiceAsync(amount, $"ldk pays nltg {Guid.NewGuid():N}", ct);

        var payment = await Ldk.PayAsync(invoice.Bolt11!, PayTimeoutSeconds, ct);

        Console.WriteLine($"[ldk] LDK pay: {payment.ToJsonString()}");
        var details = payment["payment"] ?? payment;
        Assert.Equal("SUCCEEDED", details["status"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        var preimage = Convert.FromHexString(FindString(details, "preimage")
                                          ?? throw new InvalidOperationException("LDK reported no preimage"));
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
    /// We pay LDK's invoice (<c>bolt11-receive</c>): our payment succeeds with LDK's preimage and LDK lists the payment
    /// as succeeded.
    /// </summary>
    public async Task AssertWePayLdkAsync(LightningMoney amount, CancellationToken ct)
    {
        var before = await GetOurChannelAsync(ct);
        var invoice = await Ldk.Bolt11ReceiveAsync((long)amount.Satoshi, "nltg pays ldk", ct);
        var bolt11 = invoice["invoice"]!.GetValue<string>();
        var paymentHash = invoice["payment_hash"]!.GetValue<string>();

        var payment = await Node.PayInvoiceAsync(bolt11, ct);

        Console.WriteLine($"[ldk] our payment: {payment.Status}, failure {payment.FailureCode} at "
                        + $"{payment.FailureSourceIndex}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.NotNull(payment.Preimage);
        Assert.Equal(Convert.FromHexString(paymentHash), SHA256.HashData((byte[])payment.Preimage.Value));
        Assert.Equal(amount, payment.Amount);
        Assert.Equal(LightningMoney.Zero, payment.Fee);
        var lastSeen = "nothing";
        JsonNode received;
        try
        {
            received = await Poll.ForAsync(async () =>
            {
                var details = await Ldk.FindPaymentByHashAsync(paymentHash, ct);
                lastSeen = details?.ToJsonString() ?? "not listed";
                return string.Equals(details?["status"]?.GetValue<string>(), "SUCCEEDED",
                                     StringComparison.OrdinalIgnoreCase)
                    && string.Equals(details?["direction"]?.GetValue<string>(), "INBOUND",
                                     StringComparison.OrdinalIgnoreCase)
                           ? details
                           : null;
            }, SettleTimeout, "LDK lists the payment as received", ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}. LDK's payment: {lastSeen}", e);
        }

        Console.WriteLine($"[ldk] LDK received: {received.ToJsonString()}");
        await WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi - amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }

    /// <summary>
    /// We pay a hold invoice of LDK's (<c>bolt11-receive-for-hash</c>, <paramref name="amountSat"/>) and return once LDK
    /// holds the HTLC (its inbound payment listed <c>PENDING</c>) and it is in both our commitments. LDK settles it on
    /// <see cref="LdkClient.Bolt11ClaimForIdAsync"/> with <see cref="HeldHtlc.LdkPaymentId"/> (NL-556).
    /// </summary>
    public async Task<HeldHtlc> SendHeldHtlcAsync(string description, CancellationToken ct, long amountSat = 30_000)
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hashHex = Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
        var invoice = await Ldk.Bolt11ReceiveForHashAsync(hashHex, amountSat, description, ct);

        var inFlight = await Node.PayInvoiceAsync(invoice, ct, timeoutSeconds: 2);
        Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
        var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(Node, ChannelId, HtlcDirection.Outgoing, ct);
        var held = await Poll.ForAsync(async () =>
        {
            var payment = await Ldk.FindPaymentByHashAsync(hashHex, ct);
            return string.Equals(payment?["direction"]?.ToString(), "INBOUND", StringComparison.OrdinalIgnoreCase)
                       ? payment
                       : null;
        }, SettleTimeout, "LDK holds our HTLC", ct);
        Console.WriteLine($"[ldk] LDK holds our HTLC {htlc.Id} (cltv_expiry {htlc.CltvExpiry}): "
                        + held.ToJsonString());
        return new HeldHtlc(preimage, hashHex, held["payment_id"]!.GetValue<string>(), htlc);
    }

    /// <summary>
    /// An HTLC of ours that LDK holds: the preimage, the payment hash (hex), LDK Node's <c>payment_id</c> of the held
    /// payment (not the hash for a hold invoice) and our HTLC record.
    /// </summary>
    public sealed record HeldHtlc(byte[] Preimage, string HashHex, string LdkPaymentId, HtlcRecord Htlc)
    {
        public Hash PaymentHash => new(SHA256.HashData(Preimage));
    }

    /// <summary>The first string property named <paramref name="name"/> anywhere in <paramref name="node"/>.</summary>
    public static string? FindString(JsonNode? node, string name)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == name && value is JsonValue v && v.TryGetValue<string>(out var s))
                        return s;

                    if (FindString(value, name) is { } found)
                        return found;
                }

                return null;
            case JsonArray array:
                return array.Select(item => FindString(item, name)).FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }

    private async Task<(bool Ready, string Status)> CheckUsableAsync(bool requireNoHtlcs,
                                                                     CancellationToken cancellationToken)
    {
        if (!Node.IsRunning)
            return (false, "node stopped");

        var ours = (await Node.ListChannelsAsync(cancellationToken)).Channels
                                                                    .FirstOrDefault(c => c.ChannelId == ChannelId);
        var theirs = await Ldk.GetChannelAsync(ChannelIdHex, cancellationToken);
        var ready = ours is not null
                 && ours.IsUsable()
                 && ours.ShortChannelId is not null
                 && theirs?["is_usable"]?.GetValue<bool>() == true;
        if (requireNoHtlcs)
            ready &= ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 };

        return (ready, $"ours {ours?.Describe() ?? "not listed"}; ldk {DescribeLdk(theirs)}");
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.LdkAddress))
                  .WaitAsync(cancellationToken);
        await Poll.UntilAsync(async () => Node.IsConnectedTo(LdkPubKey)
                                       && await Ldk.IsConnectedAsync(Node.NodeIdHex, cancellationToken),
                              TimeSpan.FromSeconds(30), $"{Node.Name} and LDK connected", cancellationToken);
    }

    /// <summary>Mines one block at a time until both ends agree the channel is usable.</summary>
    private async Task MineUntilUsableAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + UsableTimeout;
        while (true)
        {
            var (ready, status) = await CheckUsableAsync(true, cancellationToken);
            if (ready)
                break;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The LDK channel was not usable on both ends in time: {status}");

            await _fixture.MineAndWaitAsync(1, [Node], cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        Console.WriteLine($"[ldk] channel ready: {await DescribeAsync(cancellationToken)}");
    }
}