using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using static TrampolineVectorKit;

/// <summary>
/// BOLTs PR 836 <c>trampoline-to-blinded-path-payment-onion-test.json</c>, byte for byte: [0] Carol, the trampoline,
/// pays Eve's blinded path (Dave introduction node) from <c>recipient_blinded_paths</c>; [1] every node of Eve's path
/// is a trampoline hop (Dave the introduction node, Eve blinded).
/// </summary>
public class TrampolineBlindedPathVectorTests
{
    private static readonly JsonElement s_vectors = Load("trampoline-to-blinded-path-payment-onion-test.json");
    private static readonly JsonElement s_toBlindedPath = s_vectors[0];
    private static readonly JsonElement s_blindedTrampoline = s_vectors[1];

    #region [0] Carol pays the blinded path

    [Fact]
    public void Given_EvesPathData_When_CreatingTheBlindedPath_Then_PathMatches()
    {
        // Arrange
        var generate = s_toBlindedPath.GetProperty("generate_blinded_path_eve");
        var hops = generate.GetProperty("hops").EnumerateArray().ToList();

        // Act
        var path = RouteBlinding.CreateBlindedPath(hops.Select(h => PubKey(h, "node_id")).ToList(),
                                                   hops.Select(h => Hex(h, "encoded_tlvs")).ToList(),
                                                   PrivKey(generate, "session_key"));

        // Assert
        Assert.Equal(PubKey(generate, "introduction_node_id"), path.FirstNodeId);
        Assert.Equal(PubKey(generate, "path_key"), path.FirstPathKey);
        for (var i = 0; i < hops.Count; i++)
        {
            Assert.Equal(PubKey(hops[i], "blinded_node_id"), path.Hops[i].BlindedNodeId);
            Assert.Equal(Hex(hops[i], "encrypted_data"), path.Hops[i].EncryptedRecipientData.ToArray());
        }
    }

    [Fact]
    public void Given_TheTrampolinePayloadForCarol_When_Building_Then_TrampolineOnionMatches()
    {
        // Arrange
        var generate = s_toBlindedPath.GetProperty("generate_trampoline_onion");
        var hop = new OnionHop(PubKey(generate, "trampoline_node_id"),
                               StripLengthPrefix(Hex(generate, "encoded_tlvs")));

        // Act
        var onion = TrampolineOnions.Build([hop], PrivKey(generate, "session_key"), Hex(generate, "associated_data"),
                                           TrampolineOnionSizePolicy.Exact);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(generate, "trampoline_onion")),
                     Convert.ToHexStringLower(onion.ToTlvValue()));
        Assert.Equal(538, onion.Packet.Length);
    }

    [Fact]
    public void Given_ThePaymentOnion_When_BuiltAndPeeledByBobAndCarol_Then_EveryStageMatches()
    {
        // Arrange
        var generate = s_toBlindedPath.GetProperty("generate_payment_onion");
        var paymentHash = Hex(generate, "associated_data");
        var hops = generate.GetProperty("hops").EnumerateArray().Select(h => Hop(h, "pubkey", "encoded_tlvs"))
                           .ToList();
        var bob = s_toBlindedPath.GetProperty("relay_payment_bob");
        var carol = s_toBlindedPath.GetProperty("decrypt_onion_carol");
        var carolKey = PrivKey(carol, "node_privkey");
        var trampolineGenerate = s_toBlindedPath.GetProperty("generate_trampoline_onion");

        // Act
        var onion = Sphinx.Construct(hops, PrivKey(generate, "session_key"), paymentHash);
        var atBob = Sphinx.Peel(new OnionPacket(Hex(bob, "onion")), paymentHash, PrivKey(bob, "node_privkey"));
        var atCarol = Sphinx.Peel(new OnionPacket(Hex(carol, "onion")), paymentHash, carolKey);
        var carolTrampoline = TrampolineOnions.PeelWithNodeKey(
            FindTlv(atCarol.Payload.Span, TrampolineOnionPacketType), paymentHash, carolKey);

        // Assert
        Assert.Equal(Hex(generate, "onion"), onion.ToBytes());
        Assert.Equal(Hex(trampolineGenerate, "trampoline_onion"),
                     FindTlv(hops[1].Payload.Span, TrampolineOnionPacketType));
        Assert.Equal(Hex(bob, "next_onion"), atBob.NextPacket!.Value.ToBytes());
        Assert.True(atCarol.IsFinal);
        Assert.Equal(hops[1].Payload.ToArray(), atCarol.Payload.ToArray());
        Assert.True(carolTrampoline.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(trampolineGenerate, "encoded_tlvs")), carolTrampoline.Payload.ToArray());
    }

    [Fact]
    public void Given_CarolsOnionToTheBlindedPath_When_BuiltAndPeeledByDaveAndEve_Then_EveryStageMatches()
    {
        // Arrange
        var relayCarol = s_toBlindedPath.GetProperty("relay_payment_carol");
        var paymentHash = Hex(relayCarol, "associated_data");
        var hops = relayCarol.GetProperty("hops").EnumerateArray().Select(h => Hop(h, "pubkey", "encoded_tlvs"))
                             .ToList();
        var dave = s_toBlindedPath.GetProperty("relay_payment_dave");
        var eve = s_toBlindedPath.GetProperty("receive_payment_eve");
        var pathHops = s_toBlindedPath.GetProperty("generate_blinded_path_eve").GetProperty("hops");
        var daveKey = PrivKey(dave, "node_privkey");
        var eveKey = PrivKey(eve, "node_privkey");

        // Act: Dave is the introduction node (his payload's current_path_key), Eve gets the path key alongside
        var onion = Sphinx.Construct(hops, PrivKey(relayCarol, "session_key"), paymentHash);
        var atDave = Sphinx.Peel(new OnionPacket(Hex(dave, "onion")), paymentHash, daveKey);
        var daveData = RouteBlinding.Unblind(daveKey, PubKey(dave.GetProperty("tlvs"), "current_path_key"),
                                             FindTlv(atDave.Payload.Span, 10));
        var atEve = Sphinx.Peel(new OnionPacket(Hex(eve, "onion")), paymentHash, eveKey, PubKey(eve, "path_key"));
        var eveData = RouteBlinding.Unblind(eveKey, PubKey(eve, "path_key"), FindTlv(atEve.Payload.Span, 10));

        // Assert
        Assert.Equal(Hex(relayCarol, "onion"), onion.ToBytes());
        Assert.Equal(Hex(dave, "next_onion"), atDave.NextPacket!.Value.ToBytes());
        Assert.Equal(Hex(pathHops[0], "encoded_tlvs"), daveData.DecryptedData.ToArray());
        Assert.Equal(PubKey(eve, "path_key"), daveData.NextPathKey);
        Assert.True(atEve.IsFinal);
        Assert.Equal(hops[1].Payload.ToArray(), atEve.Payload.ToArray());
        Assert.Equal(Hex(eve, "path_id"), eveData.RecipientData.PathId!.Value.ToArray());
    }

    #endregion

    #region [1] Every blinded hop is a trampoline hop

    [Fact]
    public void Given_TheTrampolineHopsOverEvesPath_When_Building_Then_TrampolineOnionMatches()
    {
        // Arrange
        var generate = s_blindedTrampoline.GetProperty("generate_trampoline_onion");
        var hops = generate.GetProperty("hops").EnumerateArray().Select(h => Hop(h, "pubkey", "encoded_tlvs"))
                           .ToList();

        // Act
        var onion = TrampolineOnions.Build(hops, PrivKey(generate, "session_key"), Hex(generate, "associated_data"),
                                           TrampolineOnionSizePolicy.Exact);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(Hex(generate, "trampoline_onion")),
                     Convert.ToHexStringLower(onion.ToTlvValue()));
        Assert.Equal(544, onion.Packet.Length);
    }

    [Fact]
    public void Given_TheBlindedTrampolineRoute_When_ForwardedHopByHop_Then_EveryStageMatches()
    {
        // Arrange
        var vector = s_blindedTrampoline;
        var trampolineHops = vector.GetProperty("generate_trampoline_onion").GetProperty("hops");
        var pathHops = vector.GetProperty("generate_blinded_path_eve").GetProperty("hops");
        var generatePayment = vector.GetProperty("generate_payment_onion");
        var paymentHash = Hex(generatePayment, "associated_data");
        var aliceHops = generatePayment.GetProperty("hops").EnumerateArray()
                                       .Select(h => Hop(h, "pubkey", "encoded_tlvs")).ToList();
        var bob = vector.GetProperty("relay_payment_bob");
        var carolKey = PrivKey(vector.GetProperty("decrypt_onion_carol"), "node_privkey");
        var relayCarol = vector.GetProperty("relay_payment_carol");
        var carolHop = Hop(relayCarol.GetProperty("hops")[0], "pubkey", "encoded_tlvs");
        var daveKey = PrivKey(vector.GetProperty("decrypt_onion_dave"), "node_privkey");
        var relayDave = vector.GetProperty("relay_payment_dave");
        var daveHop = Hop(relayDave.GetProperty("hops")[0], "pubkey", "encoded_tlvs");
        var eveKey = PrivKey(vector.GetProperty("receive_payment_eve"), "node_privkey");

        // Act: Alice's payment onion, Bob, Carol (both layers), Carol's onion to Dave, Dave (both layers; the
        // introduction node peels the trampoline layer with his node key and reads its current_path_key), Dave's
        // onion to Eve, Eve (the trampoline layer with the outer payload's current_path_key)
        var aliceOnion = Sphinx.Construct(aliceHops, PrivKey(generatePayment, "session_key"), paymentHash);
        var atBob = Sphinx.Peel(aliceOnion, paymentHash, PrivKey(bob, "node_privkey"));
        var atCarol = Sphinx.Peel(atBob.NextPacket!.Value, paymentHash, carolKey);
        var carolTrampoline = TrampolineOnions.PeelWithNodeKey(
            FindTlv(atCarol.Payload.Span, TrampolineOnionPacketType), paymentHash, carolKey);
        var carolOnion = Sphinx.Construct([carolHop], PrivKey(relayCarol, "session_key"), paymentHash);
        var atDave = Sphinx.Peel(carolOnion, paymentHash, daveKey);
        var daveTrampoline = TrampolineOnions.PeelWithNodeKey(
            FindTlv(atDave.Payload.Span, TrampolineOnionPacketType), paymentHash, daveKey);
        var daveData = RouteBlinding.Unblind(daveKey,
                                             new CompactPubKey(FindTlv(daveTrampoline.Payload.Span,
                                                                       CurrentPathKeyType)),
                                             FindTlv(daveTrampoline.Payload.Span, 10));
        var daveOnion = Sphinx.Construct([daveHop], PrivKey(relayDave, "session_key"), paymentHash);
        var atEve = Sphinx.Peel(daveOnion, paymentHash, eveKey);
        var evePathKey = new CompactPubKey(FindTlv(atEve.Payload.Span, CurrentPathKeyType));
        var eveTrampoline = TrampolineOnions.PeelWithNodeKey(FindTlv(atEve.Payload.Span, TrampolineOnionPacketType),
                                                             paymentHash, eveKey, evePathKey);
        var eveData = RouteBlinding.Unblind(eveKey, evePathKey, FindTlv(eveTrampoline.Payload.Span, 10));

        // Assert
        Assert.Equal(Hex(generatePayment, "onion"), aliceOnion.ToBytes());
        Assert.Equal(Hex(vector.GetProperty("generate_trampoline_onion"), "trampoline_onion"),
                     FindTlv(aliceHops[1].Payload.Span, TrampolineOnionPacketType));
        Assert.Equal(Hex(bob, "next_onion"), atBob.NextPacket.Value.ToBytes());
        Assert.Equal(StripLengthPrefix(Hex(trampolineHops[0], "encoded_tlvs")), carolTrampoline.Payload.ToArray());
        Assert.Equal(carolTrampoline.NextPacket!.Value.ToBytes(),
                     FindTlv(carolHop.Payload.Span, TrampolineOnionPacketType));
        Assert.Equal(Hex(relayCarol, "onion"), carolOnion.ToBytes());
        Assert.True(atDave.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(trampolineHops[1], "encoded_tlvs")), daveTrampoline.Payload.ToArray());
        Assert.Equal(Hex(pathHops[0], "encoded_tlvs"), daveData.DecryptedData.ToArray());
        Assert.Equal(evePathKey, daveData.NextPathKey);
        Assert.Equal(daveTrampoline.NextPacket!.Value.ToBytes(),
                     FindTlv(daveHop.Payload.Span, TrampolineOnionPacketType));
        Assert.Equal(Hex(relayDave, "onion"), daveOnion.ToBytes());
        Assert.Equal(Hex(vector.GetProperty("receive_payment_eve"), "onion"), daveOnion.ToBytes());
        Assert.True(atEve.IsFinal);
        Assert.Equal(daveHop.Payload.ToArray(), atEve.Payload.ToArray());
        Assert.True(eveTrampoline.IsFinal);
        Assert.Equal(StripLengthPrefix(Hex(trampolineHops[2], "encoded_tlvs")), eveTrampoline.Payload.ToArray());
        Assert.Equal(Hex(pathHops[1], "encoded_tlvs"), eveData.DecryptedData.ToArray());
    }

    #endregion
}