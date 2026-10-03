namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;

/// <summary>
/// Guards the pooled sphinx hot path (NL-083): rented generators come back clean, concurrent builds and peels never
/// share hash state, and the per-operation managed allocations stay where the benchmark drove them.
/// </summary>
public sealed class SphinxHotPathTests
{
    private const int Hops = 5;
    private const int PayloadLength = 200;
    private const int AllocationIterations = 1_000;

    private static long s_sink;

    // BOLT 4 onion-error-test.json hops[0]
    private static readonly byte[] s_hop0SharedSecret =
        Convert.FromHexString("53eb63ea8a3fec3b3cd433b85cd62a4b145e1dda09391b348c4e1cd36a03ea66");

    private static readonly byte[] s_hop0AmmagKey =
        Convert.FromHexString("3761ba4d3e726d8abb16cba5950ee976b84937b61b7ad09e741724d7dee12eb5");

    private static readonly byte[] s_associatedData = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private readonly Ecdh _ecdh = new();
    private readonly SphinxService _sphinxService = new(new Secp256K1Math());

    [Fact]
    public void Given_ARentedGenerator_When_ReusedAcrossRentCycles_Then_VectorKeysStayCorrect()
    {
        // A returned generator must come back clean: the same vector key has to come out of every rent cycle.
        for (var i = 0; i < 4; i++)
        {
            using var keyGenerator = SphinxKeyGenerator.Rent();
            Assert.Equal(s_hop0AmmagKey, keyGenerator.DeriveKey(OnionConstants.Ammag, s_hop0SharedSecret));
        }
    }

    [Fact]
    public async Task Given_ConcurrentBuildsAndPeels_When_HammeringThePooledPath_Then_EveryPayloadRoundTrips()
    {
        // Arrange: one shared service and several tasks; pooled generators must never share state across operations.
        const int tasks = 8;
        const int iterationsPerTask = 25;

        // Act
        await Task.WhenAll(Enumerable.Range(0, tasks).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < iterationsPerTask; i++)
                RoundTrip(t * iterationsPerTask + i);
        }, TestContext.Current.CancellationToken)));

        return;

        void RoundTrip(int seed)
        {
            var random = new Random(seed);
            var nodeKeys = Enumerable.Range(0, Hops).Select(_ => _ecdh.GenerateKeyPair()).ToList();
            var hops = nodeKeys.Select(k => new OnionHop(k.CompactPubKey, Payload(random))).ToList();
            var sessionKey = _ecdh.GenerateKeyPair().PrivKey;

            var packet = _sphinxService.Construct(hops, sessionKey, s_associatedData);
            for (var i = 0; i < Hops; i++)
            {
                var peeled = _sphinxService.Peel(packet, s_associatedData, nodeKeys[i].PrivKey);
                Assert.Equal(hops[i].Payload.ToArray(), peeled.Payload.ToArray());
                Assert.Equal(i == Hops - 1, peeled.NextPacket is null);

                if (peeled.NextPacket is not { } next)
                    break;

                packet = next;
            }
        }
    }

    [Fact]
    public void Given_WarmService_When_BuildingAFiveHopOnion_Then_AllocationsStayBounded()
    {
        // Arrange
        var (nodeKeys, hops, sessionKey, _) = BuildRoute();
        for (var i = 0; i < 200; i++)
            _sphinxService.Construct(hops, sessionKey, s_associatedData);

        // Act
        var perOp = PerOpBytes(() => s_sink += _sphinxService.Construct(hops, sessionKey, s_associatedData).Length);

        // Assert: pooling removed the per-op generator; the payload, filler and shared-secret buffers remain
        Assert.True(perOp <= 12_800, $"Allocated {perOp} bytes per 5-hop build.");
    }

    [Fact]
    public void Given_WarmService_When_PeelingAFiveHopOnion_Then_AllocationsStayBounded()
    {
        // Arrange: peeling never modifies its input packet, so one pre-peeled chain serves every measured op
        var (nodeKeys, _, _, packets) = BuildRoute();
        for (var i = 0; i < 200; i++)
            _sphinxService.Peel(packets[0], s_associatedData, nodeKeys[0]);

        // Act
        var perLayer = PerOpBytes(() =>
            s_sink += _sphinxService.Peel(packets[0], s_associatedData, nodeKeys[0]).Payload.Length);
        var perRoute = PerOpBytes(() =>
        {
            for (var i = 0; i < Hops; i++)
                s_sink += _sphinxService.Peel(packets[i], s_associatedData, nodeKeys[i]).Payload.Length;
        });

        // Assert: the unwrapped (2 x 1300 bytes) buffer dominates; the pooled hash state must not add to it
        Assert.True(perLayer <= 7_168, $"Allocated {perLayer} bytes per peel.");
        Assert.True(perRoute <= 32_768, $"Allocated {perRoute} bytes per 5-hop peel.");
    }

    private static byte[] Payload(Random random)
    {
        var payload = new byte[PayloadLength];
        random.NextBytes(payload);
        return payload;
    }

    private (List<PrivKey> NodeKeys, List<OnionHop> Hops, PrivKey SessionKey, OnionPacket[] Packets) BuildRoute()
    {
        var nodeKeys = Enumerable.Range(0, Hops).Select(_ => _ecdh.GenerateKeyPair().PrivKey).ToList();
        var hops = nodeKeys.Select((k, i) => new OnionHop(PubKeyOf(k),
                                   Enumerable.Repeat((byte)(i + 1), PayloadLength).ToArray())).ToList();
        var sessionKey = _ecdh.GenerateKeyPair().PrivKey;

        var packet = _sphinxService.Construct(hops, sessionKey, s_associatedData);
        var packets = new OnionPacket[Hops];
        packets[0] = packet;
        for (var i = 1; i < Hops; i++)
            packets[i] = _sphinxService.Peel(packets[i - 1], s_associatedData, nodeKeys[i - 1]).NextPacket!.Value;

        return (nodeKeys, hops, sessionKey, packets);
    }

    private CompactPubKey PubKeyOf(PrivKey privKey)
    {
        using var ecPrivKey = SphinxKeyGenerator.CreatePrivateKey(privKey.Value, nameof(privKey));
        Span<byte> pubKey = stackalloc byte[CryptoConstants.CompactPubkeyLen];
        ecPrivKey.CreatePubKey().WriteToSpan(true, pubKey, out _);
        return new CompactPubKey(pubKey.ToArray());
    }

    private static long PerOpBytes(Action operation)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationIterations; i++)
            operation();

        return (GC.GetAllocatedBytesForCurrentThread() - before) / AllocationIterations;
    }
}