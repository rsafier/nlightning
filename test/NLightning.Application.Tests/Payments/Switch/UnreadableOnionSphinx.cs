namespace NLightning.Application.Tests.Payments.Switch;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// An <see cref="ISphinxService"/> for switch tests whose onions are never read: every peel fails as a bad onion (Moq
/// cannot mock the span parameters, and an unconfigured mock throws <see cref="InvalidProgramException"/>).
/// </summary>
internal sealed class UnreadableOnionSphinx : ISphinxService
{
    public OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> associatedData,
                                 int hopPayloadsLength, OnionPacketKind packetKind) =>
        throw new NotSupportedException();

    public ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                       ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                                       OnionPacketKind packetKind) =>
        throw new NotSupportedException();

    public IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey) =>
        throw new NotSupportedException();

    public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData, CompactPubKey? pathKey,
                                       OnionPacketKind packetKind) =>
        throw new OnionException(FailureCode.InvalidOnionHmac, "unreadable test onion");

    public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                            CompactPubKey? pathKey, OnionPacketKind packetKind) =>
        throw new NotSupportedException();
}