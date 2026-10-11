using Microsoft.Extensions.DependencyInjection;
using NBitcoin.Secp256k1;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT7;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// Plan G0-T5 / Proof G0: the BOLT 7 messages captured from LND 0.20 and CLN v26.06.8 (<see cref="Bolt7Vectors"/>)
/// parse through the node's own message serializer, re-serialize byte-identically, lay out their signed data where
/// BOLT 7 says, and every signature verifies against the key it names.
/// </summary>
public class Bolt7CapturedVectorTests
{
    private readonly IMessageSerializer _serializer;

    public Bolt7CapturedVectorTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializationInfrastructureServices();
        _serializer = services.BuildServiceProvider().GetRequiredService<IMessageSerializer>();
    }

    public static TheoryData<Bolt7CapturedMessage> AllVectors => new(Bolt7Vectors.All);

    [Theory]
    [MemberData(nameof(AllVectors))]
    public async Task Given_CapturedMessage_When_RoundTripped_Then_BytesAreIdentical(Bolt7CapturedMessage vector)
    {
        // Arrange
        using var input = new MemoryStream(vector.Wire);

        // Act
        var message = await _serializer.DeserializeMessageAsync(input);
        using var output = new MemoryStream();
        await _serializer.SerializeAsync(message!, output);

        // Assert
        Assert.NotNull(message);
        Assert.Equal((MessageTypes)vector.Type, message.Type);
        Assert.Equal(input.Length, input.Position);
        Assert.Equal(vector.Wire, output.ToArray());
    }

    [Fact]
    public void Given_Captures_Then_EachImplementationSentEveryAnnouncementType()
    {
        // Assert: the proof needs 256, 257 and 258 (and a 259) from both implementations
        foreach (var source in new[] { Bolt7Vectors.Lnd, Bolt7Vectors.Cln })
            foreach (ushort type in new[] { 256, 257, 258, 259 })
                Assert.Contains(source, v => v.Type == type);
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public async Task Given_CapturedAnnouncement_When_Parsed_Then_SignedDataStartsWhereBolt7Says(
        Bolt7CapturedMessage vector)
    {
        // Arrange: BOLT 7 hashes 256 from offset 256 (after the four signatures), 257 and 258 after the one signature
        var signedOffset = vector.Type switch
        {
            256 => 256,
            257 or 258 => 64,
            _ => -1
        };
        if (signedOffset < 0)
            return;

        // Act
        var message = await ParseAsync(vector);
        var hash = message switch
        {
            ChannelAnnouncementMessage m => m.Payload.GetSignatureHash(),
            NodeAnnouncementMessage m => m.Payload.GetSignatureHash(),
            ChannelUpdateMessage m => m.Payload.GetSignatureHash(),
            _ => throw new InvalidOperationException($"Unexpected {message.GetType().Name}")
        };

        // Assert
        var once = System.Security.Cryptography.SHA256.HashData(vector.Payload.AsSpan(signedOffset));
        var expected = System.Security.Cryptography.SHA256.HashData(once);
        Assert.Equal(expected, (byte[])hash);
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public async Task Given_CapturedAnnouncementOrUpdate_When_Verified_Then_EverySignatureIsValid(
        Bolt7CapturedMessage vector)
    {
        // Arrange
        var message = await ParseAsync(vector);
        var announcements = await GetChannelAnnouncementsAsync();

        // Act + Assert
        switch (message)
        {
            case ChannelAnnouncementMessage { Payload: var announcement }:
                var hash = announcement.GetSignatureHash();
                Assert.True(Verify(hash, announcement.NodeSignature1, announcement.NodeId1), "node_signature_1");
                Assert.True(Verify(hash, announcement.NodeSignature2, announcement.NodeId2), "node_signature_2");
                Assert.True(Verify(hash, announcement.BitcoinSignature1, announcement.BitcoinKey1),
                            "bitcoin_signature_1");
                Assert.True(Verify(hash, announcement.BitcoinSignature2, announcement.BitcoinKey2),
                            "bitcoin_signature_2");
                // BOLT 7: node_id_1 is the lexicographically lesser key
                Assert.True(((byte[])announcement.NodeId1).AsSpan().SequenceCompareTo(announcement.NodeId2) < 0);
                Assert.Equal(ChainConstants.Regtest, announcement.ChainHash);
                break;

            case NodeAnnouncementMessage { Payload: var nodeAnnouncement }:
                Assert.True(Verify(nodeAnnouncement.GetSignatureHash(), nodeAnnouncement.Signature,
                                   nodeAnnouncement.NodeId));
                break;

            case ChannelUpdateMessage { Payload: var update }:
                // The origin is node_id_1 for direction 0; a channel that was never announced (CLN's channel to us)
                // is signed by one of the nodes the same implementation announced
                var candidates = announcements.TryGetValue(update.ShortChannelId, out var channel)
                                     ? [update.Direction ? channel.NodeId2 : channel.NodeId1]
                                     : await GetAnnouncedNodesAsync(vector.Source);
                Assert.Contains(candidates, nodeId => Verify(update.GetSignatureHash(), update.Signature, nodeId));
                Assert.Equal(ChainConstants.Regtest, update.ChainHash);
                break;

            case AnnouncementSignaturesMessage { Payload: var signatures }:
                // They sign an announcement that was never completed: the layout is all that can be checked
                Assert.Equal(vector.Payload[32..40], (byte[])signatures.ShortChannelId);
                Assert.True(signatures.ExtraData.IsEmpty);
                break;

            default:
                Assert.Fail($"Unexpected {message.GetType().Name}");
                break;
        }
    }

    private async Task<IMessage> ParseAsync(Bolt7CapturedMessage vector)
    {
        using var input = new MemoryStream(vector.Wire);
        return await _serializer.DeserializeMessageAsync(input)
            ?? throw new InvalidOperationException($"{vector} did not parse");
    }

    private async Task<Dictionary<ShortChannelId, ChannelAnnouncementPayload>> GetChannelAnnouncementsAsync()
    {
        var announcements = new Dictionary<ShortChannelId, ChannelAnnouncementPayload>();
        foreach (var vector in Bolt7Vectors.All.Where(v => v.Type == 256))
        {
            var announcement = (ChannelAnnouncementMessage)await ParseAsync(vector);
            announcements[announcement.Payload.ShortChannelId] = announcement.Payload;
        }

        return announcements;
    }

    private async Task<List<CompactPubKey>> GetAnnouncedNodesAsync(string source)
    {
        var nodes = new List<CompactPubKey>();
        foreach (var vector in Bolt7Vectors.All.Where(v => v.Type == 257 && v.Source == source))
            nodes.Add(((NodeAnnouncementMessage)await ParseAsync(vector)).Payload.NodeId);

        return nodes;
    }

    /// <summary>
    /// Strict ECDSA verification (low-S only): the captured messages come straight from their origin.
    /// </summary>
    private static bool Verify(Hash hash, CompactSignature signature, CompactPubKey pubKey) =>
        SecpECDSASignature.TryCreateFromCompact(signature.Value, out var ecdsaSignature)
     && ECPubKey.TryCreate((byte[])pubKey, Context.Instance, out _, out var ecPubKey)
     && ecPubKey.SigVerify(ecdsaSignature, (byte[])hash);
}