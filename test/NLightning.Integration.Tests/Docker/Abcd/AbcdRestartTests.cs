using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// ABCD roadmap §3 variant (b): Bob restarts while Alice's HTLC to David is locked in on all three hops (David holds
/// it with a hold invoice). Bob must come back, reestablish with Alice and Carol, and, through his persisted forward
/// circuit, fulfill Alice once Carol's fulfill arrives. No block is mined while the HTLC is in flight.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class AbcdRestartTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    : AbcdTestBase(fixture, output)
{
    /// <summary>
    /// b2 (main): David settles while Bob is down. Carol learns and persists the preimage and settles C–D; her
    /// upstream fulfill waits for Bob, and she retransmits it after the reestablish.
    /// </summary>
    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_HtlcInFlight_When_BobRestartsAndDavidSettlesWhileBobIsDown_Then_AliceSucceeds(bool crash)
    {
        await RunRestartVariantAsync(crash, settleWhileBobIsDown: true, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// b1: David settles only after Bob is back and reestablished.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_HtlcInFlight_When_BobRestartsAndDavidSettlesAfterBobIsBack_Then_AliceSucceeds()
    {
        await RunRestartVariantAsync(crash: false, settleWhileBobIsDown: false,
                                     TestContext.Current.CancellationToken);
    }

    private async Task RunRestartVariantAsync(bool crash, bool settleWhileBobIsDown, CancellationToken ct)
    {
        // Arrange: a hold invoice at David, and Alice's payment left in flight until David holds the HTLC
        var n = Network;
        const long amountMsat = 40_000_321;
        var fees = AbcdPathFees.ToDavid(amountMsat);
        var before = await n.SnapshotAsync(ct);
        var tip = await GetTipAsync(ct);
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var invoice = await LndTestHelpers.AddHoldInvoiceAsync(n.David, paymentHash, amountMsat,
                                                               [n.HintThroughBobAndCarol()], ct,
                                                               $"abcd restart crash={crash} settleWhileDown={settleWhileBobIsDown}");
        var paymentTask = n.PayFromAliceAsync(invoice.PaymentRequest, ct, HeldPaymentTimeout);
        var settled = false;
        try
        {
            // Accepted: the HTLC is locked in on A-B, B-C and C-D
            await LndTestHelpers.WaitForInvoiceStateAsync(n.David, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          LndTestHelpers.DefaultPaymentTimeout, ct);
            Assert.False(paymentTask.IsCompleted, "Alice's payment completed before David settled");

            // Act: Bob goes down with the HTLC in flight on both of his channels
            await n.StopNodeAsync(n.Bob, crash);
            await Poll.UntilAsync(async () => !n.Carol.IsConnectedTo(n.Bob.NodeId)
                                           && !await LndTestHelpers.IsConnectedToAsync(n.Alice, n.Bob.NodeIdHex, ct),
                                  PeerDropTimeout, "alice and carol dropped bob", ct);

            if (settleWhileBobIsDown)
            {
                await LndTestHelpers.SettleInvoiceAsync(n.David, preimage, ct);
                settled = true;

                // Carol has the preimage (C-D settled with David) and still owes Bob the fulfill on B-C
                await AbcdNetwork.WaitForAsync(async () =>
                {
                    var carolDavid = await n.Carol.GetChannelAsync(n.CarolDavid.ChannelId, ct);
                    var bobCarol = await n.Carol.GetChannelAsync(n.BobCarol.ChannelId, ct);
                    return (carolDavid.OfferedHtlcCount == 0 && bobCarol.ReceivedHtlcCount == 1,
                            $"carol C-D {carolDavid.Describe()}; carol B-C {bobCarol.Describe()}");
                }, AbcdNetwork.SettleTimeout, "carol settled C-D while B-C still holds bob's HTLC", ct);
                Assert.False(paymentTask.IsCompleted, "Alice's payment completed while Bob was down");
            }

            await n.StartNodeAsync(n.Bob, ct);
            await n.WaitUntilUsableAsync(ct);

            if (!settleWhileBobIsDown)
            {
                await LndTestHelpers.SettleInvoiceAsync(n.David, preimage, ct);
                settled = true;
            }

            var payment = await paymentTask.WaitAsync(LndTestHelpers.DefaultPaymentTimeout, ct);

            // Assert
            Assert.Equal(Convert.ToHexString(preimage), payment.PaymentPreimage, StringComparer.OrdinalIgnoreCase);
            await AssertPaidThroughBobAndCarolAsync(payment, paymentHash, fees, before, ct);
            Assert.Equal(tip, await GetTipAsync(ct));
        }
        finally
        {
            if (!settled)
                await CancelQuietlyAsync(paymentHash);
        }
    }

    /// <summary>
    /// Releases a hold invoice a failed run left accepted, so the next test does not find its HTLC pending.
    /// </summary>
    private async Task CancelQuietlyAsync(byte[] paymentHash)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LndTestHelpers.CancelInvoiceAsync(Network.David, paymentHash, timeoutCts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[abcd] could not cancel the hold invoice: {e.Message}");
        }
    }
}