namespace NLightning.Integration.Tests.Docker.Taproot;

using Abcd;
using Day0;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Utils;

/// <summary>
/// NL-1090 regression on regtest: two NLightning nodes on a private simple taproot channel (alice funds), bob issues
/// hold invoices and alice pays them. Settling a held set used to mark its part through a rebuilt commitment snapshot
/// that lost alice's taproot verification nonce, so bob could not sign his fulfill, nor alice's next payment, until a
/// reconnection (live on Mutinynet, 2026-10-05). Here the settle, a cancel and a plain payment that follows them each
/// leave both commitments without HTLCs on the same connection.
/// </summary>
/// <remarks>In the <c>taproot</c> suite (<c>scripts/run-cluster.sh --suite taproot</c>) for its bitcoind; our nodes
/// never connect to its LND.</remarks>
[Collection(LndTaprootRegtestCollection.Name)]
public sealed class TaprootHoldInvoiceFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_amount = LightningMoney.Satoshis(60_000);

    private readonly LndTaprootNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public TaprootHoldInvoiceFlowTests(LndTaprootNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
            foreach (var node in _nodes)
            {
                Console.WriteLine($"===== {node.Name}: last log lines =====");
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            }

        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    [Fact]
    public async Task Given_ATaprootChannelBetweenTwoNLightningNodes_When_HoldInvoicesAreSettledAndCanceled_Then_EveryHtlcIsCommittedAwayWithoutAReconnection()
    {
        // Arrange: alice opens a private taproot channel to bob
        var ct = TestContext.Current.CancellationToken;
        var alice = await StartNodeAsync("thold-alice", ct);
        var bob = await StartNodeAsync("thold-bob", ct);
        await alice.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        // bob backs the anchors reserve of the channel he accepts (NL-379)
        await bob.FundWalletAsync(LightningMoney.Satoshis(500_000), AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(alice, bob, ct);
        var opened = await alice.OpenChannelAsync(new OpenChannelClientRequest(bob.Address, s_capacity)
        {
            IsSimpleTaproot = true
        }, ct);
        var channelId = opened.ChannelId;
        await Day0Harness.MineUntilUsableAsync(_fixture, [], alice, bob, channelId, ct);
        Assert.Equal(CommitmentFormat.SimpleTaproot, (await alice.GetChannelAsync(channelId, ct)).ChannelType);
        // One connection (a PeerModel per connection) for the whole test: nothing below may heal by a reestablish
        var connection = alice.PeerManager.GetPeer(bob.NodeId);
        Assert.NotNull(connection);

        // Act 1: alice pays bob's hold invoice, bob settles it
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var settledHash = new Hash(paymentHash);
        var payment = await PayUntilHeldAsync(alice, bob, settledHash, "nl1090 settle", ct);
        await bob.SettleHoldInvoiceAsync(settledHash, new Secret(preimage), ct);

        // Assert 1: alice has the preimage and the fulfill leaves both commitments on this connection
        var paid = await payment;
        Console.WriteLine($"alice's payment of the settled hold: {paid.Status} {paid.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, paid.Status);
        await WaitNoHtlcsAsync(alice, bob, channelId, ct);

        // Act 2: a second hold is paid (it must lock in at bob) and canceled
        var (_, canceledHashBytes) = LndTestHelpers.NewPreimage();
        var canceledHash = new Hash(canceledHashBytes);
        payment = await PayUntilHeldAsync(alice, bob, canceledHash, "nl1090 cancel", ct);
        await bob.CancelHoldInvoiceAsync(canceledHash, ct);

        // Assert 2
        var failed = await payment;
        Console.WriteLine($"alice's payment of the canceled hold: {failed.Status} {failed.FailureReason}");
        Assert.Equal(PaymentStatus.Failed, failed.Status);
        await WaitNoHtlcsAsync(alice, bob, channelId, ct);

        // Act 3 / Assert 3: a plain payment still goes through and settles on both sides
        var invoice = await bob.CreateInvoiceAsync(s_amount, "nl1090 after", ct);
        var plain = await alice.PayInvoiceAsync(invoice.Bolt11!, ct);
        Assert.Equal(PaymentStatus.Succeeded, plain.Status);
        await WaitNoHtlcsAsync(alice, bob, channelId, ct);
        Assert.Same(connection, alice.PeerManager.GetPeer(bob.NodeId));
    }

    private async Task<NLightningTestNode> StartNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, configureNodeOptions: o =>
        {
            o.Features.AllowExperimentalFeatures = true;
            o.Features.OptionSimpleTaproot = FeatureSupport.Optional;
        });
        _nodes.Add(node);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>
    /// bob issues a hold invoice for <paramref name="paymentHash"/> and alice starts paying it; returns alice's
    /// payment (it completes on the settle or cancel) once the set is held at bob — its HTLC locked in.
    /// </summary>
    private static async Task<Task<PaymentInfoClientResponse>> PayUntilHeldAsync(
        NLightningTestNode alice, NLightningTestNode bob, Hash paymentHash, string description, CancellationToken ct)
    {
        var invoice = await bob.CreateHoldInvoiceAsync(paymentHash, s_amount, description, ct);
        var payment = alice.PayInvoiceAsync(invoice.Bolt11!, ct, timeoutSeconds: 120);
        await Poll.ForAsync(async () =>
        {
            if (payment.IsCompleted)
                throw new InvalidOperationException(
                    $"alice's payment ended before the hold: {(await payment).Status} {(await payment).FailureReason}");
            var stored = await bob.GetInvoiceAsync(paymentHash, ct);
            return stored?.Status == InvoiceStatus.Held ? stored : null;
        }, s_timeout, $"bob's hold invoice {description} held", ct);
        return payment;
    }

    private static async Task WaitNoHtlcsAsync(NLightningTestNode alice, NLightningTestNode bob, ChannelId channelId,
                                               CancellationToken ct)
    {
        foreach (var node in new[] { alice, bob })
            await Poll.UntilAsync(async () =>
            {
                var channel = await node.GetChannelAsync(channelId, ct);
                Console.WriteLine($"[{node.Name}] {channel.Describe()}");
                return channel.OfferedHtlcCount + channel.ReceivedHtlcCount == 0;
            }, s_timeout, $"{node.Name}: no HTLC left on channel {channelId}", ct);
    }
}