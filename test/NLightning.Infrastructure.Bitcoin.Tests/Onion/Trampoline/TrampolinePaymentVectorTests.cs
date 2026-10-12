using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.Trampoline;

using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using static TrampolineVectorKit;

/// <summary>
/// BOLTs PR 836 <c>trampoline-payment-onion-test.json</c>, byte for byte: Alice pays Eve through the trampoline
/// Carol, reaching Carol over Bob; Carol reaches Eve over Dave.
/// </summary>
public class TrampolinePaymentVectorTests
{
    private static readonly JsonElement s_vector = Load("trampoline-payment-onion-test.json");

    private static readonly JsonElement s_generateAlice = s_vector.GetProperty("generate_alice");
    private static readonly byte[] s_paymentHash = Hex(s_generateAlice, "associated_data");

    [Fact]
    public void Given_AlicesTrampolineHops_When_Building_Then_TrampolineOnionMatches()
    {
        // Arrange
        var hops = s_generateAlice.GetProperty("trampoline_hops").EnumerateArray()
                                  .Select(h => Hop(h, "node_id", "payload", "hex")).ToList();

        // Act
        var onion = TrampolineOnions.Build(hops, PrivKey(s_generateAlice, "trampoline_session_key"), s_paymentHash,
                                           TrampolineOnionSizePolicy.Exact);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(s_generateAlice, "trampoline_onion")),
                     Convert.ToHexStringLower(onion.ToTlvValue()));
        Assert.Equal(161, onion.HopPayloadsLength);
    }

    [Fact]
    public void Given_AlicesOuterHops_When_Building_Then_OnionMatchesAndCarriesTheTrampolineOnion()
    {
        // Arrange
        var hops = s_generateAlice.GetProperty("hops").EnumerateArray()
                                  .Select(h => Hop(h, "node_id", "payload", "hex")).ToList();

        // Act
        var onion = Sphinx.Construct(hops, PrivKey(s_generateAlice, "session_key"), s_paymentHash);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(s_generateAlice, "onion")), Convert.ToHexStringLower(onion));
        Assert.Equal(Hex(s_generateAlice, "trampoline_onion"),
                     FindTlv(hops[1].Payload.Span, TrampolineOnionPacketType));
    }

    [Fact]
    public void Given_BobsOnion_When_Peeling_Then_NextOnionMatches()
    {
        // Arrange
        var bob = s_vector.GetProperty("decrypt_bob");

        // Act
        var peeled = Sphinx.Peel(new OnionPacket(Hex(bob, "onion")), s_paymentHash, PrivKey(bob, "node_privkey"));

        // Assert
        Assert.False(peeled.IsFinal);
        Assert.Equal(Hex(bob, "next_onion"), peeled.NextPacket!.Value.ToBytes());
    }

    [Fact]
    public void Given_CarolsOnion_When_PeelingBothLayers_Then_PayloadsAndNextTrampolineOnionMatch()
    {
        // Arrange
        var carol = s_vector.GetProperty("decrypt_carol");
        var nodeKey = PrivKey(carol, "node_privkey");
        var carolOuterHop = s_generateAlice.GetProperty("hops")[1];
        var carolTrampolineHop = s_generateAlice.GetProperty("trampoline_hops")[0];

        // Act
        var outer = Sphinx.Peel(new OnionPacket(Hex(carol, "onion")), s_paymentHash, nodeKey);
        var trampolinePacket = FindTlv(outer.Payload.Span, TrampolineOnionPacketType);
        var trampoline = TrampolineOnions.PeelWithNodeKey(trampolinePacket, s_paymentHash, nodeKey);

        // Assert
        Assert.True(outer.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(carolOuterHop.GetProperty("payload"), "hex")), outer.Payload.ToArray());
        Assert.False(trampoline.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(carolTrampolineHop.GetProperty("payload"), "hex")),
                     trampoline.Payload.ToArray());
        Assert.Equal(Convert.ToHexStringLower(Hex(carol, "next_trampoline_onion")),
                     Convert.ToHexStringLower(trampoline.NextPacket!.Value.ToBytes()));
    }

    [Fact]
    public void Given_CarolsRouteToEve_When_Building_Then_OnionMatchesAndCarriesTheNextTrampolineOnion()
    {
        // Arrange
        var generateCarol = s_vector.GetProperty("generate_carol");
        var hops = generateCarol.GetProperty("hops").EnumerateArray()
                                .Select(h => Hop(h, "node_id", "payload", "hex")).ToList();

        // Act
        var onion = Sphinx.Construct(hops, PrivKey(generateCarol, "session_key"),
                                     Hex(generateCarol, "associated_data"));

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(generateCarol, "onion")), Convert.ToHexStringLower(onion));
        Assert.Equal(Hex(s_vector.GetProperty("decrypt_carol"), "next_trampoline_onion"),
                     FindTlv(hops[1].Payload.Span, TrampolineOnionPacketType));
    }

    [Fact]
    public void Given_DavesOnion_When_Peeling_Then_NextOnionMatches()
    {
        // Arrange
        var dave = s_vector.GetProperty("decrypt_dave");

        // Act
        var peeled = Sphinx.Peel(new OnionPacket(Hex(dave, "onion")), s_paymentHash, PrivKey(dave, "node_privkey"));

        // Assert
        Assert.Equal(Hex(dave, "next_onion"), peeled.NextPacket!.Value.ToBytes());
    }

    [Fact]
    public void Given_EvesOnion_When_PeelingBothLayers_Then_SheIsTheFinalTrampolineHop()
    {
        // Arrange
        var eve = s_vector.GetProperty("decrypt_eve");
        var nodeKey = PrivKey(eve, "node_privkey");

        // Act
        var outer = Sphinx.Peel(new OnionPacket(Hex(eve, "onion")), s_paymentHash, nodeKey);
        var trampoline = TrampolineOnions.PeelWithNodeKey(FindTlv(outer.Payload.Span, TrampolineOnionPacketType),
                                                          s_paymentHash, nodeKey);

        // Assert
        Assert.True(outer.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(eve, "decrypted_payload")), outer.Payload.ToArray());
        Assert.True(trampoline.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(eve, "decrypted_trampoline_payload")), trampoline.Payload.ToArray());
    }

    [Fact]
    public void Given_TheWholeRoute_When_ForwardedHopByHop_Then_EveryOnionMatches()
    {
        // Arrange: every node peels what the previous one produced, Carol building her own route in between
        var alice = s_generateAlice;
        var trampolineHops = alice.GetProperty("trampoline_hops").EnumerateArray()
                                  .Select(h => Hop(h, "node_id", "payload", "hex")).ToList();
        var trampolineOnion = TrampolineOnions.Build(trampolineHops, PrivKey(alice, "trampoline_session_key"),
                                                     s_paymentHash, TrampolineOnionSizePolicy.Exact);
        var aliceHops = alice.GetProperty("hops").EnumerateArray()
                             .Select(h => Hop(h, "node_id", "payload", "hex")).ToList();
        var onion = Sphinx.Construct(aliceHops, PrivKey(alice, "session_key"), s_paymentHash);

        // Act
        var atBob = Sphinx.Peel(onion, s_paymentHash, PrivKey(s_vector.GetProperty("decrypt_bob"), "node_privkey"));
        var carolKey = PrivKey(s_vector.GetProperty("decrypt_carol"), "node_privkey");
        var atCarol = Sphinx.Peel(atBob.NextPacket!.Value, s_paymentHash, carolKey);
        var carolTrampoline = TrampolineOnions.PeelWithNodeKey(
            FindTlv(atCarol.Payload.Span, TrampolineOnionPacketType), s_paymentHash, carolKey);
        var generateCarol = s_vector.GetProperty("generate_carol");
        var carolHops = generateCarol.GetProperty("hops").EnumerateArray()
                                     .Select(h => Hop(h, "node_id", "payload", "hex")).ToList();
        var carolOnion = Sphinx.ConstructWithSharedSecrets(carolHops, PrivKey(generateCarol, "session_key"),
                                                           s_paymentHash);
        var atDave = Sphinx.Peel(carolOnion.Packet, s_paymentHash,
                                 PrivKey(s_vector.GetProperty("decrypt_dave"), "node_privkey"));
        var eveKey = PrivKey(s_vector.GetProperty("decrypt_eve"), "node_privkey");
        var atEve = Sphinx.Peel(atDave.NextPacket!.Value, s_paymentHash, eveKey);
        var eveTrampoline = TrampolineOnions.PeelWithNodeKey(FindTlv(atEve.Payload.Span, TrampolineOnionPacketType),
                                                             s_paymentHash, eveKey);

        // Assert: the packets on the wire and the trampoline packet Carol forwards are the vector's
        Assert.Equal(Hex(s_vector.GetProperty("decrypt_bob"), "next_onion"), atBob.NextPacket.Value.ToBytes());
        Assert.Equal(carolTrampoline.NextPacket!.Value.ToBytes(),
                     FindTlv(carolHops[1].Payload.Span, TrampolineOnionPacketType));
        Assert.Equal(Hex(s_vector.GetProperty("decrypt_dave"), "next_onion"), atDave.NextPacket.Value.ToBytes());
        Assert.True(eveTrampoline.IsFinal);
        Assert.Equal(trampolineOnion.SharedSecrets[0], carolTrampoline.SharedSecret);
        Assert.Equal(trampolineOnion.SharedSecrets[1], eveTrampoline.SharedSecret);
        Assert.Equal(carolOnion.SharedSecrets[1], atEve.SharedSecret);
    }
}