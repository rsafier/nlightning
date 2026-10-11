using System.Security.Cryptography;

namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Keysend;

/// <summary>
/// Builds the Sphinx onion for a <see cref="PaymentRoute"/> (ONION M4-T6).
/// </summary>
/// <remarks>
/// <para>Hop payloads (BOLT 4 writer, outside a blinded route): every hop gets <c>amt_to_forward</c> and
/// <c>outgoing_cltv_value</c>; intermediate hops also get <c>short_channel_id</c>; the payee gets
/// <c>payment_data</c> (<c>payment_secret</c>, <c>total_msat</c> = <see cref="PaymentRoute.TotalAmount"/>: the amount,
/// or the whole payment's amount for one part of a multi-part payment) and, when the invoice has one,
/// <c>payment_metadata</c>. A keysend payment's payee gets <c>keysend_preimage</c> and the custom records instead of
/// <c>payment_data</c> (<see cref="KeysendFinalRecords"/>; no invoice, so no <c>payment_secret</c>).</para>
/// <para>Trampoline (NL-875, BOLTs PR 836): when the route's last hop is a trampoline node, its payload also carries the
/// <c>trampoline_onion_packet</c> (TLV 20) and, for a blinded trampoline path, the next <c>current_path_key</c> (TLV 12)
/// (<see cref="TrampolineFinalHop"/>). Its <c>payment_data</c> is then the route's <see cref="PaymentRoute.PaymentSecret"/>
/// and <see cref="PaymentRoute.TotalAmount"/>, which the caller chooses (a random outer secret, never the invoice's, and
/// what the trampoline node receives in total); it is always sent, also for a single part.</para>
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
    /// <param name="route">The route.</param>
    /// <param name="keysend">The keysend records of the payee's payload, for a keysend payment.</param>
    /// <param name="trampoline">The trampoline records of the last hop's payload, when it is a trampoline node.</param>
    public async Task<PaymentOnion> CreateAsync(PaymentRoute route, KeysendFinalRecords? keysend = null,
                                                TrampolineFinalHop? trampoline = null)
    {
        var sessionKey = CreateSessionKey();
        try
        {
            return await CreateAsync(route, new PrivKey(sessionKey), keysend, trampoline);
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
    public async Task<PaymentOnion> CreateAsync(PaymentRoute route, PrivKey sessionKey,
                                                KeysendFinalRecords? keysend = null,
                                                TrampolineFinalHop? trampoline = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var hops = new List<OnionHop>(route.Hops.Count);
        foreach (var hop in route.Hops)
        {
            using var stream = new MemoryStream();
            await _hopPayloadSerializer.SerializeAsync(CreatePayload(hop, route, keysend, trampoline), stream);
            hops.Add(new OnionHop(hop.NodeId, stream.ToArray()));
        }

        var constructed = _sphinxService.ConstructWithSharedSecrets(hops, sessionKey, route.PaymentHash);
        return new PaymentOnion(route, constructed.Packet, constructed.SharedSecrets);
    }

    /// <summary>
    /// The bytes the hop payloads of <paramref name="route"/> take in the onion, framed as Sphinx frames them (BigSize
    /// length, payload, HMAC); the onion holds <see cref="OnionConstants.HopPayloadsLength"/>.
    /// </summary>
    public async Task<int> GetFramedLengthAsync(PaymentRoute route, KeysendFinalRecords? keysend = null,
                                                TrampolineFinalHop? trampoline = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var total = 0;
        foreach (var hop in route.Hops)
            total += await GetFramedLengthAsync(CreatePayload(hop, route, keysend, trampoline));

        return total;
    }

    /// <summary>
    /// The framed bytes (as <see cref="GetFramedLengthAsync(PaymentRoute, KeysendFinalRecords?, TrampolineFinalHop?)"/>)
    /// of every hop of <paramref name="route"/> but the last: what the outer onion of a trampoline payment spends before
    /// the trampoline node's layer (<c>ITrampolineOnionService.GetMaxHopPayloadsLength</c>).
    /// </summary>
    public async Task<int> GetIntermediateFramedLengthAsync(PaymentRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var total = 0;
        for (var i = 0; i < route.Hops.Count - 1; i++)
            total += await GetFramedLengthAsync(CreatePayload(route.Hops[i], route));

        return total;
    }

    /// <summary>
    /// The length of the TLV records of the route's last payload other than <c>trampoline_onion_packet</c> (its
    /// <c>amt_to_forward</c>, <c>outgoing_cltv_value</c>, <c>payment_data</c> and, with <paramref name="nextPathKey"/>,
    /// <c>current_path_key</c>), without the payload's length prefix.
    /// </summary>
    public async Task<int> GetTrampolineFinalOtherTlvsLengthAsync(PaymentRoute route, CompactPubKey? nextPathKey)
    {
        ArgumentNullException.ThrowIfNull(route);

        using var stream = new MemoryStream();
        var tlvs = CreateTrampolineFinalTlvs(route.Hops[^1], route, null, nextPathKey);
        await _hopPayloadSerializer.SerializeAsync(new HopPayload(tlvs.ToArray()), stream);
        return (int)stream.Length;
    }

    /// <summary>
    /// The framed bytes (as <see cref="GetFramedLengthAsync(PaymentRoute, KeysendFinalRecords?)"/>) of a keysend payee's
    /// layer alone: the least any route to it takes, so a payment whose layer does not fit can be refused before any
    /// route is tried.
    /// </summary>
    public Task<int> GetKeysendFinalFramedLengthAsync(LightningMoney amount, uint outgoingCltvValue,
                                                      KeysendFinalRecords keysend)
    {
        ArgumentNullException.ThrowIfNull(keysend);

        var tlvs = new List<BaseTlv> { new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(outgoingCltvValue) };
        tlvs.AddRange(keysend.ToTlvs());
        return GetFramedLengthAsync(new HopPayload(tlvs.ToArray()));
    }

    private async Task<int> GetFramedLengthAsync(HopPayload payload)
    {
        using var stream = new MemoryStream();
        await _hopPayloadSerializer.SerializeAsync(payload, stream);
        var length = (int)stream.Length;
        var lengthPrefix = length < 0xfd ? 1 : length <= 0xffff ? 3 : 5;
        return lengthPrefix + length + OnionConstants.HmacLength;
    }

    /// <summary>
    /// The hop payload a route hop receives.
    /// </summary>
    /// <param name="hop">The hop.</param>
    /// <param name="route">The route.</param>
    /// <param name="keysend">For a keysend payment, what the payee gets instead of <c>payment_data</c>.</param>
    /// <param name="trampoline">For a route to a trampoline node, the records its (last) payload adds.</param>
    /// <exception cref="ArgumentException">A keysend or trampoline payment through a blinded path, or a keysend payment
    /// to a trampoline node.</exception>
    public static HopPayload CreatePayload(RouteHop hop, PaymentRoute route, KeysendFinalRecords? keysend = null,
                                           TrampolineFinalHop? trampoline = null)
    {
        ArgumentNullException.ThrowIfNull(hop);
        ArgumentNullException.ThrowIfNull(route);
        if (keysend is not null && trampoline is not null)
            throw new ArgumentException("A keysend payment is not sent through a trampoline node.", nameof(trampoline));

        if (hop.EncryptedRecipientData is { } encryptedRecipientData)
        {
            if (keysend is not null)
                throw new ArgumentException("A keysend payment is not paid through a blinded path.", nameof(keysend));
            if (trampoline is not null)
                throw new ArgumentException("A route to a trampoline node does not end in a blinded path.",
                                            nameof(trampoline));

            return CreateBlindedPayload(hop, encryptedRecipientData, route);
        }

        if (trampoline is not null && hop.IsFinal)
            return new HopPayload(CreateTrampolineFinalTlvs(hop, route, trampoline.TrampolinePacket,
                                                            trampoline.NextPathKey).ToArray());

        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(hop.AmountToForward),
            new OutgoingCltvValueTlv(hop.OutgoingCltvValue)
        };

        if (hop.OutgoingShortChannelId is { } shortChannelId)
        {
            tlvs.Add(new OnionShortChannelIdTlv(shortChannelId));
        }
        else if (keysend is not null)
        {
            // keysend (LND, CLN): no payment_data, one HTLC, the preimage and the custom records for the payee
            tlvs.AddRange(keysend.ToTlvs());
        }
        else
        {
            tlvs.Add(new PaymentDataTlv(route.PaymentSecret, route.TotalAmount));
            if (route.PaymentMetadata is { Length: > 0 } metadata)
                tlvs.Add(new PaymentMetadataTlv(metadata.Span));
        }

        return new HopPayload(tlvs.ToArray());
    }

    /// <summary>
    /// The outer payload of a trampoline node (BOLTs PR 836): <c>amt_to_forward</c>, <c>outgoing_cltv_value</c>,
    /// <c>payment_data</c> (the route's secret and total), the next <c>current_path_key</c> when given and the
    /// <c>trampoline_onion_packet</c> when given (its length is measured without it).
    /// </summary>
    private static List<BaseTlv> CreateTrampolineFinalTlvs(RouteHop hop, PaymentRoute route,
                                                           ReadOnlyMemory<byte>? trampolinePacket,
                                                           CompactPubKey? nextPathKey)
    {
        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(hop.AmountToForward),
            new OutgoingCltvValueTlv(hop.OutgoingCltvValue),
            new PaymentDataTlv(route.PaymentSecret, route.TotalAmount)
        };
        if (nextPathKey is { } pathKey)
            tlvs.Add(new CurrentPathKeyTlv(pathKey));
        if (trampolinePacket is { } packet)
            tlvs.Add(new TrampolineOnionPacketTlv(packet.Span));

        return tlvs;
    }

    /// <summary>
    /// BOLT 4 writer inside a blinded route: every blinded hop gets its <c>encrypted_recipient_data</c> (the
    /// introduction node also <c>current_path_key</c>); only the final one gets <c>amt_to_forward</c>,
    /// <c>outgoing_cltv_value</c> and <c>total_amount_msat</c>, and no <c>payment_data</c> (the recipient's
    /// <c>path_id</c> replaces the payment secret).
    /// </summary>
    private static HopPayload CreateBlindedPayload(RouteHop hop, ReadOnlyMemory<byte> encryptedRecipientData,
                                                   PaymentRoute route)
    {
        var tlvs = new List<BaseTlv>();
        if (hop.IsFinal)
        {
            tlvs.Add(new AmtToForwardTlv(hop.AmountToForward));
            tlvs.Add(new OutgoingCltvValueTlv(hop.OutgoingCltvValue));
            tlvs.Add(new TotalAmountMsatTlv(route.TotalAmount));
        }

        tlvs.Add(new EncryptedRecipientDataTlv(encryptedRecipientData.Span));
        if (hop.CurrentPathKey is { } pathKey)
            tlvs.Add(new CurrentPathKeyTlv(pathKey));

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