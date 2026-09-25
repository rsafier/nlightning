using System.Security.Cryptography;
using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Domain.Client.Responses;
using Fixtures;
using Utils;

/// <summary>
/// The shared frame of the ABCD test classes: the fixture's <see cref="AbcdNetwork"/> made ready before each test
/// (<see cref="AbcdNetwork.PrepareForPaymentAsync"/>), the failure dump after it, and the assertions every variant
/// repeats.
/// </summary>
/// <remarks>
/// Every class using it must carry <c>[Collection(LightningRegtestNetworkFixtureCollection.Name)]</c>: the network
/// lives on the collection fixture and the Docker classes of that collection never run in parallel.
/// </remarks>
public abstract class AbcdTestBase : IAsyncLifetime
{
    /// <summary>
    /// Roadmap §3: 6 minutes per test.
    /// </summary>
    protected const int TestTimeoutMs = 6 * 60 * 1_000;

    /// <summary>
    /// A payment whose HTLCs are held (hold invoice, restarts) needs more than the default payment wait.
    /// </summary>
    protected static readonly TimeSpan HeldPaymentTimeout = TimeSpan.FromMinutes(4);

    protected static readonly TimeSpan PeerDropTimeout = TimeSpan.FromSeconds(30);

    protected AbcdTestBase(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        Fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    protected LightningRegtestNetworkFixture Fixture { get; }

    protected AbcdNetwork Network { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Network = await AbcdNetwork.GetAsync(Fixture, ct);
        await Network.PrepareForPaymentAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            if (Network is not null)
                await Network.DumpDiagnosticsAsync();
            else
                await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The current block height (tests that hold an HTLC assert nothing was mined meanwhile).
    /// </summary>
    protected async Task<int> GetTipAsync(CancellationToken ct) => await Fixture.Bitcoin.GetBlockCountAsync(ct);

    /// <summary>
    /// The attempt that settled <paramref name="payment"/> (max_parts=1, so exactly one).
    /// </summary>
    protected static HTLCAttempt SucceededAttempt(Payment payment) =>
        Assert.Single(payment.Htlcs, h => h.Status == HTLCAttempt.Types.HTLCStatus.Succeeded);

    /// <summary>
    /// Asserts a payment Alice made to David through Bob and Carol: succeeded with David's preimage, David's invoice
    /// settled for exactly <paramref name="fees"/>.AmountMsat, LND's fee and route match BOLT 7 fees with our
    /// policies, and every channel end moved by exactly the forwarded amounts.
    /// </summary>
    protected async Task AssertPaidThroughBobAndCarolAsync(Payment payment, byte[] paymentHash,
                                                           AbcdPathFees fees, AbcdSnapshot before,
                                                           CancellationToken ct)
    {
        Console.WriteLine($"Payment {payment.PaymentHash}: {payment.Status}, fee {payment.FeeMsat} msat, "
                        + $"reason {payment.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);

        var invoice = await LndTestHelpers.WaitForInvoiceStateAsync(Network.David, paymentHash,
                                                                    Invoice.Types.InvoiceState.Settled,
                                                                    AbcdNetwork.SettleTimeout, ct);
        Assert.Equal(Convert.ToHexString(invoice.RPreimage.ToByteArray()), payment.PaymentPreimage,
                     StringComparer.OrdinalIgnoreCase);
        Assert.Equal(paymentHash, SHA256.HashData(invoice.RPreimage.ToByteArray()));
        Assert.Equal(fees.AmountMsat, invoice.AmtPaidMsat);

        // LND's own fee maths over the hinted edges equals BOLT 7 with Bob's and Carol's policies
        Assert.Equal(fees.TotalFeeMsat, payment.FeeMsat);
        var hops = SucceededAttempt(payment).Route.Hops;
        Assert.Equal(3, hops.Count);
        Assert.Equal(fees.FeeBobMsat, hops[0].FeeMsat);
        Assert.Equal(fees.FeeCarolMsat, hops[1].FeeMsat);
        Assert.Equal(Network.AliceBob.ShortChannelId, hops[0].ChanId);
        Assert.Equal(Network.BobCarol.ShortChannelId, hops[1].ChanId);
        Assert.Equal(Network.CarolDavid.ShortChannelId, hops[2].ChanId);

        await Network.WaitNoPendingHtlcsAsync(ct);
        var after = await Network.SnapshotAsync(ct);
        Console.WriteLine($"Before: {before}");
        Console.WriteLine($"After:  {after}");

        // Ours, to the msat (gross balances; nothing is pending any more)
        Assert.Equal(fees.AmountAliceToBobMsat, LocalDeltaMsat(before.BobAliceBob, after.BobAliceBob));
        Assert.Equal(-fees.AmountBobToCarolMsat, LocalDeltaMsat(before.BobBobCarol, after.BobBobCarol));
        Assert.Equal(fees.AmountBobToCarolMsat, LocalDeltaMsat(before.CarolBobCarol, after.CarolBobCarol));
        Assert.Equal(-fees.AmountMsat, LocalDeltaMsat(before.CarolCarolDavid, after.CarolCarolDavid));

        // LND's, in sat (to_local is rounded down to the sat), within 1 sat
        AssertWithinOneSat(-fees.AmountAliceToBobMsat, before.AliceAliceBob.LocalBalance,
                           after.AliceAliceBob.LocalBalance, "alice A-B");
        AssertWithinOneSat(fees.AmountMsat, before.DavidCarolDavid.LocalBalance, after.DavidCarolDavid.LocalBalance,
                           "david C-D");

        await AssertNetworkHealthyAsync(after, ct);
    }

    /// <summary>
    /// Asserts nothing moved: the same balances everywhere as in <paramref name="before"/>, once nothing is pending.
    /// </summary>
    protected async Task AssertBalancesUnchangedAsync(AbcdSnapshot before, CancellationToken ct)
    {
        await Network.WaitNoPendingHtlcsAsync(ct);
        var after = await Network.SnapshotAsync(ct);
        Console.WriteLine($"Before: {before}");
        Console.WriteLine($"After:  {after}");

        Assert.Equal(0, LocalDeltaMsat(before.BobAliceBob, after.BobAliceBob));
        Assert.Equal(0, LocalDeltaMsat(before.BobBobCarol, after.BobBobCarol));
        Assert.Equal(0, LocalDeltaMsat(before.CarolBobCarol, after.CarolBobCarol));
        Assert.Equal(0, LocalDeltaMsat(before.CarolCarolDavid, after.CarolCarolDavid));
        Assert.Equal(before.AliceAliceBob.LocalBalance, after.AliceAliceBob.LocalBalance);
        Assert.Equal(before.DavidCarolDavid.LocalBalance, after.DavidCarolDavid.LocalBalance);

        await AssertNetworkHealthyAsync(after, ct);
    }

    /// <summary>
    /// Zero pending HTLCs, B–C commitment numbers mirrored on Bob and Carol, every channel still usable on both ends
    /// and every peer still connected.
    /// </summary>
    protected async Task AssertNetworkHealthyAsync(AbcdSnapshot snapshot, CancellationToken ct)
    {
        Assert.Equal(0, snapshot.PendingHtlcCount);

        // Bob's commitment is Carol's remote one and vice versa
        Assert.Equal(snapshot.BobBobCarol.LocalCommitmentNumber, snapshot.CarolBobCarol.RemoteCommitmentNumber);
        Assert.Equal(snapshot.BobBobCarol.RemoteCommitmentNumber, snapshot.CarolBobCarol.LocalCommitmentNumber);

        await Network.WaitUntilUsableAsync(ct, TimeSpan.FromSeconds(10));
        Assert.True(await Network.AllPeersConnectedAsync(ct), "an ABCD peer disconnected");
    }

    protected static long LocalDeltaMsat(ChannelInfoClientResponse before, ChannelInfoClientResponse after) =>
        (long)after.LocalBalance.MilliSatoshi - (long)before.LocalBalance.MilliSatoshi;

    /// <summary>
    /// LND rounds each commitment output down to the sat, so a balance that moved by <paramref name="deltaMsat"/>
    /// moves by <c>floor</c> or <c>ceil</c> of it in sat depending on the msat it held before.
    /// </summary>
    protected static void AssertWithinOneSat(long deltaMsat, long beforeSat, long afterSat, string what)
    {
        var expectedSat = (double)deltaMsat / 1_000;
        var actualSat = afterSat - beforeSat;
        Assert.True(Math.Abs(actualSat - expectedSat) <= 1,
                    $"{what}: LND local balance moved by {actualSat} sat, expected {expectedSat} sat (±1)");
    }
}