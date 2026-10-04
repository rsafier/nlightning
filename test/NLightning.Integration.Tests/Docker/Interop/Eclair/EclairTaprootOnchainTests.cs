using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Abcd;
using Application.Payments.Switch;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Fixtures;
using Onchain.Anchors;
using Utils;
using SpliceWireRecorder = Cln.ClnSpliceTests.SpliceWireRecorder;

/// <summary>
/// Force closes of simple taproot channels against Eclair 0.14.3, resolved on chain by our T4 code (taproot wave t03,
/// lane ECL2; NL-877, NL-966), each with an HTLC in flight in both directions: Eclair's HTLC to us is one part of a
/// multi-part payment that Eclair sends alone (<c>sendtoroute</c> with a <c>recipientAmountMsat</c> above it), so we
/// hold it waiting for the rest (<c>Node:Switch:MppTimeout</c> 1 h), and ours to Eclair is a payment of Eclair's
/// invoice whose <c>update_fulfill_htlc</c> our node never processes (the recorder drops every channel message from
/// Eclair from then on), so Eclair holds the preimage and the HTLC stays on both commitments. (a) We force close: our
/// commitment confirms, Eclair claims our offered HTLC with the preimage and our payment succeeds from it, Eclair times
/// its HTLC out at its expiry, and after Eclair's 720-block delay our <c>to_local</c> is swept into our wallet by
/// script path. (b) Eclair force closes: its commitment confirms, its HTLC-success transaction gives us the preimage of
/// our payment, its HTLC-timeout takes its own HTLC back, and our <c>to_remote</c> is swept into our wallet.
/// </summary>
/// <remarks>Run with <c>scripts/run-cluster.sh -n 1 --suite eclair --class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairTaprootOnchainTests</c>.</remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairTaprootOnchainTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 20 * 60 * 1_000;

    /// <summary>The final CLTV delta of Eclair's partial HTLC to us (our invoices ask for 40).</summary>
    private const int EclairPartFinalCltvExpiry = 60;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(180);

    private readonly EclairFixture _fixture;
    private EclairChannelSession? _session;

    public EclairTaprootOnchainTests(EclairFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;

        if (TestDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine($"[eclair] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            foreach (var row in await SafeRowsAsync(_session))
                Console.WriteLine($"[nltg] row {Describe(row)}");
            foreach (var line in _session.Node.NodeLog.TakeLast(500))
                Console.WriteLine(line);
            await _fixture.DumpEclairLogAsync(500);
        }

        await _session.DisposeAsync();
    }

    /// <summary>
    /// (a) Our <c>forceclosechannel</c> of the taproot channel Eclair opened, with Eclair's partial HTLC held by us and
    /// our HTLC whose fulfill we never processed: our commitment confirms and is recorded as ours with both HTLC rows;
    /// Eclair spends our offered HTLC output with the preimage and our payment succeeds with it; at its expiry Eclair
    /// spends our received HTLC output (its timeout) and our invoice stays unpaid; after Eclair's 720-block
    /// <c>to_self_delay</c> our <c>to_local</c> sweep confirms and pays our wallet.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_HtlcsInFlightBothWays_When_WeForceCloseATaprootChannel_Then_EachOutputIsResolvedOnChain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-tap-fc-we", ct);
        var (theirs, invoiceHash) = await HoldEclairsPartAsync(session, ct);
        var (ours, paymentHash) = await HoldOurPaymentAsync(session, wire, ct);
        var toSelfDelay = EclairTaprootTests.Model(session).ChannelParams.Remote.ToSelfDelay;

        // Act
        var closed = await EclairTaprootTests.HandleAsync<ForceCloseChannelClientRequest,
                         ForceCloseChannelClientResponse>(session, new ForceCloseChannelClientRequest(session.ChannelId),
                                                          ct);

        // Assert: our commitment, recorded as ours with both HTLC outputs
        Console.WriteLine($"[nltg] forceclosechannel: {closed.State} {closed.Status} {closed.CommitmentTxId}");
        Assert.NotNull(closed.CommitmentTxId);
        var commitmentTxId = new uint256((byte[])closed.CommitmentTxId.Value);
        await ConfirmAsync(session, commitmentTxId, ct);
        var close = await AnchorsHarness.WaitForCloseAsync(session.Node, session.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
        var offered = await WaitRowAsync(session, close.CommitmentTransactionId, OutputDescriptorKind.LocalOfferedHtlc,
                                         ct);
        var received = await WaitRowAsync(session, close.CommitmentTransactionId,
                                          OutputDescriptorKind.LocalReceivedHtlc, ct);
        var toLocal = await WaitRowAsync(session, close.CommitmentTransactionId, OutputDescriptorKind.DelayedToLocal,
                                         ct);
        Assert.Equal(ours.Id, offered.HtlcId);
        Assert.Equal(theirs.Id, received.HtlcId);

        // ...Eclair claims our HTLC with the preimage: our payment succeeds from the chain
        await AssertPaymentSucceedsFromChainAsync(session, paymentHash, commitmentTxId, offered, ct);

        // ...Eclair times its HTLC out at its expiry: our invoice is never paid
        await MineUntilHeightAsync(session, theirs.CltvExpiry, ct);
        var timeout = await MineUntilAsync(session, () => FindChainSpenderAsync(
                                                            new OutPoint(commitmentTxId, received.OutputIndex), ct),
                                           "Eclair's timeout spend of our received HTLC output", ct);
        Console.WriteLine($"[proof] Eclair's timeout of its HTLC: {timeout.GetHash()} (locktime {timeout.LockTime})");
        Assert.NotEqual(InvoiceStatus.Settled, (await session.Node.GetInvoiceAsync(invoiceHash, ct))?.Status);

        // ...and our to_local after the delay
        var commitment = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(commitmentTxId, true, ct);
        var toLocalValue = commitment.Outputs[(int)toLocal.OutputIndex].Value;
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        await MineManyAsync(session, toSelfDelay, ct);
        var sweep = await MineUntilResolvedAsync(session, toLocal, ct);
        await Poll.UntilAsync(() => Task.FromResult(AnchorsHarness.WalletBalance(session.Node) - walletBefore
                                                 >= LightningMoney.Satoshis((ulong)toLocalValue.Satoshi - 5_000)),
                              s_stepTimeout, "our wallet holds our to_local less the sweep fee", ct);
        Console.WriteLine($"[proof] to_local {toLocalValue} swept by {sweep}; wallet +"
                        + $"{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");
        AssertNoUnsupportedTaprootAlert(session);
    }

    /// <summary>
    /// (b) Eclair's <c>forceclose</c> with the same two HTLCs: Eclair's commitment confirms and is recorded as the peer's
    /// with both HTLC rows; Eclair's HTLC-success transaction spends the HTLC we offered and our payment succeeds with
    /// its preimage; Eclair's HTLC-timeout takes its own HTLC back at its expiry; our <c>to_remote</c> sweep confirms and
    /// pays our wallet.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_HtlcsInFlightBothWays_When_EclairForceClosesATaprootChannel_Then_EachOutputIsResolvedOnChain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-tap-fc-eclair", ct);
        var (theirs, invoiceHash) = await HoldEclairsPartAsync(session, ct);
        var (ours, paymentHash) = await HoldOurPaymentAsync(session, wire, ct);

        // Act
        var answer = await session.Eclair.ForceCloseAsync(session.ChannelIdHex, ct);
        Console.WriteLine($"[eclair] forceclose: {answer?.ToJsonString()}");

        // Assert: Eclair's commitment, recorded as the peer's with both HTLC outputs
        var close = await MineUntilAsync(session, async () =>
        {
            using var scope = session.Node.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<Domain.Persistence.Interfaces.IUnitOfWork>()
                              .OnchainResolutionDbRepository.GetCloseAsync(session.ChannelId);
        }, "Eclair's commitment confirmed and recorded", ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        var commitmentTxId = new uint256((byte[])close.CommitmentTransactionId);
        var offered = await WaitRowAsync(session, close.CommitmentTransactionId,
                                         OutputDescriptorKind.RemoteReceivedHtlc, ct);
        var received = await WaitRowAsync(session, close.CommitmentTransactionId,
                                          OutputDescriptorKind.RemoteOfferedHtlc, ct);
        var toRemote = await WaitRowAsync(session, close.CommitmentTransactionId,
                                          OutputDescriptorKind.PaymentToRemote, ct);
        Assert.Equal(ours.Id, offered.HtlcId);
        Assert.Equal(theirs.Id, received.HtlcId);

        // ...our to_remote sweep pays our wallet
        var commitment = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(commitmentTxId, true, ct);
        var toRemoteValue = commitment.Outputs[(int)toRemote.OutputIndex].Value;
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        var sweep = await MineUntilResolvedAsync(session, toRemote, ct);
        await Poll.UntilAsync(() => Task.FromResult(AnchorsHarness.WalletBalance(session.Node) - walletBefore
                                                 >= LightningMoney.Satoshis((ulong)toRemoteValue.Satoshi - 5_000)),
                              s_stepTimeout, "our wallet holds our to_remote less the sweep fee", ct);
        Console.WriteLine($"[proof] to_remote {toRemoteValue} swept by {sweep}; wallet +"
                        + $"{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");

        // ...Eclair's HTLC-success gives us the preimage of our payment
        await AssertPaymentSucceedsFromChainAsync(session, paymentHash, commitmentTxId, offered, ct);

        // ...and Eclair's HTLC-timeout takes its HTLC back at its expiry
        await MineUntilHeightAsync(session, theirs.CltvExpiry, ct);
        var timeout = await MineUntilAsync(session, () => FindChainSpenderAsync(
                                                            new OutPoint(commitmentTxId, received.OutputIndex), ct),
                                           "Eclair's HTLC-timeout of its HTLC", ct);
        Console.WriteLine($"[proof] Eclair's HTLC-timeout: {timeout.GetHash()} (locktime {timeout.LockTime})");
        Assert.NotEqual(InvoiceStatus.Settled, (await session.Node.GetInvoiceAsync(invoiceHash, ct))?.Status);
        AssertNoUnsupportedTaprootAlert(session);
    }

    /// <summary>
    /// The taproot channel Eclair opens to us (1M sat) with our traffic recorded, our MPP timeout at 1 h, and 300,000
    /// sat paid to us so both sides have a balance output and we can pay Eclair.
    /// </summary>
    private async Task<(EclairChannelSession Session, SpliceWireRecorder Wire)> BuildAsync(string nodeName,
        CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _session = await EclairChannelSession.BuildEclairFundedAsync(
                       _fixture, nodeName, s_capacity, ct, EclairTaprootTests.EnableTaproot,
                       configureNode: node => node.ConfigureServices = services =>
                       {
                           wire.Install(services);
                           services.Configure<HtlcSwitchOptions>(o => o.MppTimeout = TimeSpan.FromHours(1));
                       },
                       channelType: EclairTaprootTests.EclairTaprootChannelType);
        await EclairTaprootTests.AssertTaprootChannelAsync(_fixture, _session, weAreInitiator: false, s_capacity, ct);
        await _session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(300_000), ct);
        return (_session, wire);
    }

    /// <summary>
    /// Eclair sends one 40,000 sat part of our 100,000 sat invoice alone (<c>sendtoroute</c>): we hold it, waiting for
    /// the rest. Returns the HTLC once it is on both our commitments, and the invoice's payment hash.
    /// </summary>
    private async Task<(HtlcRecord Htlc, Hash PaymentHash)> HoldEclairsPartAsync(EclairChannelSession session,
                                                                                 CancellationToken ct)
    {
        var invoice = await session.Node.CreateInvoiceAsync(LightningMoney.Satoshis(100_000), "held by nltg", ct);
        var answer = await session.Eclair.SendToRouteAsync(invoice.Bolt11!, [session.EclairPubKeyHex,
                                                                             session.Node.NodeIdHex],
                                                           40_000_000, 100_000_000, EclairPartFinalCltvExpiry, ct);
        Console.WriteLine($"[eclair] sendtoroute (one part of 40,000 of 100,000 sat): {answer?.ToJsonString()}");
        var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(session.Node, session.ChannelId,
                                                                         HtlcDirection.Incoming, ct);
        Console.WriteLine($"[proof] Eclair's part held: id {htlc.Id}, {htlc.AmountMsat} msat, expiry {htlc.CltvExpiry}");
        Assert.Equal(40_000_000UL, htlc.AmountMsat);
        Assert.Equal(invoice.PaymentHash, htlc.PaymentHash);
        return (htlc, invoice.PaymentHash);
    }

    /// <summary>
    /// We pay Eclair's 50,000 sat invoice and drop every channel message Eclair sends from its
    /// <c>update_fulfill_htlc</c> on: our HTLC stays on both commitments while Eclair knows the preimage. Returns the
    /// HTLC and the payment hash.
    /// </summary>
    private static async Task<(HtlcRecord Htlc, Hash PaymentHash)> HoldOurPaymentAsync(EclairChannelSession session,
        SpliceWireRecorder wire, CancellationToken ct)
    {
        var deaf = 0;
        wire.DropInbound = type =>
        {
            if (type == (ushort)MessageTypes.UpdateFulfillHtlc)
                Volatile.Write(ref deaf, 1);
            return Volatile.Read(ref deaf) == 1 && type is not ((ushort)MessageTypes.Ping or (ushort)MessageTypes.Pong);
        };
        var invoice = await session.Eclair.CreateInvoiceAsync(50_000_000, "held by eclair", ct);
        var paymentHash = new Hash(Convert.FromHexString(invoice["paymentHash"]!.GetValue<string>()));
        var from = wire.CurrentSequence;
        _ = session.Node.PayInvoiceAsync(invoice["serialized"]!.GetValue<string>(), ct, 10);
        var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(session.Node, session.ChannelId,
                                                                         HtlcDirection.Outgoing, ct);
        await Poll.ForAsync(() => Task.FromResult(wire.FirstOrDefault(inbound: true, MessageTypes.UpdateFulfillHtlc,
                                                                      from)),
                            s_stepTimeout, "Eclair's update_fulfill_htlc (dropped)", ct);
        Console.WriteLine($"[proof] our HTLC held by Eclair: id {htlc.Id}, {htlc.AmountMsat} msat, expiry "
                        + $"{htlc.CltvExpiry}; its fulfill was dropped");
        Assert.Equal(paymentHash, htlc.PaymentHash);
        return (htlc, paymentHash);
    }

    /// <summary>
    /// Mines until our payment <paramref name="paymentHash"/> succeeded with a preimage read from the chain, and the
    /// output of <paramref name="row"/> was spent by a transaction that carries that preimage.
    /// </summary>
    private async Task AssertPaymentSucceedsFromChainAsync(EclairChannelSession session, Hash paymentHash,
                                                           uint256 commitmentTxId, OutputResolutionModel row,
                                                           CancellationToken ct)
    {
        var payment = await MineUntilAsync(session, async () =>
                                               await session.Node.GetPaymentAsync(paymentHash, ct) is
                                               { Status: PaymentStatus.Succeeded } p
                                                   ? p
                                                   : null,
                                           "our payment succeeded from the on-chain preimage", ct);
        Assert.NotNull(payment.Preimage);
        var preimage = (byte[])payment.Preimage.Value;
        Assert.Equal((byte[])paymentHash, SHA256.HashData(preimage));
        var spender = await MineUntilAsync(session, () => FindChainSpenderAsync(
                                                         new OutPoint(commitmentTxId, row.OutputIndex), ct),
                                           "Eclair's preimage spend of our HTLC output confirmed", ct);
        Assert.Contains(spender.Inputs, i => i.WitScript.Pushes.Any(p => p.SequenceEqual(preimage)));
        Console.WriteLine($"[proof] our payment succeeded from Eclair's preimage spend {spender.GetHash()}");
    }

    /// <summary>No output was left to the operator as an unsupported taproot one (the NL-966 safety floor's alert).</summary>
    private static void AssertNoUnsupportedTaprootAlert(EclairChannelSession session) =>
        Assert.Equal(0, session.Node.CountLogLines("[NL-966]"));

    /// <summary>The confirmed transaction that spends <paramref name="outPoint"/>, or null.</summary>
    private async Task<Transaction?> FindChainSpenderAsync(OutPoint outPoint, CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var tip = await rpc.GetBlockCountAsync(ct);
        for (var height = tip; height > tip - 30 && height > 0; height--)
        {
            var block = await rpc.GetBlockAsync(height, ct);
            var spender = block.Transactions.FirstOrDefault(t => t.Inputs.Any(i => i.PrevOut == outPoint));
            if (spender is not null)
                return spender;
        }

        return null;
    }

    /// <summary>Mines until the chain is at least at <paramref name="height"/>.</summary>
    private async Task MineUntilHeightAsync(EclairChannelSession session, uint height, CancellationToken ct)
    {
        var tip = await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);
        if (height > tip)
            await MineManyAsync(session, (int)(height - tip), ct);
    }

    /// <summary>Mines one block at a time until <paramref name="txId"/> is confirmed.</summary>
    private async Task ConfirmAsync(EclairChannelSession session, uint256 txId, CancellationToken ct)
    {
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(txId),
                              s_stepTimeout, $"{txId} in the mempool", ct);
        await _fixture.MineAndWaitAsync(1, [session.Node], ct);
        var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(txId, ct);
        Assert.True(info.Confirmations >= 1, $"{txId} is not confirmed");
    }

    private async Task<T> MineUntilAsync<T>(EclairChannelSession session, Func<Task<T?>> probe, string what,
                                            CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + s_stepTimeout;
        while (true)
        {
            if (await probe() is { } found)
                return found;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Not in time: {what}");

            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>Mines <paramref name="blocks"/> blocks in batches, waiting for our node after each.</summary>
    private async Task MineManyAsync(EclairChannelSession session, int blocks, CancellationToken ct)
    {
        for (var left = blocks; left > 0; left -= 100)
        {
            await _fixture.MineAsync(Math.Min(100, left), ct);
            await _fixture.WaitAllAtTipAsync([session.Node], ct, TimeSpan.FromMinutes(3));
        }
    }

    /// <summary>
    /// Mines one block at a time until our node recorded a resolving transaction for <paramref name="row"/>'s output and
    /// it confirmed; returns its txid.
    /// </summary>
    private async Task<uint256> MineUntilResolvedAsync(EclairChannelSession session, OutputResolutionModel row,
                                                       CancellationToken ct)
    {
        var resolving = await MineUntilAsync(session, async () =>
        {
            var rows = await AnchorsHarness.GetRowsAsync(session.Node, session.ChannelId);
            var current = rows.FirstOrDefault(r => r.TransactionId == row.TransactionId
                                                && r.OutputIndex == row.OutputIndex);
            return current?.ResolvingTransactionId is { } txId ? new uint256((byte[])txId) : null;
        }, $"a resolving transaction for output {row.OutputIndex}", ct);
        await MineUntilAsync(session, async () =>
        {
            try
            {
                var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(resolving, ct);
                return info.Confirmations >= 1 ? info : null;
            }
            catch (NBitcoin.RPC.RPCException)
            {
                return null;
            }
        }, $"{resolving} confirmed", ct);
        return resolving;
    }

    private static async Task<OutputResolutionModel> WaitRowAsync(EclairChannelSession session, TxId commitment,
                                                                  OutputDescriptorKind kind, CancellationToken ct) =>
        await AnchorsHarness.WaitForRowAsync(session.Node, session.ChannelId,
                                             r => r.TransactionId == commitment && r.Descriptor == kind,
                                             $"our {kind} row", ct);

    private static async Task<IReadOnlyList<OutputResolutionModel>> SafeRowsAsync(EclairChannelSession session)
    {
        try
        {
            return await AnchorsHarness.GetRowsAsync(session.Node, session.ChannelId);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string Describe(OutputResolutionModel row) =>
        $"{row.Descriptor} {new uint256((byte[])row.TransactionId)}:{row.OutputIndex} {row.State} htlc {row.HtlcId} "
      + $"resolving {(row.ResolvingTransactionId is { } r ? new uint256((byte[])r) : null)}";
}