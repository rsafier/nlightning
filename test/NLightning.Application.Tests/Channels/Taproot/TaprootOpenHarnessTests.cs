using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Taproot;

using Application.Channels.Safety;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// The goal of taproot wave t02 lane OPS (NL-877 T5): a private simple taproot channel opened between two NLightning
/// nodes with the v1 flow, used for payments both ways and reestablished after a restart of each side with an HTLC in
/// flight, on SQLite (<see cref="TaprootOpenHarness"/>), with LND 0.21's wire shape (channel_type exactly {80},
/// <c>next_local_nonce</c> in open/accept/channel_ready, zero signatures with <c>partial_signature_with_nonce</c>).
/// </summary>
public class TaprootOpenHarnessTests
{
    private static readonly LightningMoney s_fundingAmount = LightningMoney.Satoshis(1_000_000);

    [Fact]
    public async Task Given_TwoNodes_When_ASimpleTaprootChannelIsOpenedV1_Then_ItPaysBothWaysAndSurvivesRestarts()
    {
        // Arrange
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var alice = harness.Alice;
        var bob = harness.Bob;

        // Act 1 - Alice opens: open_channel -> accept_channel -> funding_created -> funding_signed
        var (channelId, funding) = await harness.OpenAsync(s_fundingAmount);

        // Assert 1 - the wire shape LND 0.21 expects
        var open = Assert.Single(bob.Received.OfType<OpenChannel1Message>());
        Assert.Equal([TaprootChannelType.CompulsoryBit], open.ChannelTypeTlv!.Features.GetSetBits());
        Assert.False(open.Payload.ChannelFlags.AnnounceChannel);
        Assert.NotNull(open.NextLocalNonceTlv);
        Assert.NotNull(Assert.Single(alice.Received.OfType<AcceptChannel1Message>()).NextLocalNonceTlv);
        var created = Assert.Single(bob.Received.OfType<FundingCreatedMessage>());
        Assert.True(created.Payload.Signature.IsZero);
        Assert.NotNull(created.PartialSignatureWithNonceTlv);
        var fundingSigned = Assert.Single(alice.Received.OfType<FundingSignedMessage>());
        Assert.True(fundingSigned.Payload.Signature.IsZero);
        Assert.NotNull(fundingSigned.PartialSignatureWithNonceTlv);
        Assert.Equal(ChannelState.V1FundingSigned, alice.Channel(channelId).State);
        Assert.Equal(ChannelState.V1FundingSigned, bob.Channel(channelId).State);
        Assert.True(alice.Channel(channelId).ChannelParams.OptionSimpleTaproot);
        Assert.True(bob.Channel(channelId).ChannelParams.OptionSimpleTaproot);

        // The published funding transaction pays the MuSig2 P2TR output of both funding keys and is signed
        var fundingTx = Transaction.Load(funding.RawTransaction, Network.RegTest);
        var aliceChannel = alice.Channel(channelId);
        var musig2 = alice.Services.GetRequiredService<IMusig2Service>();
        var outputKey = musig2.AggregateTaprootKeyPath(aliceChannel.FundingOutput!.LocalFundingPubKey,
                                                       aliceChannel.FundingOutput.RemoteFundingPubKey);
        var fundingTxOut = fundingTx.Outputs[aliceChannel.FundingOutput.Index!.Value];
        Assert.Equal(outputKey.GetTaprootScriptPubKey(), fundingTxOut.ScriptPubKey.ToBytes());
        Assert.Equal(s_fundingAmount.Satoshi, fundingTxOut.Value.Satoshi);
        Assert.All(fundingTx.Inputs, i => Assert.False(WitScript.IsNullOrEmpty(i.WitScript)));

        // Act 2 - the funding confirms: channel_ready with next_local_nonce both ways
        await harness.ConfirmFundingAsync(channelId, funding.TransactionId);

        // Assert 2
        Assert.Equal(ChannelState.Open, alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, bob.Channel(channelId).State);
        Assert.All(alice.Received.Concat(bob.Received).OfType<ChannelReadyMessage>(),
                   m => Assert.NotNull(m.NextLocalNonceTlv));
        Assert.Single(alice.Channel(channelId).Commitments!.RemoteNextNonces);

        // Act 3 - payments both ways
        await alice.PayAsync(bob, channelId, LightningMoney.Satoshis(200_000));
        await harness.PumpAsync();
        await bob.PayAsync(alice, channelId, LightningMoney.Satoshis(50_000));
        await harness.PumpAsync();

        // Assert 3
        Assert.Single(alice.PaymentHandler.Fulfilled);
        Assert.Single(bob.PaymentHandler.Fulfilled);
        AssertIdle(alice, bob, channelId, 850_000_000);

        // Act 4 - Alice pays, and Bob restarts before her update_add_htlc and commitment_signed reach him; then the
        // same with Bob paying and Alice restarting
        await alice.PayAsync(bob, channelId, LightningMoney.Satoshis(30_000));
        await harness.RestartAsync(bob);
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        await bob.PayAsync(alice, channelId, LightningMoney.Satoshis(10_000));
        await harness.RestartAsync(alice);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 4 - both payments went through after the reestablish, which carried the nonces
        Assert.Equal(2, alice.PaymentHandler.Fulfilled.Count);
        Assert.Equal(2, bob.PaymentHandler.Fulfilled.Count);
        AssertIdle(alice, bob, channelId, 830_000_000);
        Assert.All(alice.Received.Concat(bob.Received).OfType<ChannelReestablishMessage>(),
                   m => Assert.True(m.NextLocalNoncesTlv!.Nonces.Contains(funding.TransactionId)));
        Assert.True(alice.Received.OfType<ChannelReestablishMessage>().Count() >= 2);

        // Every signing nonce was sent once
        var signingNonces = alice.Received.Concat(bob.Received).OfType<CommitmentSignedMessage>()
                                 .Select(m => m.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce)
                                 .ToList();
        Assert.Equal(signingNonces.Count, signingNonces.Distinct().Count());

        // Act 5 / Assert 5 - each side's latest commitment, signed for broadcast, passes script verification
        AssertBroadcastVerifies(alice, channelId, fundingTxOut);
        AssertBroadcastVerifies(bob, channelId, fundingTxOut);
    }

    [Fact]
    public async Task Given_ATaprootOpen_When_OpenChannelIsSerialized_Then_ItCarriesChannelTypeBit80AloneAndTlv4()
    {
        // Arrange
        await using var harness = await TaprootOpenHarness.CreateAsync();
        await harness.OpenAsync(s_fundingAmount);
        var open = Assert.Single(harness.Bob.Received.OfType<OpenChannel1Message>());
        var serializer = harness.Alice.Services.GetRequiredService<IMessageSerializer>();

        // Act
        using var stream = new MemoryStream();
        await serializer.SerializeAsync(open, stream);
        var bytes = stream.ToArray();

        // Assert - channel_type (TLV 1): 11 bytes, big-endian bit 80 only (LND accepts exactly {80}); next_local_nonce
        // (TLV 4): 66 bytes
        byte[] channelType = [0x01, 0x0B, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(bytes.AsSpan().IndexOf(channelType) > 0, Convert.ToHexString(bytes));
        byte[] nonceTlv = [0x04, 0x42, .. (byte[])open.NextLocalNonceTlv!.Nonce];
        Assert.True(bytes.AsSpan().IndexOf(nonceTlv) > 0);
        Assert.Equal(bytes.Length - nonceTlv.Length, bytes.AsSpan().IndexOf(nonceTlv));
    }

    [Fact]
    public async Task Given_AFundedTaprootChannelBeforeChannelReady_When_ForceClosed_Then_CommitmentZeroVerifies()
    {
        // Arrange - right after funding_signed: no commitment state yet, the peer's partial signature of commitment 0
        // is the channel's LastReceivedPartialSignature (persisted)
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var (channelId, funding) = await harness.OpenAsync(s_fundingAmount);
        await harness.RestartAsync(harness.Alice);
        var fundingTx = Transaction.Load(funding.RawTransaction, Network.RegTest);
        var channel = harness.Alice.Channel(channelId);
        Assert.Null(channel.Commitments);
        Assert.NotNull(channel.LastReceivedPartialSignature);

        // Act / Assert - both sides' commitment 0, from what they saved
        AssertBroadcastVerifies(harness.Alice, channelId, fundingTx.Outputs[channel.FundingOutput!.Index!.Value]);
        AssertBroadcastVerifies(harness.Bob, channelId, fundingTx.Outputs[channel.FundingOutput.Index.Value]);
    }

    [Theory]
    [InlineData("open_channel")]
    [InlineData("open_channel bad nonce")]
    [InlineData("accept_channel")]
    [InlineData("funding_created")]
    [InlineData("funding_signed")]
    public async Task Given_AnOpenMessageWithoutItsNonceOrPartialSignature_When_Received_Then_TheOpenFails(
        string stripped)
    {
        // Arrange - the spec: the receiver MUST fail the channel when next_local_nonce or partial_signature_with_nonce
        // is absent (or the nonce does not parse as two points)
        await using var harness = await TaprootOpenHarness.CreateAsync();
        harness.Tamper = (_, message) => (stripped, message) switch
        {
            ("open_channel", OpenChannel1Message open) =>
                new OpenChannel1Message(open.Payload, open.ChannelTypeTlv, open.UpfrontShutdownScriptTlv),
            ("open_channel bad nonce", OpenChannel1Message open) =>
                new OpenChannel1Message(open.Payload, open.ChannelTypeTlv, open.UpfrontShutdownScriptTlv,
                                        new Domain.Protocol.Tlv.NextLocalNonceTlv(
                                            new Domain.Crypto.ValueObjects.MusigPublicNonce(
                                                Enumerable.Repeat((byte)0xFF, 66).ToArray()))),
            ("accept_channel", AcceptChannel1Message accept) =>
                new AcceptChannel1Message(accept.Payload, accept.ChannelTypeTlv, accept.UpfrontShutdownScriptTlv),
            ("funding_created", FundingCreatedMessage created) => new FundingCreatedMessage(created.Payload),
            ("funding_signed", FundingSignedMessage signed) => new FundingSignedMessage(signed.Payload),
            _ => message
        };

        // Act / Assert
        var exception = await Assert.ThrowsAnyAsync<Domain.Exceptions.ChannelErrorException>(
                            () => harness.OpenAsync(s_fundingAmount));
        Assert.Contains(stripped.Split(' ')[0], exception.Message);
        Assert.Empty(harness.Alice.Published);
    }

    [Fact]
    public async Task Given_ChannelReadyWithoutItsNonce_When_Received_Then_TheChannelFails()
    {
        // Arrange
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var (channelId, funding) = await harness.OpenAsync(s_fundingAmount);
        harness.Tamper = (from, message) => from == "Alice" && message is ChannelReadyMessage ready
                                                ? new ChannelReadyMessage(ready.Payload, ready.ShortChannelIdTlv)
                                                : message;

        // Act
        var exception = await Assert.ThrowsAsync<Domain.Exceptions.ChannelFailedException>(
                            () => harness.ConfirmFundingAsync(channelId, funding.TransactionId));

        // Assert
        Assert.Equal("TAPROOT-CR-R01", exception.RequirementId);
        Assert.Equal(ChannelState.Failed, harness.Bob.Channel(channelId).State);
    }

    private static void AssertIdle(TaprootOpenNode alice, TaprootOpenNode bob, ChannelId channelId,
                                   ulong aliceBalanceMsat)
    {
        var a = alice.Channel(channelId).Commitments!;
        var b = bob.Channel(channelId).Commitments!;
        Assert.Empty(a.Htlcs);
        Assert.Empty(b.Htlcs);
        Assert.Null(a.RemoteNextCommit);
        Assert.Null(b.RemoteNextCommit);
        Assert.Equal(aliceBalanceMsat, a.LocalBalanceMsat);
        Assert.Equal(a.LocalBalanceMsat, b.RemoteBalanceMsat);
        Assert.Equal(a.LocalCommit.Number, b.RemoteCommit.Number);
        Assert.Equal(b.LocalCommit.Number, a.RemoteCommit.Number);
        Assert.True(a.HasRemoteNoncesForActiveFundings && b.HasRemoteNoncesForActiveFundings);
    }

    private static void AssertBroadcastVerifies(TaprootOpenNode node, ChannelId channelId, TxOut fundingTxOut)
    {
        var services = node.Services;
        var builder = new LocalCommitmentBroadcastBuilder(
            services.GetRequiredService<ICommitmentTransactionModelFactory>(),
            services.GetRequiredService<ICommitmentTransactionBuilder>(),
            services.GetRequiredService<Domain.Bitcoin.Interfaces.ILightningSigner>());
        var signed = builder.Build(node.Channel(channelId));

        var tx = Transaction.Load(signed.Transaction.RawTxBytes, Network.RegTest);
        var precomputed = tx.PrecomputeTransactionData([fundingTxOut]);
        var input = tx.Inputs.AsIndexedInputs().Single();
        Assert.True(input.VerifyScript(fundingTxOut, ScriptVerify.Standard | ScriptVerify.Taproot, precomputed,
                                       out var error),
                    $"{node.Name}'s commitment {signed.CommitmentNumber} does not verify: {error}");
        Assert.Equal(new TxId(tx.GetHash().ToBytes()), signed.Transaction.TxId);
    }
}