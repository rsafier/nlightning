using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Splicing;
using Application.Gossip;
using Application.Gossip.Announcements;
using Application.Gossip.Relay.Interfaces;
using Channels.Harness;
using Channels.Splicing;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Gossip;

/// <summary>
/// NL-1131 (taproot gossip, BOLTs PR #1059 "splice_locked Extensions"): a public simple taproot channel is spliced on
/// <see cref="SpliceHarness"/> (real engine, real <c>LocalLightningSigner</c>s with rotated funding keys, production
/// announcement services) and announced again under the splice: both <c>splice_locked</c>s carry fresh announcement
/// nonces, <c>announcement_signatures_2</c> name the splice's txid and short channel id once it is 6 deep, the new
/// <c>channel_announcement_2</c> verifies against the splice's P2TR output, the old short channel id keeps resolving
/// (retired map), and a restart between <c>splice_locked</c> and the exchange recovers through
/// <c>channel_reestablish</c> TLV 7.
/// </summary>
public class SpliceAnnouncement2HarnessTests
{
    private const long SpliceInSatoshis = 100_000;
    private const uint SpliceHeight = TwoNodeHarness.BlockHeight + 3;
    private const uint SpliceDepth5 = SpliceHeight + 4;
    private const uint SpliceDepth6 = SpliceHeight + 5;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AnAnnouncedTaprootChannel_When_SplicedAndSixDeep_Then_ReannouncedUnderTheSplice(
        bool aliceSplicesIn)
    {
        // Arrange: announced on its original funding
        using var harness = await CreateAnnouncedAsync();
        var (original, _) = Assert.Single(Sink(harness.Alice).ChannelAnnouncements2);
        Assert.Equal(TwoNodeHarness.ShortChannelId, original.ShortChannelId);
        var initialNonces = NoncesOf(harness);

        // Act 1: Alice splices in, or Bob (the channel's fundee) splices out; it confirms and locks both ways
        var spliceTxId = await SpliceAndLockAsync(harness, aliceSplicesIn ? harness.Alice : harness.Bob,
                                                  aliceSplicesIn ? SpliceInSatoshis : -50_000);

        // Assert 1: both splice_locked carry both nonces (BOLTs #1059), fresh ones; locked with the new scid, the old
        // one resolving for 72 blocks; nothing re-announced before the splice is 6 deep; no channel_ready nonces
        var spliceLocked = harness.Transcript.Select(t => t.Message).OfType<SpliceLockedMessage>().ToList();
        Assert.Equal(2, spliceLocked.Count);
        Assert.All(spliceLocked, m =>
        {
            Assert.Equal(spliceTxId, m.Payload.SpliceTxId);
            Assert.NotNull(m.AnnouncementNodeNonceTlv);
            Assert.NotNull(m.AnnouncementBitcoinNonceTlv);
        });
        Assert.All(spliceLocked, m =>
        {
            Assert.DoesNotContain(m.AnnouncementNodeNonceTlv!.Nonce, initialNonces);
            Assert.DoesNotContain(m.AnnouncementBitcoinNonceTlv!.Nonce, initialNonces);
        });
        var spliceScid = new ShortChannelId(SpliceHeight, 1, harness.Alice.Node.Channel.FundingOutput!.Index!.Value);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(spliceScid, node.Node.Channel.ShortChannelId);
            Assert.True(node.Node.Services.GetRequiredService<IRetiredScidMap>()
                            .TryResolve(TwoNodeHarness.ShortChannelId, out _));
            Assert.False(node.Node.Services.GetRequiredService<AnnouncedChannels2>()
                             .IsAnnounced(TwoNodeHarness.ChannelId));
        }

        await RaiseBlockAsync(harness, SpliceDepth5);
        Assert.Equal(0, CountSignatures2(harness, spliceScid));

        // Act 2: the splice's sixth confirmation
        await RaiseBlockAsync(harness, SpliceDepth6);

        // Assert 2: one announcement_signatures_2 each way for the splice; the same new announcement at both ends,
        // valid against the splice's P2TR output (the rotated funding keys), and each side's channel_update_2 for it
        AssertReannounced(harness, spliceTxId, spliceScid, original);
        var messages = harness.Transcript.Select(t => t.Message).ToList();
        Assert.DoesNotContain(messages.Skip(messages.FindIndex(m => m is SpliceLockedMessage)),
                              m => m is ChannelReadyMessage { AnnouncementNodeNonceTlv: not null });
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task Given_TheSpliceAlreadySixDeepAtTheLock_When_Locked_Then_SignaturesFollowTheSpliceLocked()
    {
        // Arrange: both tips are past the splice's 6th block before the depth watcher reports it
        using var harness = await CreateAnnouncedAsync();
        harness.Alice.Fund(SpliceInSatoshis + 200_000);
        var result = await harness.SpliceAsync(harness.Alice, SpliceInSatoshis);
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        harness.Alice.Node.SetTip(SpliceDepth6);
        harness.Bob.Node.SetTip(SpliceDepth6);

        // Act
        await harness.ConfirmAsync(result.SpliceTxId!.Value, SpliceHeight, harness.Alice, harness.Bob);

        // Assert: announced at the lock; each side's announcement_signatures_2 goes after its own splice_locked
        var spliceScid = harness.Alice.Node.Channel.ShortChannelId;
        AssertReannounced(harness, result.SpliceTxId!.Value, spliceScid, null);
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var sequence = harness.Transcript.Where(t => t.From == name).Select(t => t.Message).ToList();
            Assert.True(sequence.FindIndex(m => m is SpliceLockedMessage)
                      < sequence.FindIndex(m => m is AnnouncementSignatures2Message s
                                             && s.Payload.ShortChannelId == spliceScid), name);
        }
    }

    [Fact]
    public async Task Given_AliceRestartsAfterHerSpliceLocked_When_Reconnected_Then_TheSpliceIsAnnounced()
    {
        // Arrange: only Alice reached the splice's depth: her splice_locked (with her nonces) went to Bob, then she
        // restarts (her nonces' secret halves are gone; nothing of the session is stored)
        using var harness = await CreateAnnouncedAsync();
        harness.Alice.Fund(SpliceInSatoshis + 200_000);
        var result = await harness.SpliceAsync(harness.Alice, SpliceInSatoshis);
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        var spliceTxId = result.SpliceTxId!.Value;
        await harness.ConfirmAsync(spliceTxId, SpliceHeight, harness.Alice);
        Assert.Single(harness.Transcript, t => t is { From: "Alice", Message: SpliceLockedMessage });
        await RestartAsync(harness, harness.Alice, SpliceDepth6);

        // Act: the reconnection (BOLTs #1059: TLV 7 with fresh nonces for the funding my_current_funding_locked
        // names, bit 1), then Bob's depth, his splice_locked, the lock, the 6th confirmation
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();
        var aliceReestablish = harness.Bob.Node.Received.OfType<ChannelReestablishMessage>().Last();
        Assert.Equal(spliceTxId, aliceReestablish.MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.NotEqual(0, aliceReestablish.MyCurrentFundingLockedTlv.RetransmitFlags & 0x02);
        Assert.NotNull(aliceReestablish.AnnouncementNoncesTlv);
        harness.Bob.Node.SetTip(SpliceDepth6);
        await harness.ConfirmAsync(spliceTxId, SpliceHeight, harness.Bob);
        await RaiseBlockAsync(harness, SpliceDepth6);

        // Assert
        AssertReannounced(harness, spliceTxId, harness.Alice.Node.Channel.ShortChannelId, null);
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task Given_BobRestartsBetweenTheLockAndTheSixthBlock_When_Reconnected_Then_TheSpliceIsAnnounced()
    {
        // Arrange: locked both ways, not 6 deep; Bob restarts (the splice's session is lost with him)
        using var harness = await CreateAnnouncedAsync();
        var spliceTxId = await SpliceAndLockAsync(harness, harness.Alice, SpliceInSatoshis);
        await RestartAsync(harness, harness.Bob, SpliceDepth5);

        // Act: the reconnection carries fresh nonces for the current (spliced) funding both ways; then the 6th block
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();
        var bobReestablish = harness.Alice.Node.Received.OfType<ChannelReestablishMessage>().Last();
        Assert.Equal(spliceTxId, bobReestablish.MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.NotEqual(0, bobReestablish.MyCurrentFundingLockedTlv.RetransmitFlags & 0x02);
        Assert.NotNull(bobReestablish.AnnouncementNoncesTlv);
        await RaiseBlockAsync(harness, SpliceDepth6);

        // Assert: one announcement_signatures_2 each way (the new session's), the splice announced
        AssertReannounced(harness, spliceTxId, harness.Alice.Node.Channel.ShortChannelId, null);
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public async Task Given_AForgedPartialSignatureForTheSplice_When_Received_Then_WarningAndNothingPublished()
    {
        // Arrange: Bob's announcement_signatures_2 for the splice carries zero partial signatures
        using var harness = await CreateAnnouncedAsync();
        harness.Bob.Node.Rewrite = m => m is AnnouncementSignatures2Message signatures
                                            ? new AnnouncementSignatures2Message(AnnouncementSignatures2Payload.Create(
                                                signatures.Payload.ChannelId, signatures.Payload.ShortChannelId,
                                                new MusigPartialSignature(new byte[32]),
                                                new MusigPartialSignature(new byte[32]),
                                                signatures.Payload.FundingTxId))
                                            : m;
        var spliceTxId = await SpliceAndLockAsync(harness, harness.Alice, SpliceInSatoshis);

        // Act
        await RaiseBlockAsync(harness, SpliceDepth6);

        // Assert: Alice warned (and dropped the connection); she published nothing for the splice
        var spliceScid = harness.Alice.Node.Channel.ShortChannelId;
        Assert.Contains(harness.Failures, f => f is { Node: "Alice", Exception: ChannelWarningException });
        Assert.DoesNotContain(Sink(harness.Alice).ChannelAnnouncements2, a => a.Announcement.ShortChannelId
                                                                           == spliceScid);
        Assert.Equal(spliceTxId, harness.Alice.Node.Channel.FundingOutput!.TransactionId);
    }

    [Fact]
    public async Task Given_ABumpedTaprootSplice_When_TheBumpLocks_Then_TheBumpIsAnnounced()
    {
        // Arrange: Alice splices in and bumps it; the bump confirms
        using var harness = await CreateAnnouncedAsync(o => o.MinRbfInterval = TimeSpan.Zero);
        harness.Alice.Fund(SpliceInSatoshis + 400_000);
        var first = await harness.SpliceAsync(harness.Alice, SpliceInSatoshis);
        Assert.True(first.State == SpliceNegotiationState.Signed, first.FailureReason);
        var bump = harness.Alice.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId,
                                                                         SpliceHarness.FeeratePerKw * 2),
                                                   TestContext.Current.CancellationToken);
        await harness.PumpAsync(bump);
        var bumped = await bump;
        Assert.True(bumped.State == SpliceNegotiationState.Signed, $"{bumped.State}: {bumped.FailureReason}");
        var rbfTxId = bumped.SpliceTxId!.Value;

        // Act
        await harness.ConfirmAsync(rbfTxId, SpliceHeight, harness.Alice, harness.Bob);
        await RaiseBlockAsync(harness, SpliceDepth6);

        // Assert: the sibling that locked is the one announced
        AssertReannounced(harness, rbfTxId, harness.Alice.Node.Channel.ShortChannelId, null);
        Assert.Empty(harness.Failures);
    }

    private static void AssertReannounced(SpliceHarness harness, TxId spliceTxId, ShortChannelId spliceScid,
                                          ChannelAnnouncement2Payload? original)
    {
        var signatures = harness.Transcript.Select(t => t.Message).OfType<AnnouncementSignatures2Message>()
                                .Where(m => m.Payload.ShortChannelId == spliceScid).ToList();
        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, s => Assert.Equal(spliceTxId, s.Payload.FundingTxId));

        var (atAlice, capacity) = Sink(harness.Alice).ChannelAnnouncements2[^1];
        var (atBob, _) = Sink(harness.Bob).ChannelAnnouncements2[^1];
        Assert.Equal(spliceScid, atAlice.ShortChannelId);
        Assert.Equal(atAlice.GetSignedData(), atBob.GetSignedData());
        Assert.Equal(spliceTxId, atAlice.FundingTxId);
        var alice = harness.Alice.Node.Channel;
        Assert.Equal(spliceTxId, alice.FundingOutput!.TransactionId);
        Assert.Equal(alice.FundingOutput.Amount.Satoshi, capacity.Satoshi);
        Assert.NotEqual(harness.Alice.Node.Basepoints.FundingPubKey, alice.LocalFundingPubKey);
        if (original is not null)
            Assert.NotEqual(original.GetSignedData(), atAlice.GetSignedData());

        var musig2 = harness.Alice.Node.Services.GetRequiredService<IMusig2Service>();
        var verifier = harness.Alice.Node.Services.GetRequiredService<IGossipV2SignatureVerifier>();
        var script = musig2.AggregateTaprootKeyPath(alice.LocalFundingPubKey, alice.RemoteFundingPubKey!.Value)
                           .GetTaprootScriptPubKey();
        Assert.Equal(GossipV2ProofResult.Valid, verifier.CheckChannelProof(atAlice, script));
        Assert.Equal(GossipV2ProofResult.Valid, verifier.CheckChannelProof(atBob, script));

        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.True(node.Node.Services.GetRequiredService<AnnouncedChannels2>()
                            .IsAnnounced(TwoNodeHarness.ChannelId));
            Assert.Contains(Sink(node).ChannelUpdates2, u => u.ShortChannelId == spliceScid);
        }
    }

    private static async Task<TxId> SpliceAndLockAsync(SpliceHarness harness, SpliceNode initiator,
                                                       long contributionSatoshis)
    {
        if (contributionSatoshis > 0)
            initiator.Fund(contributionSatoshis + 200_000);
        var result = await harness.SpliceAsync(initiator, contributionSatoshis);
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        var spliceTxId = result.SpliceTxId!.Value;
        await harness.ConfirmAsync(spliceTxId, SpliceHeight, harness.Alice, harness.Bob);
        foreach (var node in new[] { harness.Alice, harness.Bob })
            Assert.Equal(spliceTxId, node.Node.Channel.FundingOutput!.TransactionId);
        return spliceTxId;
    }

    private static async Task RestartAsync(SpliceHarness harness, SpliceNode node, uint tip)
    {
        await harness.RestartAsync(node);
        node.Node.NegotiatedFeatures = NegotiatedFeatures();
        node.Node.SetTip(tip);
    }

    private static async Task<SpliceHarness> CreateAnnouncedAsync(Action<SpliceOptions>? configureSplice = null)
    {
        var harness = new SpliceHarness(realEngine: true, simpleTaproot: true, announceChannel: true,
                                        configureSplice: (_, o) => configureSplice?.Invoke(o),
                                        configureServices: (node, services) =>
                                        {
                                            var options = new NodeOptions
                                            {
                                                EnableHtlcs = true,
                                                BitcoinNetwork = BitcoinNetwork.Regtest,
                                                Alias = node.Name,
                                                Features = NegotiatedFeatures()
                                            };
                                            services.AddSingleton(Options.Create(options));
                                            services.AddGossipServices();
                                            services.TryAddSingleton<IGossipV2SignatureVerifier,
                                                GossipV2SignatureVerifier>();
                                            services.AddSingleton<IOwnGossipSink>(new RecordingOwnGossipSink());
                                            services.AddSingleton<IGossipRelayScheduler>(
                                                new RecordingRelayScheduler());
                                            services
                                               .AddScoped<IChannelMessageHandler<AnnouncementSignaturesMessage>,
                                                    AnnouncementSignaturesMessageHandler>();
                                            services
                                               .AddScoped<IChannelMessageHandler<AnnouncementSignatures2Message>,
                                                    AnnouncementSignatures2MessageHandler>();
                                            services.AddScoped<IChannelMessageHandler<ChannelReadyMessage>,
                                                ChannelReadyMessageHandler>();
                                        });
        harness.Alice.Node.NegotiatedFeatures = NegotiatedFeatures();
        harness.Bob.Node.NegotiatedFeatures = NegotiatedFeatures();

        await RaiseBlockAsync(harness, TwoNodeHarness.BlockHeight);
        Assert.Single(Sink(harness.Alice).ChannelAnnouncements2);
        Assert.Single(Sink(harness.Bob).ChannelAnnouncements2);
        return harness;
    }

    private static FeatureOptions NegotiatedFeatures() => new()
    {
        AllowExperimentalFeatures = true,
        OptionQuiesce = FeatureSupport.Optional,
        OptionSplice = FeatureSupport.Optional,
        OptionSimpleTaproot = FeatureSupport.Optional,
        OptionSimpleClose = FeatureSupport.Optional,
        OptionGossipV2 = FeatureSupport.Optional
    };

    private static async Task RaiseBlockAsync(SpliceHarness harness, uint height)
    {
        await harness.Alice.Node.RaiseBlockAsync(height);
        await harness.Bob.Node.RaiseBlockAsync(height);
        await harness.PumpAsync();
    }

    /// <summary>Every announcement nonce sent so far (channel_ready, splice_locked, channel_reestablish).</summary>
    private static HashSet<MusigPublicNonce> NoncesOf(SpliceHarness harness)
    {
        var nonces = new HashSet<MusigPublicNonce>();
        foreach (var message in harness.Transcript.Select(t => t.Message))
        {
            switch (message)
            {
                case ChannelReadyMessage { AnnouncementNodeNonceTlv: { } node, AnnouncementBitcoinNonceTlv: { } bitcoin }:
                    nonces.Add(node.Nonce);
                    nonces.Add(bitcoin.Nonce);
                    break;
                case SpliceLockedMessage { AnnouncementNodeNonceTlv: { } node, AnnouncementBitcoinNonceTlv: { } bitcoin }:
                    nonces.Add(node.Nonce);
                    nonces.Add(bitcoin.Nonce);
                    break;
                case ChannelReestablishMessage { AnnouncementNoncesTlv: { } tlv }:
                    nonces.Add(tlv.NodeNonce);
                    nonces.Add(tlv.BitcoinNonce);
                    break;
            }
        }

        return nonces;
    }

    private static int CountSignatures2(SpliceHarness harness, ShortChannelId shortChannelId) =>
        harness.Transcript.Count(t => t.Message is AnnouncementSignatures2Message m
                                   && m.Payload.ShortChannelId == shortChannelId);

    private static RecordingOwnGossipSink Sink(SpliceNode node) =>
        (RecordingOwnGossipSink)node.Node.Services.GetRequiredService<IOwnGossipSink>();
}