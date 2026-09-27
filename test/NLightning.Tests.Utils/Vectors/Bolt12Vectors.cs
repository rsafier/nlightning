using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NLightning.Tests.Utils.Vectors;

/// <summary>
/// One case of the BOLT 12 <c>format-string-test.json</c> vector file.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed record Bolt12FormatStringVector(string Comment, bool Valid, string String)
{
    public override string ToString() => Comment;
}

/// <summary>
/// One expected field of an <c>offers-test.json</c> case.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed record Bolt12OfferVectorField(ulong Type, int Length, byte[] Value);

/// <summary>
/// One case of the BOLT 12 <c>offers-test.json</c> vector file.
/// </summary>
/// <param name="Description"><c>description</c></param>
/// <param name="Valid"><c>valid</c></param>
/// <param name="Bolt12"><c>bolt12</c>, the offer string</param>
/// <param name="Fields"><c>fields</c> (type, length, value), present for every valid case and some invalid ones</param>
[ExcludeFromCodeCoverage]
public sealed record Bolt12OfferVector(string Description, bool Valid, string Bolt12,
                                       IReadOnlyList<Bolt12OfferVectorField>? Fields)
{
    public override string ToString() => Description;
}

/// <summary>
/// One record's leaves in a <c>signature-test.json</c> case.
/// </summary>
/// <param name="Tlv">The full TLV encoding hashed by the <c>LnLeaf</c> leaf (from the key).</param>
/// <param name="Leaf"><c>H("LnLeaf", tlv)</c></param>
/// <param name="Nonce"><c>H("LnNonce" || first-tlv, type)</c></param>
/// <param name="Branch"><c>H("LnBranch", leaf + nonce)</c></param>
[ExcludeFromCodeCoverage]
public sealed record Bolt12MerkleLeafVector(byte[] Tlv, byte[] Leaf, byte[] Nonce, byte[] Branch);

/// <summary>
/// One inner node in a <c>signature-test.json</c> case.
/// </summary>
/// <param name="Description"><c>desc</c></param>
/// <param name="Input">The 64 bytes hashed (lesser || greater, from the key).</param>
/// <param name="Output">The node's hash.</param>
[ExcludeFromCodeCoverage]
public sealed record Bolt12MerkleBranchVector(string Description, byte[] Input, byte[] Output);

/// <summary>
/// One case of the BOLT 12 <c>signature-test.json</c> vector file.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed record Bolt12SignatureVector(
    string Comment,
    string TlvStreamName,
    byte[] FirstTlv,
    IReadOnlyList<Bolt12MerkleLeafVector> Leaves,
    IReadOnlyList<Bolt12MerkleBranchVector> Branches,
    byte[] Merkle,
    string? Bolt12,
    string? SignatureTag,
    byte[]? SignatureDigest,
    byte[]? Signature)
{
    /// <summary>
    /// The TLV stream the leaves were computed over (every leaf's TLV, in order; signature elements excluded).
    /// </summary>
    public byte[] GetSignedTlvStream() => Leaves.SelectMany(l => l.Tlv).ToArray();

    public override string ToString() => Comment;
}

/// <summary>
/// Loaders of the BOLT 12 JSON vectors (<c>test/NLightning.Integration.Tests/BOLT12/Vectors/</c>, copied next to the
/// test assembly).
/// </summary>
[ExcludeFromCodeCoverage]
public static class Bolt12Vectors
{
    public const string FormatStringTestPath = "BOLT12/Vectors/format-string-test.json";
    public const string OffersTestPath = "BOLT12/Vectors/offers-test.json";
    public const string SignatureTestPath = "BOLT12/Vectors/signature-test.json";

    /// <summary>
    /// The private key of <c>offer_issuer_id</c> in the signature vector's invoice_request (0x41 x 32).
    /// </summary>
    public static readonly byte[] SignatureIssuerPrivateKey = Enumerable.Repeat((byte)0x41, 32).ToArray();

    /// <summary>
    /// The private key of <c>invreq_payer_id</c> in the signature vector's invoice_request (0x42 x 32).
    /// </summary>
    public static readonly byte[] SignaturePayerPrivateKey = Enumerable.Repeat((byte)0x42, 32).ToArray();

    public static IReadOnlyList<Bolt12FormatStringVector> LoadFormatStringTest(string path = FormatStringTestPath)
    {
        using var document = Bolt4Vectors.LoadDocument(path);
        return document.RootElement.EnumerateArray()
                       .Select(e => new Bolt12FormatStringVector(GetString(e, "comment"),
                                                                 Bolt4Vectors.GetRequired(e, "valid").GetBoolean(),
                                                                 GetString(e, "string")))
                       .ToList();
    }

    public static IReadOnlyList<Bolt12OfferVector> LoadOffersTest(string path = OffersTestPath)
    {
        using var document = Bolt4Vectors.LoadDocument(path);
        var vectors = new List<Bolt12OfferVector>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            List<Bolt12OfferVectorField>? fields = null;
            if (element.TryGetProperty("fields", out var fieldsElement))
            {
                fields = fieldsElement.EnumerateArray()
                                      .Select(f => new Bolt12OfferVectorField(
                                                  Bolt4Vectors.GetRequired(f, "type").GetUInt64(),
                                                  Bolt4Vectors.GetRequired(f, "length").GetInt32(),
                                                  Bolt4Vectors.GetHex(f, "hex")))
                                      .ToList();
            }

            vectors.Add(new Bolt12OfferVector(GetString(element, "description"),
                                              Bolt4Vectors.GetRequired(element, "valid").GetBoolean(),
                                              GetString(element, "bolt12"), fields));
        }

        return vectors;
    }

    public static IReadOnlyList<Bolt12SignatureVector> LoadSignatureTest(string path = SignatureTestPath)
    {
        using var document = Bolt4Vectors.LoadDocument(path);
        var vectors = new List<Bolt12SignatureVector>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var leaves = new List<Bolt12MerkleLeafVector>();
            foreach (var leaf in Bolt4Vectors.GetRequired(element, "leaves").EnumerateArray())
            {
                var properties = leaf.EnumerateObject().ToList();
                if (properties.Count != 3)
                    throw new InvalidDataException("A signature-test leaf must have 3 entries.");

                leaves.Add(new Bolt12MerkleLeafVector(ParseKeyArgument(properties[0].Name, "H(`LnLeaf`,"),
                                                      ParseHexValue(properties[0].Value),
                                                      ParseHexValue(properties[1].Value),
                                                      ParseHexValue(properties[2].Value)));
            }

            var branches = new List<Bolt12MerkleBranchVector>();
            foreach (var branch in Bolt4Vectors.GetRequired(element, "branches").EnumerateArray())
            {
                var node = branch.EnumerateObject().Single(p => p.Name != "desc");
                branches.Add(new Bolt12MerkleBranchVector(GetString(branch, "desc"),
                                                          ParseKeyArgument(node.Name, "H(`LnBranch`,"),
                                                          ParseHexValue(node.Value)));
            }

            vectors.Add(new Bolt12SignatureVector(
                            GetString(element, "comment"), GetString(element, "tlv"),
                            Bolt4Vectors.GetHex(element, "first-tlv"), leaves, branches,
                            Bolt4Vectors.GetHex(element, "merkle"),
                            element.TryGetProperty("bolt12", out var bolt12) ? bolt12.GetString() : null,
                            element.TryGetProperty("signature_tag", out var tag) ? tag.GetString() : null,
                            Bolt4Vectors.GetOptionalHex(element, "H(signature_tag,merkle)"),
                            Bolt4Vectors.GetOptionalHex(element, "signature")));
        }

        return vectors;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        Bolt4Vectors.GetRequired(element, propertyName).GetString()
     ?? throw new InvalidDataException($"BOLT 12 vector property '{propertyName}' is null");

    private static byte[] ParseKeyArgument(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith(')'))
            throw new InvalidDataException($"Unexpected signature-test key '{key}'.");

        return Convert.FromHexString(key[prefix.Length..^1]);
    }

    private static byte[] ParseHexValue(JsonElement value) =>
        Convert.FromHexString(value.GetString() ?? throw new InvalidDataException("A hash value is null."));
}