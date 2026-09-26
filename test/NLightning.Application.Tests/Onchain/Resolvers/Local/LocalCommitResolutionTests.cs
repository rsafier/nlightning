using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Local;
using Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.ValueObjects;
using static LocalCommitResolutionHarness;

/// <summary>
/// BOLT 5 plan O3-T3/T4 (rows B5-LCL-*) with a fake chain: our real commitment is on chain and
/// <see cref="Application.Onchain.Resolvers.LocalCommitResolver"/> resolves it block by block; every transaction it
/// builds is checked by script execution against the output it spends.
/// </summary>
public sealed class LocalCommitResolutionTests
{
    private const ulong OfferedMsat = 20_000_000;
    private const ulong ReceivedMsat = 30_000_000;
    private const uint OfferedCltv = 1_010;
    private const uint ReceivedCltv = 1_020;

    private static readonly Secret s_offeredPreimage = RealSigningCommitmentPair.Preimage(1);
    private static readonly Secret s_receivedPreimage = RealSigningCommitmentPair.Preimage(2);

    [Fact]
    public async Task Given_OurCommitmentOnChain_When_BeforeTheCsv_Then_ToLocalIsSweptOnlyAtTheCsv()
    {
        // Arrange
        using var harness = new LocalCommitResolutionHarness();

        // Act: right after the classification, then up to one block before the CSV allows the sweep
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        var waiting = harness.CommitmentRow(toLocal);
        await harness.MineToAsync(CloseHeight + Csv - 2);

        // Assert: nothing broadcast; the row waits for the height from which the sweep can be mined
        Assert.Equal(OutputResolutionState.Waiting, waiting.State);
        Assert.Equal(CloseHeight + Csv - 1, waiting.WaitUntilHeight);
        Assert.Empty(harness.Broadcasts);

        // Act: the tip from which a sweep with nSequence = csv enters the next block
        await harness.MineAsync();

        // Assert: one sweep of to_local with nSequence = to_self_delay, into the wallet, valid against the output
        var sweep = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep));
        var input = Assert.Single(sweep.Inputs);
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, toLocal), input.PrevOut);
        Assert.Equal((uint)Csv, input.Sequence.Value);
        Assert.Equal(harness.Destination, Assert.Single(sweep.Outputs).ScriptPubKey.ToBytes());
        harness.AssertAllInputsVerify(sweep);
        var row = harness.CommitmentRow(toLocal);
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        Assert.Equal(new TxId(sweep.GetHash().ToBytes()), row.ResolvingTransactionId);

        // Act: the next rounds, before and after the sweep confirms
        var again = await harness.ResolveAsync();
        await harness.MineAsync();
        await harness.MineAsync();

        // Assert: never built twice; resolved by our own sweep, no alert
        Assert.DoesNotContain(again, a => a is BroadcastAction);
        Assert.Single(harness.Broadcasts);
        Assert.Equal(OutputResolutionState.Resolved, harness.CommitmentRow(toLocal).State);
        Assert.Empty(harness.Alerts);
    }

    [Fact]
    public async Task Given_OurOfferedHtlc_When_CltvExpiryIsReached_Then_HtlcTimeoutIsBroadcastThenNotBefore()
    {
        // Arrange
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);

        // Act: one block before cltv_expiry
        await harness.MineToAsync(OfferedCltv - 1);

        // Assert
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(OutputResolutionState.Waiting, harness.CommitmentRow(vout).State);

        // Act: the tip reaches cltv_expiry
        await harness.MineAsync();

        // Assert: our pre-signed HTLC-timeout (both signatures), nLockTime = cltv_expiry, spending the HTLC output
        var timeout = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(OfferedCltv, (uint)timeout.LockTime);
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, vout), Assert.Single(timeout.Inputs).PrevOut);
        Assert.Equal(5, timeout.Inputs[0].WitScript.PushCount);
        Assert.Empty(timeout.Inputs[0].WitScript.Pushes.ElementAt(3));
        harness.AssertAllInputsVerify(timeout);
        Assert.Equal(new TxId(timeout.GetHash().ToBytes()), harness.CommitmentRow(vout).ResolvingTransactionId);
        Assert.Equal(OutputResolutionState.Broadcast, harness.CommitmentRow(vout).State);
    }

    [Fact]
    public async Task Given_OurHtlcTimeoutConfirmed_When_ReasonablyDeep_Then_UpstreamFailedAtDepthAndSecondLevelSweptAfterCsv()
    {
        // Arrange: the HTLC-timeout is broadcast at cltv_expiry and confirms in the next block
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        await harness.MineToAsync(OfferedCltv);
        var timeout = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        var timeoutTxId = new TxId(timeout.GetHash().ToBytes());
        await harness.MineAsync();
        var confirmedAt = harness.Height;

        // Assert: the second-level output is a row of its own, watched, and nothing went upstream yet
        var secondLevel = harness.Rows[(timeoutTxId, 0)];
        Assert.Equal(OutputDescriptorKind.DelayedToLocal, secondLevel.Descriptor);
        Assert.Equal(HtlcDirection.Outgoing, secondLevel.HtlcDirection);
        Assert.True(harness.Watches.ContainsKey((timeoutTxId, 0)));
        Assert.DoesNotContain(harness.Events, e => e.Event is OutgoingHtlcFailed);

        // Act: one block short of the reasonable depth (6)
        await harness.MineToAsync(confirmedAt + 4);

        // Assert
        Assert.DoesNotContain(harness.Events, e => e.Event is OutgoingHtlcFailed);

        // Act: at depth 6
        await harness.MineAsync();

        // Assert: failed upstream as an on-chain timeout, from that block on
        var (failedAt, failedEvent) = harness.Events.First(e => e.Event is OutgoingHtlcFailed);
        var failed = Assert.IsType<OutgoingHtlcFailed>(failedEvent);
        Assert.Equal(confirmedAt + 5, failedAt);
        Assert.Equal(HtlcRemovalKind.OnchainTimeout, failed.Removal.Kind);
        Assert.Equal(RealSigningCommitmentPair.Hash(s_offeredPreimage), failed.PaymentHash);

        // Act: up to one block before the CSV of the HTLC-timeout's output allows its sweep
        await harness.MineToAsync(confirmedAt + Csv - 2);

        // Assert (to_local, CSV-locked since the commitment, is swept on its own schedule)
        Assert.Empty(SweepsOf(harness, timeout));

        // Act
        await harness.MineAsync();

        // Assert: the second-level output is swept with nSequence = to_self_delay, valid against it
        var sweep = Assert.Single(SweepsOf(harness, timeout));
        Assert.Equal(new OutPoint(timeout, 0), Assert.Single(sweep.Inputs).PrevOut);
        Assert.Equal((uint)Csv, sweep.Inputs[0].Sequence.Value);
        harness.AssertAllInputsVerify(sweep);
        Assert.Equal(new TxId(sweep.GetHash().ToBytes()), harness.Rows[(timeoutTxId, 0)].ResolvingTransactionId);
        Assert.Empty(harness.Alerts);
    }

    [Fact]
    public async Task Given_PeersHtlcWithoutAnAllowedPreimage_When_UntilItExpires_Then_NeverClaimedAndIgnoredAfterExpiry()
    {
        // Arrange: Bob's HTLC to us, never fulfilled by us (an invoice preimage for its hash is not ours to use unless
        // the switch accepted this HTLC: B5-LCL-RO-02)
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);

        // Act
        await harness.MineToAsync(ReceivedCltv - 1);
        var beforeExpiry = harness.CommitmentRow(vout);
        await harness.MineAsync();

        // Assert: waited for a preimage until it expired, then nothing is left to do for it
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(OutputResolutionState.Waiting, beforeExpiry.State);
        Assert.Equal(OutputResolutionState.Ignored, harness.CommitmentRow(vout).State);
    }

    [Fact]
    public async Task Given_PeersHtlcWeFulfilled_When_OurCommitmentConfirms_Then_HtlcSuccessIsBroadcastWithThePreimage()
    {
        // Arrange: we accepted Bob's HTLC and sent our fulfill, which the channel's close cut short
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            var id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_receivedPreimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        });

        // Act
        await harness.ResolveAsync();

        // Assert: the HTLC-success at once (0 <remotesig> <localsig> <preimage> <script>), valid against the output
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        var success = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, vout), Assert.Single(success.Inputs).PrevOut);
        Assert.Equal((byte[])s_receivedPreimage, success.Inputs[0].WitScript.Pushes.ElementAt(3));
        Assert.Equal(0u, (uint)success.LockTime);
        harness.AssertAllInputsVerify(success);
        Assert.Equal(ReceivedCltv, harness.CommitmentRow(vout).DeadlineHeight);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_PeersHtlcForwardedAndFulfilledDownstream_When_OurCommitmentConfirms_Then_ClaimedOnlyWithItsOwnPreimage(
        bool sameHash)
    {
        // Arrange: Bob's HTLC was forwarded (here: to our offered HTLC 0 of the same channel) and the downstream peer
        // revealed a preimage after our channel went on chain, so our upstream fulfill was refused
        var downstreamPreimage = sameHash ? s_receivedPreimage : s_offeredPreimage;
        ulong incomingId = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            incomingId = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Add(pair.Alice, OfferedMsat, downstreamPreimage, OfferedCltv);
            pair.Settle(pair.Bob);
            pair.Fulfill(pair.Bob, 0, downstreamPreimage);
        });
        harness.Forwards[HtlcOrigin.Forwarded(harness.Channel.ChannelId, incomingId)] =
            [(harness.Channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, 0))];
        Assert.NotNull(harness.Channel.Commitments!.GetHtlc(HtlcDirection.Outgoing, 0)!.KnownPreimage);

        // Act
        await harness.ResolveAsync();

        // Assert: claimed with the HTLC-success only when the learnt preimage is the one of this HTLC's hash
        var claims = harness.Broadcast(BroadcastPurpose.HtlcTransaction)
                            .Where(t => t.Inputs[0].PrevOut.N
                                     == harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc))
                            .ToList();
        if (!sameHash)
        {
            Assert.Empty(claims);
            return;
        }

        var success = Assert.Single(claims);
        Assert.Equal((byte[])s_receivedPreimage, success.Inputs[0].WitScript.Pushes.ElementAt(3));
        harness.AssertAllInputsVerify(success);
    }

    [Fact]
    public async Task Given_PeerClaimsOurHtlcWithThePreimage_When_Spent_Then_PreimagePersistedAndUpstreamFulfilledAtOnce()
    {
        // Arrange
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);
        var claim = PeerPreimageClaim(harness, vout, s_offeredPreimage);

        // Act: Bob's direct preimage spend confirms (before our cltv_expiry)
        await harness.MineAsync(claim);

        // Assert: the preimage is staged into the HTLC's record before the fulfill is raised, in the same block
        var stage = harness.Log.FindIndex(l => l.StartsWith("stage preimage"));
        var raise = harness.Log.FindIndex(l => l == $"raise {nameof(OutgoingHtlcFulfilled)}");
        Assert.True(stage >= 0 && raise > stage, string.Join(", ", harness.Log));
        var persisted = Assert.Single(harness.Applied).UpsertedHtlcs.Single();
        Assert.Equal(s_offeredPreimage, persisted.KnownPreimage);
        var (height, fulfilledEvent) = harness.Events.First(e => e.Event is OutgoingHtlcFulfilled);
        Assert.Equal(CloseHeight + 1, height);
        Assert.Equal(s_offeredPreimage, Assert.IsType<OutgoingHtlcFulfilled>(fulfilledEvent).PaymentPreimage);
        Assert.DoesNotContain(harness.Events, e => e.Event is OutgoingHtlcFailed);
        Assert.Empty(harness.Alerts);

        // Act: the chain passes cltv_expiry
        await harness.MineToAsync(OfferedCltv + 1);

        // Assert: no HTLC-timeout for a spent output, and the preimage is not staged again
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Single(harness.Applied);
    }

    [Fact]
    public async Task Given_PeersPreimageClaimNotStagedAtSpendTime_When_ReasonablyDeep_Then_FulfilledFromTheChainAndNeverFailed()
    {
        // Arrange: Bob claims our offered HTLC with the preimage, but the spend-time round is lost (its save failed),
        // so the preimage never reached the HTLC's record
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);
        harness.NotifySpends = false;

        // Act: the claim confirms, and the chain goes well past the reasonable depth
        await harness.MineAsync(PeerPreimageClaim(harness, vout, s_offeredPreimage));
        await harness.MineToAsync(CloseHeight + 20);

        // Assert: the per-block round read the preimage from the spender: staged, fulfilled, never failed
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(harness.Events.First(e => e.Event is OutgoingHtlcFulfilled)
                                                                    .Event);
        Assert.Equal(s_offeredPreimage, fulfilled.PaymentPreimage);
        Assert.Equal(s_offeredPreimage, Assert.Single(harness.Applied).UpsertedHtlcs.Single().KnownPreimage);
        Assert.DoesNotContain(harness.Events, e => e.Event is OutgoingHtlcFailed);
    }

    [Fact]
    public async Task Given_OfferedHtlcTakenByAnUnreadableSpender_When_ReasonablyDeep_Then_AlertedAndNeverFailedUpstream()
    {
        // Arrange: as above, and the chain service cannot return the spender (so its witness cannot be read)
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);
        harness.NotifySpends = false;
        harness.ChainServiceFindsTransactions = false;
        harness.ChainServiceFindsBlocks = false;

        // Act
        await harness.MineAsync(PeerPreimageClaim(harness, vout, s_offeredPreimage));
        var spentAt = harness.Height;
        await harness.MineToAsync(spentAt + 20);

        // Assert: the peer may have been paid, so the upstream HTLC is never failed; the operator is alerted once, at
        // the depth where the fail would have been raised
        Assert.DoesNotContain(harness.Events, e => e.Event is OutgoingHtlcFailed or OutgoingHtlcFulfilled);
        Assert.Equal("B5-LCL-LO-03", Assert.Single(harness.Alerts).RequirementId);
    }

    [Fact]
    public async Task Given_PeersPreimageClaimNotStagedAndNoTxIndex_When_ReasonablyDeep_Then_FulfilledFromTheSpendBlock()
    {
        // Arrange: NL-315: the spend-time round is lost and bitcoind has no txindex (getrawtransaction finds nothing),
        // but the block at the recorded spend height is readable
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);
        harness.NotifySpends = false;
        harness.ChainServiceFindsTransactions = false;

        // Act
        await harness.MineAsync(PeerPreimageClaim(harness, vout, s_offeredPreimage));
        await harness.MineToAsync(harness.Height + 20);

        // Assert: the preimage is read from the spend's block: staged, fulfilled upstream, never failed, no alert
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(harness.Events.First(e => e.Event is OutgoingHtlcFulfilled)
                                                                    .Event);
        Assert.Equal(s_offeredPreimage, fulfilled.PaymentPreimage);
        Assert.Equal(s_offeredPreimage, Assert.Single(harness.Applied).UpsertedHtlcs.Single().KnownPreimage);
        Assert.DoesNotContain(harness.Events, e => e.Event is OutgoingHtlcFailed);
        Assert.Empty(harness.Alerts);
    }

    [Fact]
    public async Task Given_UnreadableSpenderAndNodeOfflineAtTheReasonableDepth_When_NextRound_Then_AlertedOnce()
    {
        // Arrange: NL-315: the spender cannot be read, and the node misses every round until well past the reasonable
        // depth of the spend
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);
        harness.NotifySpends = false;
        harness.ChainServiceFindsTransactions = false;
        harness.ChainServiceFindsBlocks = false;
        harness.ResolveEachBlock = false;
        await harness.MineAsync(PeerPreimageClaim(harness, vout, s_offeredPreimage));
        await harness.MineToAsync(harness.Height + 15);

        // Act: back online, several rounds
        harness.ResolveEachBlock = true;
        await harness.ResolveAsync();
        await harness.MineToAsync(harness.Height + 5);

        // Assert: alerted once at the first round past the depth, never failed nor fulfilled
        Assert.DoesNotContain(harness.Events, e => e.Event is OutgoingHtlcFailed or OutgoingHtlcFulfilled);
        Assert.Equal("B5-LCL-LO-03", Assert.Single(harness.Alerts).RequirementId);
    }

    [Fact]
    public async Task Given_SmallSecondLevelOutputAndAFeeSpike_When_Swept_Then_SweptLeavingDustInsteadOfIgnored()
    {
        // Arrange: an HTLC just above our trim threshold (546 sat + the 1,657 sat HTLC-timeout fee at 2,500 sat/kw),
        // so its HTLC-timeout pays about 570 sat; at a spiked estimate the fee is capped at half of that, which would
        // leave less than the 294 sat P2WPKH dust
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, 2_230_000, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        harness.FeeEstimatePerKw = 100_000;
        await harness.ResolveAsync();
        await harness.MineToAsync(OfferedCltv);
        var timeout = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        var value = (ulong)timeout.Outputs[0].Value.Satoshi;
        Assert.True(value - value / 2 < 294, $"{value} sat");
        await harness.MineAsync();
        var confirmedAt = harness.Height;

        // Act: the second-level CSV allows the sweep
        await harness.MineToAsync(confirmedAt + Csv - 1);

        // Assert: swept (not abandoned), paying everything above the dust output, valid against the output
        var sweep = Assert.Single(SweepsOf(harness, timeout));
        Assert.Equal(294, Assert.Single(sweep.Outputs).Value.Satoshi);
        harness.AssertAllInputsVerify(sweep);
        var row = harness.Rows[(new TxId(timeout.GetHash().ToBytes()), 0)];
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        Assert.Equal(new TxId(sweep.GetHash().ToBytes()), row.ResolvingTransactionId);
    }

    [Fact]
    public async Task Given_TrimmedOfferedHtlc_When_OurCommitmentConfirms_Then_UpstreamFailedAtOnce()
    {
        // Arrange: 1,000 sat is below the dust limit plus the HTLC-timeout fee on both commitments
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            pair.Add(pair.Alice, 1_000_000, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });

        // Act
        await harness.ResolveAsync();

        // Assert: no output anywhere, so failed upstream without waiting for depth (B5-LCL-LO-04)
        Assert.DoesNotContain(harness.Rows.Values, r => r.Descriptor == OutputDescriptorKind.LocalOfferedHtlc);
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(harness.Events).Event);
        Assert.Equal(HtlcRemovalKind.OnchainTimeout, failed.Removal.Kind);
    }

    [Fact]
    public void Given_TheRegistration_When_AddedTwice_Then_OneResolverBehindBothTypes()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddLocalCommitResolutionServices();
        services.AddLocalCommitResolutionServices();

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(IOutputResolver));
        Assert.Single(services, d => d.ServiceType == typeof(LocalCommitResolver));
        Assert.Single(services, d => d.ServiceType == typeof(ISweepDestinationProvider));
    }

    [Fact]
    public async Task Given_AnotherKindOfClose_When_Resolved_Then_NothingIsDone()
    {
        // Arrange
        using var harness = new LocalCommitResolutionHarness();
        var remote = harness.Close with { Kind = ChannelCloseKind.RemoteCommitment };

        // Act
        var actions = await harness.Resolver.ResolveAsync(remote, [], CloseHeight,
                                                          TestContext.Current.CancellationToken);

        // Assert
        Assert.False(harness.Resolver.CanResolve(ChannelCloseKind.RemoteCommitment));
        Assert.True(harness.Resolver.CanResolve(ChannelCloseKind.LocalCommitment));
        Assert.Empty(actions);
    }

    [Fact]
    public async Task Given_ToLocalTakenByAnotherTransaction_When_Spent_Then_Alerted()
    {
        // Arrange
        using var harness = new LocalCommitResolutionHarness();
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        var theft = Transaction.Create(Network.Main);
        theft.Inputs.Add(new OutPoint(harness.CommitmentTransaction, toLocal));
        theft.Outputs.Add(Money.Satoshis(1_000), new Script(harness.Destination));

        // Act
        await harness.MineAsync(theft);

        // Assert
        Assert.Equal("B5-LCL-01", Assert.Single(harness.Alerts).RequirementId);
        Assert.Empty(harness.Broadcasts);
    }

    private static IReadOnlyList<Transaction> SweepsOf(LocalCommitResolutionHarness harness, Transaction parent) =>
        harness.Broadcast(BroadcastPurpose.Sweep).Where(t => t.Inputs[0].PrevOut.Hash == parent.GetHash()).ToList();

    /// <summary>Bob's direct claim of our offered HTLC output with the preimage: <c>&lt;sig&gt; &lt;preimage&gt;
    /// &lt;script&gt;</c> (not signed: the fake chain does not validate it).</summary>
    private static Transaction PeerPreimageClaim(LocalCommitResolutionHarness harness, uint vout, Secret preimage)
    {
        var claim = Transaction.Create(Network.Main);
        claim.Inputs.Add(new OutPoint(harness.CommitmentTransaction, vout));
        claim.Outputs.Add(Money.Satoshis(15_000), new Script(harness.Destination));
        var signature = new byte[72];
        signature[0] = 0x30;
        claim.Inputs[0].WitScript = new WitScript([signature, (byte[])preimage, [0x76, 0xa9]]);
        return claim;
    }
}