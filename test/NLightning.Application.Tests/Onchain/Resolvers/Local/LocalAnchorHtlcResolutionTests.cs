using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Onchain.Resolvers.Local;
using Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using static LocalCommitResolutionHarness;

/// <summary>
/// BOLT 5 plan O7-T3 / NL-314 (B5-HTX-02) with a fake chain: our option_anchors commitment is on chain and
/// <see cref="Application.Onchain.Resolvers.LocalCommitResolver"/> resolves its HTLC outputs with zero-fee HTLC
/// transactions combined with wallet fee inputs (<see cref="AnchorTestWallet"/>) and a change output; every input of
/// every transaction is executed against the output it spends (the HTLC input with the peer's real
/// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> signature and ours over the combined transaction).
/// </summary>
public sealed class LocalAnchorHtlcResolutionTests
{
    private const ulong OfferedMsat = 20_000_000;
    private const ulong ReceivedMsat = 30_000_000;
    private const uint OfferedCltv = 1_010;
    private const uint ReceivedCltv = 1_020;

    private static readonly Secret s_offeredPreimage = RealSigningCommitmentPair.Preimage(1);
    private static readonly Secret s_receivedPreimage = RealSigningCommitmentPair.Preimage(2);

    [Fact]
    public async Task Given_AnchorsOfferedHtlc_When_CltvExpiryIsReached_Then_HtlcTimeoutWithFeeInputsBroadcastAndSwept()
    {
        // Arrange
        var wallet = new AnchorTestWallet(60_000);
        using var harness = CreateHarness(wallet, pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            pair.Settle(pair.Alice);
        });
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);

        // Act: one block before cltv_expiry, then at it
        await harness.MineToAsync(OfferedCltv - 1);
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        await harness.MineAsync();

        // Assert: the HTLC-timeout pair at index 0 (sequence 1, nLockTime = cltv_expiry), the wallet input after it,
        // change to the wallet; every input valid
        var timeout = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(OfferedCltv, (uint)timeout.LockTime);
        Assert.Equal(2, timeout.Inputs.Count);
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, vout), timeout.Inputs[0].PrevOut);
        Assert.Equal(1U, timeout.Inputs[0].Sequence.Value);
        Assert.Equal(new OutPoint(wallet.FundingTransactions[0], 0), timeout.Inputs[1].PrevOut);
        Assert.Equal(SweepFeePolicy.RbfSequence, timeout.Inputs[1].Sequence.Value);
        Assert.Equal(2, timeout.Outputs.Count);
        Assert.Equal(harness.CommitmentTransaction.Outputs[vout].Value, timeout.Outputs[0].Value);
        Assert.Equal(wallet.ChangeScript, timeout.Outputs[1].ScriptPubKey.ToBytes());
        harness.AssertAllInputsVerify(timeout);

        // The fee comes from the wallet input, at the estimate
        var fee = 60_000 - timeout.Outputs[1].Value.Satoshi;
        var weight = 3L * timeout.GetSerializedSize(TransactionOptions.None) + timeout.GetSerializedSize();
        Assert.True((ulong)fee >= SweepWeights.FeeSat(FeeratePerKw, weight), $"fee {fee} for weight {weight}");
        var broadcast = harness.Broadcasts[new TxId(timeout.GetHash().ToBytes())];
        Assert.Equal(harness.Channel.ChannelId, broadcast.ChannelId);
        Assert.InRange(broadcast.FeeratePerKw, FeeratePerKw, FeeratePerKw * 2);
        Assert.Equal(new TxId(timeout.GetHash().ToBytes()), harness.CommitmentRow(vout).ResolvingTransactionId);
        Assert.Empty(wallet.Released);

        // Act: it confirms; then the CSV of its output passes
        await harness.MineAsync();
        var confirmedAt = harness.Height;
        await harness.MineToAsync(confirmedAt + Csv - 1);

        // Assert: the second-level output (vout 0 of the combined transaction) is ours after the CSV, swept and valid;
        // the upstream HTLC failed as an on-chain timeout once the HTLC-timeout was reasonably deep
        var timeoutTxId = new TxId(timeout.GetHash().ToBytes());
        Assert.Equal(OutputDescriptorKind.DelayedToLocal, harness.Rows[(timeoutTxId, 0)].Descriptor);
        Assert.False(harness.Rows.ContainsKey((timeoutTxId, 1)));
        var sweep = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep),
                                  s => s.Inputs.Any(i => i.PrevOut == new OutPoint(timeout, 0)));
        harness.AssertAllInputsVerify(sweep);
        var failed = Assert.IsType<OutgoingHtlcFailed>(harness.Events.First(e => e.Event is OutgoingHtlcFailed).Event);
        Assert.Equal(RealSigningCommitmentPair.Hash(s_offeredPreimage), failed.PaymentHash);
        Assert.Empty(harness.Alerts);
    }

    [Fact]
    public async Task Given_AnchorsHtlcWeFulfilled_When_OurCommitmentConfirms_Then_HtlcSuccessWithFeeInputsAndPreimage()
    {
        // Arrange
        var wallet = new AnchorTestWallet(400, 500); // two small outputs: both are needed
        using var harness = CreateHarness(wallet, pair =>
        {
            var id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_receivedPreimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        }, feeEstimatePerKw: 253);

        // Act
        await harness.ResolveAsync();

        // Assert: the HTLC-success at once with the preimage, both wallet inputs, every input valid
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        var success = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, vout), success.Inputs[0].PrevOut);
        Assert.Equal((byte[])s_receivedPreimage, success.Inputs[0].WitScript.Pushes.ElementAt(3));
        Assert.Equal(0U, (uint)success.LockTime);
        Assert.Equal(3, success.Inputs.Count);
        harness.AssertAllInputsVerify(success);
        Assert.Equal(ReceivedCltv, harness.CommitmentRow(vout).DeadlineHeight);
        Assert.Equal(OutputResolutionState.Broadcast, harness.CommitmentRow(vout).State);

        // The selection was asked for the HTLC-success's own weight with a change output, at the floored estimate
        var (owner, baseWeight, feerate) = Assert.Single(wallet.Selections);
        Assert.Equal(new AnchorFeeInputOwner(harness.Channel.ChannelId, harness.CommitmentTxId, vout), owner);
        Assert.Equal(253U, feerate);
        Assert.InRange(baseWeight, 700, 900);

        // Act: the next rounds never rebuild it
        var again = await harness.ResolveAsync();

        // Assert
        Assert.DoesNotContain(again, a => a is BroadcastAction);
        Assert.Single(wallet.Selections);
    }

    [Fact]
    public async Task Given_WalletCannotPay_When_Resolved_Then_NothingBroadcastUntilItCan()
    {
        // Arrange: a wallet whose only output is too small for the fee
        var wallet = new AnchorTestWallet(300);
        using var harness = CreateHarness(wallet, pair =>
        {
            var id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_receivedPreimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        });

        // Act
        await harness.ResolveAsync();
        await harness.MineAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);

        // Assert: asked every round, nothing broadcast, the row not marked as broadcast
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(2, wallet.Selections.Count);
        Assert.Null(harness.CommitmentRow(vout).ResolvingTransactionId);
        Assert.NotEqual(OutputResolutionState.Broadcast, harness.CommitmentRow(vout).State);
    }

    [Fact]
    public async Task Given_SignerRefusesTheCombinedTransaction_When_Resolved_Then_InputsReleasedAndNothingBroadcast()
    {
        // Arrange: the production LocalLightningSigner as it stands (the O7-T3 signer seam: it signs an HTLC transaction
        // with exactly one input), so our SIGHASH_ALL signature over the combined transaction fails
        var wallet = new AnchorTestWallet(60_000);
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            var id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_receivedPreimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        }, hasAnchors: true, feeInputProvider: wallet);
        foreach (var funding in wallet.FundingTransactions)
            harness.AddKnownTransaction(funding);

        // Act
        await harness.ResolveAsync();

        // Assert: the reservation is given back and the next block tries again
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Single(wallet.Released);
        Assert.Empty(wallet.Reserved);
        Assert.Equal(0, wallet.SignCount);
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        Assert.Null(harness.CommitmentRow(vout).ResolvingTransactionId);
    }

    [Fact]
    public async Task Given_NoFeeInputProviderRegistered_When_Resolved_Then_DefaultSelectsNothingAndRetries()
    {
        // Arrange: the default registration of AddLocalCommitResolutionServices
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            var id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_receivedPreimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        }, hasAnchors: true);

        // Act
        await harness.ResolveAsync();

        // Assert
        Assert.IsType<UnavailableAnchorFeeInputProvider>(harness.GetService<IAnchorFeeInputProvider>());
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.DoesNotContain(harness.Rows.Values, r => r.State == OutputResolutionState.Broadcast
                                                      && r.Descriptor == OutputDescriptorKind.LocalReceivedHtlc);
    }

    [Fact]
    public async Task Given_UnavailableProvider_When_Used_Then_NothingSelectedAndReleaseIsHarmless()
    {
        // Arrange
        var provider = new UnavailableAnchorFeeInputProvider();
        var owner = new AnchorFeeInputOwner(RealSigningCommitmentPair.ChannelId, new TxId(new byte[32]), 1);

        // Act
        var selection = await provider.SelectAsync(owner, 700, 253, TestContext.Current.CancellationToken);
        await provider.ReleaseAsync(owner, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(selection);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SignAsync(new SignedTransaction(new byte[32], [1]), [],
                                     TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_WalletRegisteredFirst_When_AddingLocalResolution_Then_TheWalletIsKept()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IAnchorFeeInputProvider>(new AnchorTestWallet());

        // Act
        services.AddLocalCommitResolutionServices();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.IsType<AnchorTestWallet>(provider.GetRequiredService<IAnchorFeeInputProvider>());
    }

    private static LocalCommitResolutionHarness CreateHarness(AnchorTestWallet wallet,
                                                              Action<RealSigningCommitmentPair> setup,
                                                              uint feeEstimatePerKw = FeeratePerKw)
    {
        LocalCommitResolutionHarness? harness = null;
        harness = new LocalCommitResolutionHarness(setup, hasAnchors: true, feeInputProvider: wallet,
                                                   wrapSigner: signer => CombinedHtlcSigningProxy.Create(
                                                       signer, () => (harness!.Pair.Alice,
                                                                      harness.GetService<IKeyDerivationService>())))
        {
            FeeEstimatePerKw = feeEstimatePerKw
        };
        foreach (var funding in wallet.FundingTransactions)
            harness.AddKnownTransaction(funding);
        return harness;
    }
}