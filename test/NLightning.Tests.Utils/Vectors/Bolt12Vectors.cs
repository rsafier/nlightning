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

/// <summary>
/// BOLT 12 messages exchanged with Core Lightning v26.06.8 on regtest (NL-450, BOLT 12 plan B0-T4), captured by the
/// <c>Explicit</c> <c>Integration.Tests/Docker/Interop/Cln/ClnBolt12CaptureTests</c> on 2026-09-27: hex TLV streams (the
/// onion message payload field, or the bech32 data of an offer or invoice string). Checked by
/// <c>Integration.Tests/BOLT12/Bolt12ClnVectorTests</c>: parse, byte-exact re-encode, validate and the BIP-340
/// signatures.
/// </summary>
/// <remarks>
/// Re-capture from the host with <c>NLightning.Integration.Tests -class
/// NLightning.Integration.Tests.Docker.Interop.Cln.ClnBolt12CaptureTests -explicit only</c> and copy its <c>VECTOR</c>
/// lines. The invoices expire 7,200 s after their <c>invoice_created_at</c>: validate them at that time, never at the
/// wall clock.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class Bolt12ClnVectors
{
    /// <summary>
    /// CLN's node id: <c>offer_issuer_id</c> of its offer and <c>invoice_node_id</c> of its invoice.
    /// </summary>
    public const string ClnNodeId =
        "0278e33f41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073";

    /// <summary>
    /// Our node id: <c>offer_issuer_id</c> of our offer and <c>invoice_node_id</c> of our invoice.
    /// </summary>
    public const string NltgNodeId =
        "037e52461145672cab44c4b77ad1d20167b76a4f3815146c59717da782a16d2bbb";

    /// <summary>
    /// CLN's offer (<c>offer amount=10000sat description="nltg nl-450 capture" issuer=cln</c>).
    /// </summary>
    public const string ClnOffer =
        "022006226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1fc7b2b73cf188910f08039896800a136e6c7467206e6c2d34" +
        "353020636170747572651203636c6e16210278e33f41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073";

    /// <summary>
    /// Our invoice_request to <see cref="ClnOffer"/> (payer note <c>nl-450</c>), as we sent it.
    /// </summary>
    public const string NltgInvoiceRequest =
        "002088773cd4d378d15f3af65a04e4b3911a5d5a952d149a9093fe29bb8350dd6a93022006226e46111a0b59caaf126043eb" +
        "5bbf28c34f3a5e332a1fc7b2b73cf188910f08039896800a136e6c7467206e6c2d34353020636170747572651203636c6e16" +
        "210278e33f41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073502006226e46111a0b59caaf126043eb" +
        "5bbf28c34f3a5e332a1fc7b2b73cf188910f582103794757c0c53a0eef40dedf7165c7fe876943b3b67bac8c33c236d75e12" +
        "cf398159066e6c2d343530f04090b065bce29fb37c16479d5f6700f64d8b7352603faeae84a91d8cae412b7cd92f96dcffbb" +
        "d6aa0c04238f10a899a8a50081c77964eceeb7327aa7ed88d5744b";

    /// <summary>
    /// CLN's invoice for <see cref="NltgInvoiceRequest"/>, as we received it (onion message field 66).
    /// </summary>
    public const string ClnInvoice =
        "002088773cd4d378d15f3af65a04e4b3911a5d5a952d149a9093fe29bb8350dd6a93022006226e46111a0b59caaf126043eb" +
        "5bbf28c34f3a5e332a1fc7b2b73cf188910f08039896800a136e6c7467206e6c2d34353020636170747572651203636c6e16" +
        "210278e33f41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073502006226e46111a0b59caaf126043eb" +
        "5bbf28c34f3a5e332a1fc7b2b73cf188910f582103794757c0c53a0eef40dedf7165c7fe876943b3b67bac8c33c236d75e12" +
        "cf398159066e6c2d343530a0980278e33f41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073032853df" +
        "b7ca57acbd641d4e11db780e175939955ebf8a617650a3ae1c8ea5949801031ae163b15be4c4c45c512d2a2162371d61fcfe" +
        "9e5cce150b24b07d878baa82c30032972eeed2448fb46ae46edba37bc1b8770f64ae212c591732e363a02678b1821460fc58" +
        "bbe0d0ae85daeb3d2e9c9c9e8911aba21c0000000000000000000a00000000000000001d24b2dfac5200000000a4046ab932" +
        "31a820d42773ba87bfb0f48187b4626d7fe796bf4aec27a953df1c6250a4df0006a55caa03989680ae03020000b0210278e3" +
        "3f41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073f040740c06c78f0c2810b87ad6cb3eb485050a5a" +
        "d79a76fb08fd6923ab777003801ae0c83583678314cba23d7801c71dec4ecd8e5f5e117073bda420ad51a27c8eb4";

    /// <summary>
    /// CLN's invoice_error for our invoice_request to <see cref="ClnOffer"/> after <c>disableoffer</c> (field 68).
    /// </summary>
    public const string ClnInvoiceError =
        "05fd01dc4661696c656420696e76726571206c6e723171717371737271656132376b6c64756e7470717a686430656430726a" +
        "676679776a756c76647032367372776538786872647861666332737a7971727a796d6a787a7964716b6b7732347566787173" +
        "6c7474776c6a33733630386630727832736c6337657477303833337a6773377a71726e7a7467717a736e64656b3867656571" +
        "64656b7a366470347871737878637473773336687965676a7164336b636d736b797970383363656c67787665356d65347133" +
        "33366e787334767a336b6335716b6432767368767a716573727535346478786a3936717536737971727a796d6a787a796471" +
        "6b6b77323475667871736c7474776c6a33733630386630727832736c6337657477303833337a6773376b7070716730783964" +
        "347265727033636664676b3772306a367579796b65767338746767333263796d756176797a6a706573396a6d647a39757a71" +
        "68776b336b786e74373334337a616c7133616a713079703075326130616378706d71666b7475783373726e79346137753435" +
        "646d7779386b6c6c396865617a72393473396e64367539686c78777664396465643733787471706b356c7a75666674677670" +
        "7136733a204f66666572206e6f206c6f6e67657220617661696c61626c65";

    /// <summary>
    /// Our offer (5,000 sat, <c>nltg nl-450 offer</c>, an <c>offer_path</c> introduced by CLN).
    /// </summary>
    public const string NltgOffer =
        "022006226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1fc7b2b73cf188910f041071ea17f53618cdf43b7a10097dee" +
        "529d08034c4b400a116e6c7467206e6c2d343530206f6666657210f30278e33f41999a6f350463a99a1560a36c50166a990b" +
        "b040cc07ca55a6348ba073035c73c146c63bde10b5a90c8a04b415cca36645b4f7547a5ada6723a5908a2bd9020279188189" +
        "fa3f67d6c8f66132000dcdf65b6dc9a1ce07d7f7303c26ffa9caa995003513c6e20ee0f3ddd35fc428666611cf5cf724ef5c" +
        "0a33ca7f78b7d3acb35445329686a7641f008908c42353d6c3448bc876a3ade3de022c5cd233ad2aee8f585df3ba92a9db60" +
        "ae770a99681022f3ed7fb8272f92db7700359a0aef3311678bef6b742dea3d2faa8182471c728d6ecbf5073d1015e5a69782" +
        "a56185e718a5e5cf5ee8ee040880361abcc7df510f1621037e52461145672cab44c4b77ad1d20167b76a4f3815146c59717d" +
        "a782a16d2bbb";

    /// <summary>
    /// CLN's invoice_request to <see cref="NltgOffer"/> (<c>fetchinvoice</c>, payer note <c>cln nl-450</c>), as our handler received it (field 64).
    /// </summary>
    public const string ClnInvoiceRequest =
        "0010bf135d89c92f29bfb99757bceed456e0022006226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1fc7b2b73cf188" +
        "910f041071ea17f53618cdf43b7a10097dee529d08034c4b400a116e6c7467206e6c2d343530206f6666657210f30278e33f" +
        "41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073035c73c146c63bde10b5a90c8a04b415cca36645b4" +
        "f7547a5ada6723a5908a2bd9020279188189fa3f67d6c8f66132000dcdf65b6dc9a1ce07d7f7303c26ffa9caa995003513c6" +
        "e20ee0f3ddd35fc428666611cf5cf724ef5c0a33ca7f78b7d3acb35445329686a7641f008908c42353d6c3448bc876a3ade3" +
        "de022c5cd233ad2aee8f585df3ba92a9db60ae770a99681022f3ed7fb8272f92db7700359a0aef3311678bef6b742dea3d2f" +
        "aa8182471c728d6ecbf5073d1015e5a69782a56185e718a5e5cf5ee8ee040880361abcc7df510f1621037e52461145672cab" +
        "44c4b77ad1d20167b76a4f3815146c59717da782a16d2bbb502006226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1f" +
        "c7b2b73cf188910f54005821021ddcce55d626c9b9c1e0afc2dc473cfc48ffa9f58e890945c023487952d92449590a636c6e" +
        "206e6c2d343530f0402020947b9729e80d92d6944ad600a971189e51ff3d8113d701d4b81db79f3a2a9eb346dcdcbe0a3767" +
        "e83254f5f723d4a7f416eed118e637b66edb46b6966245";

    /// <summary>
    /// Our invoice for <see cref="ClnInvoiceRequest"/>, as CLN's <c>fetchinvoice</c> returned it.
    /// </summary>
    public const string NltgInvoice =
        "0010bf135d89c92f29bfb99757bceed456e0022006226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1fc7b2b73cf188" +
        "910f041071ea17f53618cdf43b7a10097dee529d08034c4b400a116e6c7467206e6c2d343530206f6666657210f30278e33f" +
        "41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba073035c73c146c63bde10b5a90c8a04b415cca36645b4" +
        "f7547a5ada6723a5908a2bd9020279188189fa3f67d6c8f66132000dcdf65b6dc9a1ce07d7f7303c26ffa9caa995003513c6" +
        "e20ee0f3ddd35fc428666611cf5cf724ef5c0a33ca7f78b7d3acb35445329686a7641f008908c42353d6c3448bc876a3ade3" +
        "de022c5cd233ad2aee8f585df3ba92a9db60ae770a99681022f3ed7fb8272f92db7700359a0aef3311678bef6b742dea3d2f" +
        "aa8182471c728d6ecbf5073d1015e5a69782a56185e718a5e5cf5ee8ee040880361abcc7df510f1621037e52461145672cab" +
        "44c4b77ad1d20167b76a4f3815146c59717da782a16d2bbb502006226e46111a0b59caaf126043eb5bbf28c34f3a5e332a1f" +
        "c7b2b73cf188910f54005821021ddcce55d626c9b9c1e0afc2dc473cfc48ffa9f58e890945c023487952d92449590a636c6e" +
        "206e6c2d343530a0fd00ff0278e33f41999a6f350463a99a1560a36c50166a990bb040cc07ca55a6348ba07303d6c32e792c" +
        "d9009ba0eacae8f8e6785eafdbbe0f21a52ec07f448f62171cd3530203a7e75a6fe4f0313ee987a4c4d09350241ed0be0604" +
        "821a8c410b53cf439de105003b9b0c23e0350161ab864e3018932ae5e8ff4234ef4e180a629988d902fc47f84b41ec1a4d48" +
        "c5cae915a9d96979bf8fcd35fe1b70563ff1e34fc00603a69c83f835d630de86afed7cb8acf892aad30086c64b9470771aa1" +
        "605ba4c0e1003bf079652df5c7732a34444883f06e74830abbdee4ebf3c4f4b2e02c80827cff25da7e0424b23ddd652c74cc" +
        "4ee1e43e66534f11f15997b3ac00d6b5a21c000000010000000a002e00000000000003e8000000002faf08000000a4046ab9" +
        "3231a8205235fc16c37554a3b3688eff78a7c916bfe3f0c9667d8e2f992474d555b532fcaa034c4b40ae03020000b021037e" +
        "52461145672cab44c4b77ad1d20167b76a4f3815146c59717da782a16d2bbbf0404577e6d53ddd6491d35e5d0a2a46a1864e" +
        "a5374d8f5369e6992e7d6c479d3343b135687309e92bc1830c2db172752e3f963084498dee914ade0b5020d9292f04";
}