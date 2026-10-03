namespace NLightning.Infrastructure.Bitcoin.Onion.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.ValueObjects;

/// <summary>
/// BOLT 4 trampoline onion (BOLTs PR 836) over the payment onion's Sphinx core (<see cref="ISphinxService"/>).
/// </summary>
/// <remarks>
/// Stateless and thread-safe. The packet is a payment onion (payloads of at least 2 bytes, failures inside a blinded
/// route reported as <c>invalid_onion_blinding</c>) whose <c>hop_payloads</c> length is chosen by a
/// <see cref="TrampolineOnionSizePolicy"/> when building and read from the TLV value's length when peeling.
/// </remarks>
internal sealed class TrampolineOnionService : ITrampolineOnionService
{
    /// <summary>
    /// The type of the outer payload's <c>trampoline_onion_packet</c> record, reported in <c>invalid_onion_payload</c>.
    /// </summary>
    private const ulong TrampolineOnionPacketTlvType = 20;

    private readonly ISphinxService _sphinxService;

    public TrampolineOnionService(ISphinxService sphinxService)
    {
        _sphinxService = sphinxService ?? throw new ArgumentNullException(nameof(sphinxService));
    }

    /// <inheritdoc/>
    public TrampolineOnion Build(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> paymentHash,
                                 TrampolineOnionSizePolicy sizePolicy)
    {
        ArgumentNullException.ThrowIfNull(hops);
        if (hops.Count == 0)
            throw new ArgumentException("The trampoline route must have at least one hop.", nameof(hops));

        ValidatePaymentHash(paymentHash);

        var framedLength = 0L;
        for (var i = 0; i < hops.Count; i++)
        {
            if (hops[i] is null)
                throw new ArgumentException($"Trampoline hop {i} is null.", nameof(hops));

            framedLength += GetFramedPayloadLength(hops[i].Payload.Length);
            if (framedLength > OnionConstants.MaxErrorPacketLength)
                throw new ArgumentException($"The trampoline payloads exceed {OnionConstants.MaxErrorPacketLength} bytes.",
                                            nameof(hops));
        }

        var hopPayloadsLength = sizePolicy.ResolveHopPayloadsLength((int)framedLength);
        var constructed = _sphinxService.ConstructWithSharedSecrets(hops, sessionKey, paymentHash, hopPayloadsLength,
                                                                    OnionPacketKind.Payment);
        return new TrampolineOnion(constructed.Packet, constructed.SharedSecrets);
    }

    /// <inheritdoc/>
    public PeeledOnion Peel(ReadOnlyMemory<byte> trampolinePacket, ReadOnlySpan<byte> paymentHash,
                            CompactPubKey? pathKey = null)
    {
        ValidatePaymentHash(paymentHash);
        var packet = ParsePacket(trampolinePacket);
        return _sphinxService.PeelAsLocalNode(packet, paymentHash, pathKey, OnionPacketKind.Payment);
    }

    /// <inheritdoc/>
    public PeeledOnion PeelWithNodeKey(ReadOnlyMemory<byte> trampolinePacket, ReadOnlySpan<byte> paymentHash,
                                       PrivKey nodeKey, CompactPubKey? pathKey = null)
    {
        ValidatePaymentHash(paymentHash);
        var packet = ParsePacket(trampolinePacket);
        return _sphinxService.Peel(packet, paymentHash, nodeKey, pathKey, OnionPacketKind.Payment);
    }

    /// <inheritdoc/>
    public int GetFramedPayloadLength(int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        return SphinxBigSize.GetEncodedLength((ulong)payloadLength) + payloadLength + OnionConstants.HmacLength;
    }

    /// <inheritdoc/>
    public int GetMaxHopPayloadsLength(int outerHopsFramedLength, int finalPayloadOtherTlvsLength,
                                       int outerHopPayloadsLength = OnionConstants.HopPayloadsLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outerHopsFramedLength);
        ArgumentOutOfRangeException.ThrowIfNegative(finalPayloadOtherTlvsLength);
        ArgumentOutOfRangeException.ThrowIfNegative(outerHopPayloadsLength);

        // The outer final hop takes bigsize(P) + P + 32, with P = other TLVs + type(1) + bigsize(V) + V and
        // V = packet overhead + L. Start from the bound without any bigsize length and step down while the varying
        // bigsize widths do not fit (a few steps at most).
        var remaining = (long)outerHopPayloadsLength - outerHopsFramedLength - OnionConstants.HmacLength;
        var candidate = remaining - finalPayloadOtherTlvsLength - 1 - OnionConstants.PacketOverheadLength;
        while (candidate > 0 && GetFinalHopCost(candidate, finalPayloadOtherTlvsLength) > remaining)
            candidate--;

        return candidate > 0 ? (int)candidate : 0;
    }

    private static long GetFinalHopCost(long hopPayloadsLength, int otherTlvsLength)
    {
        var tlvValueLength = OnionConstants.PacketOverheadLength + hopPayloadsLength;
        var payloadLength = otherTlvsLength + 1 + SphinxBigSize.GetEncodedLength((ulong)tlvValueLength)
                          + tlvValueLength;
        return SphinxBigSize.GetEncodedLength((ulong)payloadLength) + payloadLength;
    }

    private static OnionPacket ParsePacket(ReadOnlyMemory<byte> trampolinePacket)
    {
        // A value that cannot even hold version, key, one byte of payloads and the HMAC is a malformed TLV 20 of the
        // outer payload, not a bad trampoline onion: there is no packet to hash for a BADONION code
        if (trampolinePacket.Length < TrampolineOnionConstants.MinPacketLength)
            throw InvalidOnionPayloadFailureFactory.Create(
                new BigSize(TrampolineOnionPacketTlvType), 0,
                $"The trampoline onion packet is {trampolinePacket.Length} bytes; at least "
              + $"{TrampolineOnionConstants.MinPacketLength} are needed.");

        return new OnionPacket(trampolinePacket.Span,
                               trampolinePacket.Length - OnionConstants.PacketOverheadLength);
    }

    private static void ValidatePaymentHash(ReadOnlySpan<byte> paymentHash)
    {
        if (paymentHash.Length != TrampolineOnionConstants.AssociatedDataLength)
            throw new ArgumentException(
                $"The payment hash must be {TrampolineOnionConstants.AssociatedDataLength} bytes.",
                nameof(paymentHash));
    }
}