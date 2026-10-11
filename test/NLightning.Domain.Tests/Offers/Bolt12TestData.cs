namespace NLightning.Domain.Tests.Offers;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

/// <summary>
/// Well-formed BOLT 12 streams for the unit tests. Keys are points from <c>bolt12/offers-test.json</c> (on the curve).
/// </summary>
internal static class Bolt12TestData
{
    /// <summary>
    /// <c>offer_issuer_id</c> of most <c>offers-test.json</c> offers.
    /// </summary>
    public static readonly CompactPubKey IssuerId =
        Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619").AsSpan();

    /// <summary>
    /// Bob (0x4242...) in <c>offers-test.json</c>'s blinded path.
    /// </summary>
    public static readonly CompactPubKey PayerId =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c").AsSpan();

    /// <summary>
    /// 0x0202...02, a point on the curve (the vectors' path_key).
    /// </summary>
    public static readonly CompactPubKey PathKey = Enumerable.Repeat((byte)0x02, 33).ToArray().AsSpan();

    /// <summary>
    /// 0x0303...03, not a point on the curve.
    /// </summary>
    public static readonly byte[] OffCurvePoint = Enumerable.Repeat((byte)0x03, 33).ToArray();

    public static WireBlindedPath Path(CompactPubKey? firstNode = null) =>
        new(SciddirOrPubkey.FromNodeId(firstNode ?? PayerId), PathKey,
            [new BlindedPathHop(PathKey, new byte[] { 0x11, 0x22 })]);

    public static BlindedPayInfo PayInfo(byte[]? features = null) =>
        new(1000, 100, 144, 1, 1_000_000_000, features ?? []);

    public static Bolt12TlvStreamBuilder OfferBuilder() =>
        new Bolt12TlvStreamBuilder()
           .SetUtf8(Bolt12TlvTypes.OfferDescription, "coffee")
           .SetTu64(Bolt12TlvTypes.OfferAmount, 10_000)
           .SetPoint(Bolt12TlvTypes.OfferIssuerId, IssuerId);

    public static Bolt12TlvStreamBuilder InvoiceRequestBuilder(Bolt12TlvStreamBuilder? offer = null) =>
        new Bolt12TlvStreamBuilder((offer ?? OfferBuilder()).Build())
           .Set(Bolt12TlvTypes.InvreqMetadata, new byte[] { 1, 2, 3, 4 })
           .SetPoint(Bolt12TlvTypes.InvreqPayerId, PayerId)
           .Set(Bolt12TlvTypes.Signature, new byte[Bolt12Constants.SignatureLength]);

    public static Bolt12TlvStreamBuilder InvoiceBuilder(Bolt12TlvStreamBuilder? request = null) =>
        new Bolt12TlvStreamBuilder((request ?? InvoiceRequestBuilder()).Build())
           .SetPaths(Bolt12TlvTypes.InvoicePaths, [Path()])
           .SetPayInfos(Bolt12TlvTypes.InvoiceBlindedPay, [PayInfo()])
           .SetTu64(Bolt12TlvTypes.InvoiceCreatedAt, 1_700_000_000)
           .Set(Bolt12TlvTypes.InvoicePaymentHash, Enumerable.Repeat((byte)0xAB, 32).ToArray())
           .SetTu64(Bolt12TlvTypes.InvoiceAmount, 10_000)
           .SetPoint(Bolt12TlvTypes.InvoiceNodeId, IssuerId)
           .Set(Bolt12TlvTypes.Signature, new byte[Bolt12Constants.SignatureLength]);

    public static DateTimeOffset InvoiceCreatedAt => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
}