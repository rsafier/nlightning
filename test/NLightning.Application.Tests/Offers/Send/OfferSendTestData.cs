namespace NLightning.Application.Tests.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Shared values of the payer tests.
/// </summary>
internal static class OfferSendTestData
{
    public static readonly ChainHash Chain = ChainConstants.Regtest;

    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public static readonly Hash PaymentHash = new(Enumerable.Repeat((byte)0x77, 32).ToArray());

    public static CompactPubKey Key(byte seed) => TestBolt12Signer.PubKeyOf(Enumerable.Repeat(seed, 32).ToArray());

    /// <summary>
    /// A two-hop blinded payment path introduced by <paramref name="introduction"/> (keys only; nobody can decrypt
    /// it).
    /// </summary>
    public static BlindedPaymentPath PaymentPath(CompactPubKey introduction, ulong htlcMaximumMsat = 1_000_000_000,
                                                 byte[]? features = null) =>
        new(new BlindedPath(introduction, Key(0x21), [
                new BlindedPathHop(Key(0x22), new byte[] { 1, 2, 3 }),
                new BlindedPathHop(Key(0x23), new byte[] { 4, 5, 6 })
            ]),
            new BlindedPayInfo(1_000, 100, 40, 1, htlcMaximumMsat, features ?? []));

    /// <summary>
    /// A two-hop blinded message path introduced by <paramref name="introduction"/>, ending at
    /// <paramref name="recipient"/>.
    /// </summary>
    public static WireBlindedPath MessagePath(CompactPubKey introduction, CompactPubKey recipient) =>
        new(SciddirOrPubkey.FromNodeId(introduction), Key(0x31), [
            new BlindedPathHop(Key(0x32), new byte[] { 7, 8 }),
            new BlindedPathHop(recipient, new byte[] { 9, 10 })
        ]);
}