using System.Security.Cryptography;

namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;

/// <summary>
/// Builds the Sphinx onion for a <see cref="PaymentRoute"/> (ONION M4-T6).
/// </summary>
/// <remarks>
/// <para>Hop payloads (BOLT 4 writer, outside a blinded route): every hop gets <c>amt_to_forward</c> and
/// <c>outgoing_cltv_value</c>; intermediate hops also get <c>short_channel_id</c>; the payee gets
/// <c>payment_data</c> (<c>payment_secret</c>, <c>total_msat</c> = the amount, since we never split payments) and,
/// when the invoice has one, <c>payment_metadata</c>.</para>
/// <para>The session key is 32 bytes from the OS CSPRNG, drawn again until it is a valid secp256k1 scalar, used for
/// this onion only and zeroed afterwards.</para>
/// </remarks>
public sealed class PaymentOnionFactory
{
    // The secp256k1 group order n, big-endian
    private static readonly byte[] s_curveOrder =
        Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");

    private readonly ISphinxService _sphinxService;
    private readonly IHopPayloadSerializer _hopPayloadSerializer;

    public PaymentOnionFactory(ISphinxService sphinxService, IHopPayloadSerializer hopPayloadSerializer)
    {
        _sphinxService = sphinxService;
        _hopPayloadSerializer = hopPayloadSerializer;
    }

    /// <summary>
    /// Builds the onion with a fresh CSPRNG session key.
    /// </summary>
    /// <exception cref="ArgumentException">If the payloads do not fit in the 1300-byte onion.</exception>
    public async Task<PaymentOnion> CreateAsync(PaymentRoute route)
    {
        var sessionKey = CreateSessionKey();
        try
        {
            return await CreateAsync(route, new PrivKey(sessionKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    /// <summary>
    /// Builds the onion with the given session key (tests and vectors; production uses
    /// <see cref="CreateAsync(PaymentRoute)"/>). The key must never be reused.
    /// </summary>
    /// <exception cref="ArgumentException">If the payloads do not fit in the 1300-byte onion.</exception>
    public async Task<PaymentOnion> CreateAsync(PaymentRoute route, PrivKey sessionKey)
    {
        ArgumentNullException.ThrowIfNull(route);

        var hops = new List<OnionHop>(route.Hops.Count);
        foreach (var hop in route.Hops)
        {
            using var stream = new MemoryStream();
            await _hopPayloadSerializer.SerializeAsync(CreatePayload(hop, route), stream);
            hops.Add(new OnionHop(hop.NodeId, stream.ToArray()));
        }

        var constructed = _sphinxService.ConstructWithSharedSecrets(hops, sessionKey, route.PaymentHash);
        return new PaymentOnion(route, constructed.Packet, constructed.SharedSecrets);
    }

    /// <summary>
    /// The hop payload a route hop receives.
    /// </summary>
    public static HopPayload CreatePayload(RouteHop hop, PaymentRoute route)
    {
        ArgumentNullException.ThrowIfNull(hop);
        ArgumentNullException.ThrowIfNull(route);

        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(hop.AmountToForward),
            new OutgoingCltvValueTlv(hop.OutgoingCltvValue)
        };

        if (hop.OutgoingShortChannelId is { } shortChannelId)
        {
            tlvs.Add(new OnionShortChannelIdTlv(shortChannelId));
        }
        else
        {
            tlvs.Add(new PaymentDataTlv(route.PaymentSecret, hop.AmountToForward));
            if (route.PaymentMetadata is { Length: > 0 } metadata)
                tlvs.Add(new PaymentMetadataTlv(metadata.Span));
        }

        return new HopPayload(tlvs.ToArray());
    }

    internal static byte[] CreateSessionKey()
    {
        var key = new byte[CryptoConstants.PrivkeyLen];
        do
        {
            RandomNumberGenerator.Fill(key);
        } while (!IsValidScalar(key));

        return key;
    }

    internal static bool IsValidScalar(ReadOnlySpan<byte> key) =>
        key.Length == CryptoConstants.PrivkeyLen
     && key.IndexOfAnyExcept((byte)0) >= 0
     && key.SequenceCompareTo(s_curveOrder) < 0;
}