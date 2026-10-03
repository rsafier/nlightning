using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using static TrampolineVectorKit;

/// <summary>
/// BOLTs PR 836 <c>trampoline-onion-error-test.json</c>, byte for byte: [0] Eve's failure returned through Dave,
/// Carol and Bob to Alice; [1] a trampoline payment over Eve's blinded path whose introduction node Carol turns Dave's
/// malformed failure into <c>temporary_trampoline_failure</c>.
/// </summary>
public class TrampolineErrorVectorTests
{
    private static readonly FailureCode s_temporaryTrampolineFailure = FailureCode.TemporaryTrampolineFailure;

    private static readonly JsonElement s_vectors = Load("trampoline-onion-error-test.json");
    private static readonly JsonElement s_plain = s_vectors[0];
    private static readonly JsonElement s_blinded = s_vectors[1];

    #region [0] Eve's failure through Carol

    [Fact]
    public void Given_AlicesOnions_When_Building_Then_EveryHopSharedSecretMatches()
    {
        // Arrange
        var alice = s_plain.GetProperty("generate_alice");
        var paymentHash = Hex(alice, "associated_data");
        var trampolineHops = alice.GetProperty("trampoline_hops");
        var outerHops = alice.GetProperty("hops");

        // Act
        var trampoline = TrampolineOnions.Build(
            trampolineHops.EnumerateArray().Select(h => Hop(h, "node_id", "payload", "hex")).ToList(),
            PrivKey(alice, "trampoline_session_key"), paymentHash, TrampolineOnionSizePolicy.Exact);
        var outer = Sphinx.ConstructWithSharedSecrets(
            outerHops.EnumerateArray().Select(h => Hop(h, "node_id", "payload", "hex")).ToList(),
            PrivKey(alice, "session_key"), paymentHash);

        // Assert
        Assert.Equal(Hex(alice, "trampoline_onion"), trampoline.ToTlvValue());
        Assert.Equal(Hex(alice, "onion"), outer.Packet.ToBytes());
        Assert.Equal(SecretsOf(trampolineHops), trampoline.SharedSecrets);
        Assert.Equal(SecretsOf(outerHops), outer.SharedSecrets);
    }

    [Fact]
    public void Given_CarolsRouteToEve_When_Building_Then_EveryHopSharedSecretMatches()
    {
        // Arrange
        var carol = s_plain.GetProperty("generate_carol");
        var hops = carol.GetProperty("hops");

        // Act
        var onion = Sphinx.ConstructWithSharedSecrets(
            hops.EnumerateArray().Select(h => Hop(h, "node_id", "payload", "hex")).ToList(),
            PrivKey(carol, "session_key"), Hex(carol, "associated_data"));

        // Assert
        Assert.Equal(Hex(carol, "onion"), onion.Packet.ToBytes());
        Assert.Equal(SecretsOf(hops), onion.SharedSecrets);
    }

    [Fact]
    public void Given_EvesOnion_When_PeelingBothLayers_Then_SharedSecretsMatchTheFailureSection()
    {
        // Arrange
        var eve = s_plain.GetProperty("failure_eve");
        var nodeKey = PrivKey(eve, "node_privkey");
        var paymentHash = Hex(s_plain.GetProperty("generate_alice"), "associated_data");

        // Act
        var outer = Sphinx.Peel(new OnionPacket(Hex(eve, "onion")), paymentHash, nodeKey);
        var trampoline = TrampolineOnions.PeelWithNodeKey(FindTlv(outer.Payload.Span, TrampolineOnionPacketType),
                                                          paymentHash, nodeKey);

        // Assert
        Assert.Equal(Secret(eve, "onion_shared_secret"), outer.SharedSecret);
        Assert.Equal(Secret(eve, "trampoline_onion_shared_secret"), trampoline.SharedSecret);
        Assert.True(trampoline.IsFinal);
    }

    [Fact]
    public void Given_EvesFailure_When_Created_Then_PacketMatches()
    {
        // Arrange
        var eve = s_plain.GetProperty("failure_eve");
        var message = FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(100_000_000),
                                                                      800_000);

        // Act
        var packet = TrampolineFailures.CreateTrampolineErrorPacket(Secret(eve, "trampoline_onion_shared_secret"),
                                                                    Secret(eve, "onion_shared_secret"), message);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(eve, "failure_packet")), Convert.ToHexStringLower(packet));
    }

    [Fact]
    public void Given_EvesPacket_When_DaveWraps_Then_PacketMatches()
    {
        // Arrange
        var dave = s_plain.GetProperty("failure_dave");

        // Act
        var packet = FailureOnions.WrapErrorPacket(Secret(dave, "onion_shared_secret"),
                                                   Hex(s_plain.GetProperty("failure_eve"), "failure_packet"));

        // Assert
        Assert.Equal(Hex(dave, "failure_packet"), packet);
    }

    [Fact]
    public void Given_DavesPacket_When_CarolUnwrapsAndRewraps_Then_BothStagesMatch()
    {
        // Arrange
        var carol = s_plain.GetProperty("failure_carol");

        // Act
        var downstream = TrampolineFailures.UnwrapDownstreamErrorPacket(
            Secrets(carol.GetProperty("downstream_shared_secrets")),
            Hex(s_plain.GetProperty("failure_dave"), "failure_packet"));
        var packet = TrampolineFailures.WrapTrampolineErrorPacket(
            Secret(carol, "upstream_trampoline_onion_shared_secret"), Secret(carol, "upstream_onion_shared_secret"),
            downstream.UnwrappedPacket.Span);

        // Assert: no hop of Carol's route authenticated it, so it is Eve's and Carol re-wraps it
        Assert.True(downstream.MustRewrap);
        Assert.Null(downstream.Failure);
        Assert.Equal(Hex(carol, "downstream_unwrapped_failure"), downstream.UnwrappedPacket.ToArray());
        Assert.Equal(Hex(carol, "failure_packet"), packet);
    }

    [Fact]
    public void Given_CarolsPacket_When_BobWraps_Then_PacketMatches()
    {
        // Arrange
        var bob = s_plain.GetProperty("failure_bob");

        // Act
        var packet = FailureOnions.WrapErrorPacket(Secret(bob, "onion_shared_secret"),
                                                   Hex(s_plain.GetProperty("failure_carol"), "failure_packet"));

        // Assert
        Assert.Equal(Hex(bob, "failure_packet"), packet);
    }

    [Fact]
    public void Given_BobsPacket_When_AliceDecrypts_Then_EveIsTheErringTrampolineHop()
    {
        // Arrange
        var alice = s_plain.GetProperty("failure_alice");

        // Act
        var decrypted = TrampolineFailures.DecryptTrampolineErrorPacket(
            Secrets(alice.GetProperty("onion_shared_secrets")),
            Secrets(alice.GetProperty("trampoline_onion_shared_secrets")),
            Hex(s_plain.GetProperty("failure_bob"), "failure_packet"));

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        Assert.Equal(100_000_000UL, decrypted.Message!.HtlcAmount!.MilliSatoshi);
        Assert.Equal(800_000U, decrypted.Message.Height);
    }

    [Fact]
    public void Given_BobsPacket_When_AliceRemovesEachRoute_Then_IntermediateStagesMatch()
    {
        // Arrange
        var alice = s_plain.GetProperty("failure_alice");
        var outerSecrets = Secrets(alice.GetProperty("onion_shared_secrets"));
        var trampolineSecrets = Secrets(alice.GetProperty("trampoline_onion_shared_secrets"));

        // Act: the outer route authenticates nothing; the trampoline route's last hop does
        var afterOuter = TrampolineFailures.UnwrapDownstreamErrorPacket(
            outerSecrets, Hex(s_plain.GetProperty("failure_bob"), "failure_packet"));
        var afterTrampoline = afterOuter.UnwrappedPacket.ToArray();
        foreach (var secret in trampolineSecrets)
            afterTrampoline = FailureOnions.WrapErrorPacket(secret, afterTrampoline);

        var trampolineStage = TrampolineFailures.UnwrapDownstreamErrorPacket(trampolineSecrets,
                                                                             afterOuter.UnwrappedPacket.Span);

        // Assert
        Assert.True(afterOuter.MustRewrap);
        Assert.Equal(Hex(alice, "unwrapped_onion_failure"), afterOuter.UnwrappedPacket.ToArray());
        Assert.Equal(Hex(alice, "unwrapped_trampoline_onion_failure"), afterTrampoline);
        Assert.Equal(1, trampolineStage.Failure!.ErringHopIndex);
    }

    #endregion

    #region [1] Blinded trampoline path, Carol introduction node

    [Fact]
    public void Given_EvesBlindedPath_When_CarolAndDaveUnblind_Then_DataAndPathKeysMatch()
    {
        // Arrange
        var path = s_blinded.GetProperty("generate_blinded_path_eve");
        var hops = path.GetProperty("hops");
        var carolKey = PrivKey(s_blinded.GetProperty("decrypt_carol"), "node_privkey");
        var daveKey = PrivKey(s_blinded.GetProperty("decrypt_dave"), "node_privkey");
        var daveOuterPathKey = PubKey(s_blinded.GetProperty("decrypt_dave").GetProperty("tlvs"), "current_path_key");

        // Act
        var atCarol = RouteBlinding.Unblind(carolKey, PubKey(path, "path_key"),
                                            Hex(hops[0], "encrypted_data"));
        var atDave = RouteBlinding.Unblind(daveKey, atCarol.NextPathKey, Hex(hops[1], "encrypted_data"));

        // Assert
        Assert.Equal(Hex(hops[0], "encoded_tlvs"), atCarol.DecryptedData.ToArray());
        Assert.Equal(daveOuterPathKey, atCarol.NextPathKey);
        Assert.Equal(Hex(hops[1], "encoded_tlvs"), atDave.DecryptedData.ToArray());
        Assert.Equal(PubKey(hops[2], "node_id"), atDave.RecipientData.NextNodeId);
    }

    [Fact]
    public void Given_AlicesBlindedTrampolineHops_When_Building_Then_OnionsAndSecretsMatch()
    {
        // Arrange
        var alice = s_blinded.GetProperty("generate_alice");
        var paymentHash = Hex(alice, "associated_data");
        var trampolineHops = alice.GetProperty("trampoline_hops");
        var outerHops = alice.GetProperty("hops");

        // Act
        var trampoline = TrampolineOnions.Build(
            trampolineHops.EnumerateArray().Select(h => Hop(h, "node_id", "payload", "hex")).ToList(),
            PrivKey(alice, "trampoline_session_key"), paymentHash, TrampolineOnionSizePolicy.Exact);
        var outer = Sphinx.ConstructWithSharedSecrets(
            outerHops.EnumerateArray().Select(h => Hop(h, "node_id", "payload", "hex")).ToList(),
            PrivKey(alice, "session_key"), paymentHash);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(alice, "trampoline_onion")),
                     Convert.ToHexStringLower(trampoline.ToTlvValue()));
        Assert.Equal(409, trampoline.Packet.Length);
        Assert.Equal(Hex(alice, "onion"), outer.Packet.ToBytes());
        Assert.Equal(SecretsOf(trampolineHops), trampoline.SharedSecrets);
        Assert.Equal(SecretsOf(outerHops), outer.SharedSecrets);
        Assert.Equal(trampoline.ToTlvValue(),
                     FindTlv(Hop(outerHops[1], "node_id", "payload", "hex").Payload.Span, TrampolineOnionPacketType));
    }

    [Fact]
    public void Given_TheBlindedRoute_When_EachNodePeels_Then_OnionsAndPayloadsMatch()
    {
        // Arrange
        var alice = s_blinded.GetProperty("generate_alice");
        var paymentHash = Hex(alice, "associated_data");
        var bob = s_blinded.GetProperty("decrypt_bob");
        var carol = s_blinded.GetProperty("decrypt_carol");
        var generateCarol = s_blinded.GetProperty("generate_carol");
        var dave = s_blinded.GetProperty("decrypt_dave");
        var carolKey = PrivKey(carol, "node_privkey");
        var daveKey = PrivKey(dave, "node_privkey");
        var trampolineHops = alice.GetProperty("trampoline_hops");

        // Act: Bob, Carol (introduction node: no path key for the trampoline layer), Carol's onion to Dave, Dave
        // (blinded trampoline hop: the outer payload's current_path_key)
        var atBob = Sphinx.Peel(new OnionPacket(Hex(bob, "onion")), paymentHash, PrivKey(bob, "node_privkey"));
        var atCarol = Sphinx.Peel(new OnionPacket(Hex(carol, "onion")), paymentHash, carolKey);
        var carolTrampoline = TrampolineOnions.PeelWithNodeKey(
            FindTlv(atCarol.Payload.Span, TrampolineOnionPacketType), paymentHash, carolKey);
        var daveHop = Hop(generateCarol.GetProperty("hops")[0], "node_id", "payload", "hex");
        var carolOnion = Sphinx.Construct([daveHop], PrivKey(generateCarol, "session_key"),
                                          Hex(generateCarol, "associated_data"));
        var atDave = Sphinx.Peel(new OnionPacket(Hex(dave, "onion")), paymentHash, daveKey);
        var davePathKey = new CompactPubKey(FindTlv(atDave.Payload.Span, CurrentPathKeyType));
        var daveTrampoline = TrampolineOnions.PeelWithNodeKey(FindTlv(atDave.Payload.Span, TrampolineOnionPacketType),
                                                              paymentHash, daveKey, davePathKey);
        var daveData = RouteBlinding.Unblind(
            daveKey, davePathKey, FindTlv(daveTrampoline.Payload.Span, 10));

        // Assert
        Assert.Equal(Hex(bob, "next_onion"), atBob.NextPacket!.Value.ToBytes());
        Assert.True(atCarol.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(trampolineHops[0].GetProperty("payload"), "hex")),
                     carolTrampoline.Payload.ToArray());
        Assert.Equal(Convert.ToHexStringLower(Hex(carol, "next_trampoline_onion")),
                     Convert.ToHexStringLower(carolTrampoline.NextPacket!.Value.ToBytes()));
        Assert.Equal(Hex(generateCarol, "onion"), carolOnion.ToBytes());
        Assert.Equal(Hex(carol, "next_trampoline_onion"), FindTlv(daveHop.Payload.Span, TrampolineOnionPacketType));
        Assert.True(atDave.IsFinal);
        Assert.Equal(daveHop.Payload.ToArray(), atDave.Payload.ToArray());
        Assert.False(daveTrampoline.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(trampolineHops[1].GetProperty("payload"), "hex")),
                     daveTrampoline.Payload.ToArray());
        Assert.Equal(Secret(trampolineHops[1], "shared_secret"), daveTrampoline.SharedSecret);
        Assert.Equal(Hex(s_blinded.GetProperty("generate_blinded_path_eve").GetProperty("hops")[1], "encoded_tlvs"),
                     daveData.DecryptedData.ToArray());
    }

    [Fact]
    public void Given_DavesBlindedTrampolinePeelFails_When_Reported_Then_ItIsTheVectorsMalformedFailure()
    {
        // Arrange: any failure of Dave's blinded peel (here the HMAC, with another payment hash) is reported as
        // invalid_onion_blinding over the trampoline packet, which is what Dave sends in update_fail_malformed_htlc
        var dave = s_blinded.GetProperty("decrypt_dave");
        var malformed = s_blinded.GetProperty("failure_dave").GetProperty("update_fail_malformed_htlc");
        var daveKey = PrivKey(dave, "node_privkey");
        var outer = Sphinx.Peel(new OnionPacket(Hex(dave, "onion")),
                                Hex(s_blinded.GetProperty("generate_alice"), "associated_data"), daveKey);
        var davePathKey = new CompactPubKey(FindTlv(outer.Payload.Span, CurrentPathKeyType));

        // Act
        var exception = Assert.Throws<OnionException>(() => TrampolineOnions.PeelWithNodeKey(
                                                          FindTlv(outer.Payload.Span, TrampolineOnionPacketType),
                                                          new byte[32], daveKey, davePathKey));

        // Assert
        Assert.Equal(malformed.GetProperty("failure_code").GetUInt16(), (ushort)exception.FailureCode);
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
        Assert.Equal(Hex(malformed, "sha256_of_onion"), exception.FailureData!.Value.ToArray());
    }

    [Fact]
    public void Given_DavesMalformedFailure_When_CarolReplacesIt_Then_PacketMatches()
    {
        // Arrange
        var carol = s_blinded.GetProperty("failure_carol");

        // Act
        var packet = TrampolineFailures.CreateTrampolineErrorPacket(
            Secret(carol, "upstream_trampoline_onion_shared_secret"), Secret(carol, "upstream_onion_shared_secret"),
            new FailureMessage(s_temporaryTrampolineFailure, ReadOnlyMemory<byte>.Empty));

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(carol, "failure_packet")), Convert.ToHexStringLower(packet));
    }

    [Fact]
    public void Given_CarolsPacket_When_BobWrapsAndAliceDecrypts_Then_CarolIsTheErringTrampolineHop()
    {
        // Arrange
        var bob = s_blinded.GetProperty("failure_bob");
        var alice = s_blinded.GetProperty("failure_alice");
        var outerSecrets = Secrets(alice.GetProperty("onion_shared_secrets"));

        // Act
        var packet = FailureOnions.WrapErrorPacket(Secret(bob, "onion_shared_secret"),
                                                   Hex(s_blinded.GetProperty("failure_carol"), "failure_packet"));
        var afterOuter = TrampolineFailures.UnwrapDownstreamErrorPacket(outerSecrets, packet);
        var decrypted = TrampolineFailures.DecryptTrampolineErrorPacket(
            outerSecrets, Secrets(alice.GetProperty("trampoline_onion_shared_secrets")), packet);

        // Assert
        Assert.Equal(Hex(bob, "failure_packet"), packet);
        Assert.Equal(Hex(alice, "unwrapped_onion_failure"), afterOuter.UnwrappedPacket.ToArray());
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(s_temporaryTrampolineFailure, decrypted.Code);
    }

    #endregion

    private static List<Secret> SecretsOf(JsonElement hops) =>
        hops.EnumerateArray().Select(h => Secret(h, "shared_secret")).ToList();
}