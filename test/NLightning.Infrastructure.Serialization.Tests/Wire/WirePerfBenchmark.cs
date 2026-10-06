using NLightning.Infrastructure.Serialization.Wire;

namespace NLightning.Infrastructure.Serialization.Tests.Wire;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A micro-benchmark of the serialized hot path: decode and encode of an update_add_htlc-shaped message (full
/// 1366-byte onion) and a commitment_signed with 8 HTLC signatures, over the composed message serializer.
/// Explicit (<c>Category=Benchmark</c>); run with
/// <c>dotnet test ... --filter "FullyQualifiedName~WirePerfBenchmark" -- xUnit.Explicit=only</c>.
/// </summary>
public class WirePerfBenchmark(ITestOutputHelper output)
{
    private const int Iterations = 20_000;

    [Fact(Explicit = true)]
    [Trait("Category", "Benchmark")]
    public async Task HotPathDecodeEncode()
    {
        var messageSerializer = new NLightning.Infrastructure.Serialization.Messages.MessageSerializer(
            NullLogger<NLightning.Infrastructure.Serialization.Messages.MessageSerializer>.Instance,
            SerializerHelperMessageFactory());

        var add = new UpdateAddHtlcMessage(new UpdateAddHtlcPayload(
            LightningMoney.MilliSatoshis(150_000), RandomChannelId(1), 600_000, 42, RandomBytes(32),
            RandomBytes(OnionConstants.PacketLength)));
        var addBytes = await EncodeAsync(messageSerializer, add);

        var signatures = Enumerable.Range(0, 8).Select(_ => new CompactSignature(RandomBytes(64))).ToList();
        var commit = new CommitmentSignedMessage(
            new CommitmentSignedPayload(RandomChannelId(2), signatures, new CompactSignature(RandomBytes(64))),
            new Domain.Protocol.Tlv.FundingTxIdTlv(new TxId(RandomBytes(32))));
        var commitBytes = await EncodeAsync(messageSerializer, commit);

        // Warmup
        for (var i = 0; i < 1_000; i++)
        {
            await DecodeAsync(messageSerializer, addBytes);
            await DecodeAsync(messageSerializer, commitBytes);
            await EncodeAsync(messageSerializer, add);
            await EncodeAsync(messageSerializer, commit);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++)
            await DecodeAsync(messageSerializer, addBytes);
        var addDecodeMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (var i = 0; i < Iterations; i++)
            await EncodeAsync(messageSerializer, add);
        var addEncodeMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (var i = 0; i < Iterations / 2; i++)
            await DecodeAsync(messageSerializer, commitBytes);
        var commitDecodeMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (var i = 0; i < Iterations / 2; i++)
            await EncodeAsync(messageSerializer, commit);
        var commitEncodeMs = sw.Elapsed.TotalMilliseconds;

        output.WriteLine(
            $"WIREPERF update_add_htlc decode {addDecodeMs / Iterations:F4} ms/op, encode {addEncodeMs / Iterations:F4} ms/op; "
          + $"commitment_signed decode {commitDecodeMs / (Iterations / 2):F4} ms/op, encode {commitEncodeMs / (Iterations / 2):F4} ms/op");
    }

    private static async Task<byte[]> EncodeAsync(IMessageSerializer serializer, IMessage message)
    {
        using var stream = new MemoryStream();
        await serializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private static async Task<IMessage?> DecodeAsync(IMessageSerializer serializer, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await serializer.DeserializeMessageAsync(stream);
    }

    private static WireRegistry SerializerHelperMessageFactory()
    {
        return new WireRegistry();
    }

    private static readonly Random s_random = new(20261004);

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        s_random.NextBytes(bytes);
        return bytes;
    }

    private static ChannelId RandomChannelId(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());
}
