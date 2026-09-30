using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT12;

using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Signing;
using Domain.Offers.Validators;

/// <summary>
/// BOLT 12 plan B0 proofs: <c>format-string-test.json</c>, <c>offers-test.json</c> and the Merkle part of
/// <c>signature-test.json</c>, byte-exact.
/// </summary>
/// <remarks>
/// The CLN-captured offer, invoice_request, invoice and invoice_error (B0-T4, NL-450) are in
/// <see cref="Bolt12ClnVectors"/> and checked by <see cref="Bolt12ClnVectorTests"/>.
/// </remarks>
public class Bolt12VectorTests
{
    private static readonly IReadOnlyList<Bolt12FormatStringVector> s_formatStrings =
        Bolt12Vectors.LoadFormatStringTest();

    private static readonly IReadOnlyList<Bolt12OfferVector> s_offers = Bolt12Vectors.LoadOffersTest();

    private static readonly IReadOnlyList<Bolt12SignatureVector> s_signatures = Bolt12Vectors.LoadSignatureTest();

    /// <summary>
    /// The requirement each invalid <c>offers-test.json</c> case breaks first.
    /// </summary>
    /// <remarks>
    /// Upstream quirk (commit 1aadb719): the six "blinded_path" malformed cases encode <c>offer_paths</c> with a length
    /// of 2 or 3, so their stream already breaks BOLT 1 ordering before any path is read, and "type &gt; 1999999999" and
    /// "unknown even type (1000000002)" encode their type as a 3-byte <c>fd</c> BigSize (30517, 15258) whose length
    /// runs past the end. They are rejected as malformed streams (B12-ENC-03) as committed; the rules they name are
    /// asserted on well-formed streams by <see cref="Given_AnUpstreamBrokenCase_When_ItsNamedRuleRunsOnAWellFormedStream_Then_ThatRuleRejectsIt"/>
    /// and covered in <c>NLightning.Domain.Tests.Offers</c>.
    /// </remarks>
    private static readonly Dictionary<string, string> s_expectedOfferViolations = new()
    {
        ["Malformed: fields out of order"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: unknown even TLV type 78"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: empty"] = Bolt12RequirementIds.OfferReader,
        ["Malformed: truncated at type"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated in length"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated after length"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated in description"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: invalid offer_chains length"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated currency UTF-8"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: invalid currency UTF-8"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated description UTF-8"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: invalid description UTF-8"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated offer_paths"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: zero num_hops in blinded_path"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated onionmsg_hop in blinded_path"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: bad first_node_id in blinded_path"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: bad path_key in blinded_path"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: bad blinded_node_id in onionmsg_hop"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: truncated issuer UTF-8"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: invalid issuer UTF-8"] = Bolt12RequirementIds.TlvStream,
        ["Malformed: invalid offer_issuer_id"] = Bolt12RequirementIds.TlvStream,
        ["Contains type >= 80"] = Bolt12RequirementIds.TlvRange,
        ["Contains type > 1999999999"] = Bolt12RequirementIds.TlvStream,
        ["Contains unknown even type (1000000002)"] = Bolt12RequirementIds.TlvStream,
        ["Contains unknown feature 122"] = Bolt12RequirementIds.OfferReader,
        ["Missing offer_description, but has offer_amount"] = Bolt12RequirementIds.OfferReader,
        ["Missing offer_amount with offer_currency"] = Bolt12RequirementIds.OfferReader,
        ["Invalid: zero offer_amount"] = Bolt12RequirementIds.OfferReader,
        ["Invalid: zero offer_amount with currency"] = Bolt12RequirementIds.OfferReader,
        ["Missing offer_issuer_id and no offer_path"] = Bolt12RequirementIds.OfferReader,
        ["Second offer_path is empty"] = Bolt12RequirementIds.OfferReader,
        ["offer_chains with zero entries"] = Bolt12RequirementIds.OfferReader,
        ["Bech32 padding exceeds 4-bit limit"] = Bolt12RequirementIds.Encoding
    };

    public static TheoryData<int, string> FormatStringCases() => Cases(s_formatStrings, v => v.Comment);

    public static TheoryData<int, string> ValidOfferCases() =>
        Cases(s_offers, v => v.Description, v => v.Valid);

    public static TheoryData<int, string> InvalidOfferCases() =>
        Cases(s_offers, v => v.Description, v => !v.Valid);

    /// <summary>
    /// The eight invalid cases whose committed bytes are rejected at the TLV stream before the rule they name
    /// (see the remarks of <see cref="s_expectedOfferViolations"/>).
    /// </summary>
    private static readonly string[] s_upstreamBrokenOfferCases =
    [
        "Malformed: truncated offer_paths",
        "Malformed: zero num_hops in blinded_path",
        "Malformed: truncated onionmsg_hop in blinded_path",
        "Malformed: bad first_node_id in blinded_path",
        "Malformed: bad path_key in blinded_path",
        "Malformed: bad blinded_node_id in onionmsg_hop",
        "Contains type > 1999999999",
        "Contains unknown even type (1000000002)"
    ];

    public static TheoryData<int, string> UpstreamBrokenOfferCases()
    {
        var data = new TheoryData<int, string>();
        foreach (var description in s_upstreamBrokenOfferCases)
            data.Add(s_offers.Select((v, i) => (v, i)).First(p => p.v.Description == description).i, description);

        return data;
    }

    public static TheoryData<int, string> SignatureCases() => Cases(s_signatures, v => v.Comment);

    [Fact]
    public void Given_VectorFiles_When_Loading_Then_TheyHoldEveryCase()
    {
        // Assert
        Assert.Equal(12, s_formatStrings.Count);
        Assert.Equal(53, s_offers.Count);
        Assert.Equal(20, s_offers.Count(v => v.Valid));
        Assert.Equal(4, s_signatures.Count);
        Assert.Equal(s_offers.Count(v => !v.Valid), s_expectedOfferViolations.Count);
    }

    [Theory]
    [MemberData(nameof(FormatStringCases))]
    public void Given_FormatString_When_Parsing_Then_ValidOnesDecodeToTheCanonicalBytes(int index, string comment)
    {
        // Arrange
        var vector = s_formatStrings[index];
        var canonical = Bolt12Bech32.Decode(s_formatStrings[0].String).Data;

        // Act
        var parsed = Offer.TryParse(vector.String, out var offer, out var violation);

        // Assert
        Assert.Equal(vector.Valid, parsed);
        if (vector.Valid)
        {
            Assert.Equal(canonical, offer!.Stream.Encode());
            Assert.Null(OfferValidator.Validate(offer));
        }
        else
        {
            Assert.Equal(Bolt12RequirementIds.Continuation, violation!.RequirementId);
        }

        Assert.NotNull(comment);
    }

    [Fact]
    public void Given_FormatStringOffer_When_EncodingUppercase_Then_ItEqualsTheUppercaseVector()
    {
        // Arrange
        var offer = Offer.Parse(s_formatStrings[0].String);

        // Act
        var lower = offer.ToBolt12String();
        var upper = offer.ToBolt12String(uppercase: true);

        // Assert
        Assert.Equal(s_formatStrings[0].String, lower);
        Assert.Equal(s_formatStrings[1].String, upper);
        Assert.Equal(offer.Stream.Encode(), Offer.Parse(upper).Stream.Encode());
    }

    [Theory]
    [MemberData(nameof(ValidOfferCases))]
    public void Given_ValidOffer_When_Parsing_Then_FieldsMatchAndItReEncodesByteExact(int index, string description)
    {
        // Arrange
        var vector = s_offers[index];

        // Act
        var violation = OfferValidator.Validate(vector.Bolt12, out var offer);

        // Assert
        Assert.Null(violation);
        Assert.NotNull(offer);
        var expected = vector.Fields!;
        Assert.Equal(expected.Count, offer.Stream.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Type, offer.Stream.Records[i].Type);
            Assert.Equal(expected[i].Length, offer.Stream.Records[i].Value.Length);
            Assert.Equal(expected[i].Value, offer.Stream.Records[i].Value.ToArray());
        }

        Assert.Equal(vector.Bolt12, offer.ToBolt12String());
        Assert.Equal(vector.Bolt12, Offer.Parse(new Bolt12TlvStreamBuilder(offer.Stream).Build()).ToBolt12String());
        Assert.NotNull(description);
    }

    [Theory]
    [MemberData(nameof(InvalidOfferCases))]
    public void Given_InvalidOffer_When_Validating_Then_ItIsRejectedWithItsRequirement(int index, string description)
    {
        // Arrange
        var vector = s_offers[index];

        // Act
        var violation = OfferValidator.Validate(vector.Bolt12, out var offer);

        // Assert
        Assert.Null(offer);
        Assert.NotNull(violation);
        Assert.Equal(s_expectedOfferViolations[description], violation.RequirementId);
    }

    [Theory]
    [MemberData(nameof(InvalidOfferCases))]
    public void Given_InvalidOfferWithFields_When_Parsing_Then_TheFieldsMatch(int index, string description)
    {
        // Arrange
        var vector = s_offers[index];
        if (vector.Fields is null)
            return;

        // Act
        var offer = Offer.Parse(vector.Bolt12);

        // Assert
        Assert.Equal(vector.Fields.Select(f => (f.Type, f.Value)),
                     offer.Stream.Records.Select(r => (r.Type, r.Value.ToArray())));
        Assert.NotNull(description);
    }

    [Theory]
    [MemberData(nameof(UpstreamBrokenOfferCases))]
    public void Given_AnUpstreamBrokenCase_When_ItsNamedRuleRunsOnAWellFormedStream_Then_ThatRuleRejectsIt(
        int index, string description)
    {
        // Arrange: the stream the case means, with its broken record length or type encoding repaired (the committed
        // bytes cannot carry it: they die in the TLV stream parse first). Each rule is asserted at the layer that
        // owns it: the blinded_path codec behind the offer field, and the offer's TLV type ranges.
        var onCurve = Enumerable.Repeat((byte)0x02, 33).ToArray();
        var offCurve = Enumerable.Repeat((byte)0x03, 33).ToArray();
        var paths = description switch
        {
            "Malformed: truncated offer_paths"
                => onCurve.Concat(onCurve).Append((byte)1).Concat(onCurve).ToArray(),
            "Malformed: zero num_hops in blinded_path"
                => onCurve.Concat(onCurve).Append((byte)0).ToArray(),
            "Malformed: truncated onionmsg_hop in blinded_path"
                => onCurve.Concat(onCurve).Append((byte)1).Concat(onCurve).Append((byte)0).Append((byte)5).ToArray(),
            "Malformed: bad first_node_id in blinded_path"
                => offCurve.Concat(onCurve).Append((byte)1).Concat(onCurve).Append((byte)0).Append((byte)0)
                          .ToArray(),
            "Malformed: bad path_key in blinded_path"
                => onCurve.Concat(offCurve).Append((byte)1).Concat(onCurve).Append((byte)0).Append((byte)0)
                          .ToArray(),
            "Malformed: bad blinded_node_id in onionmsg_hop"
                => onCurve.Concat(onCurve).Append((byte)1).Concat(offCurve).Append((byte)0).Append((byte)0)
                          .ToArray(),
            _ => null
        };
        var type = paths is null
                       ? (description.StartsWith("Contains type") ? 2_000_000_000UL : 1_000_000_002UL)
                       : 0;
        var requirementId = description switch
        {
            "Malformed: zero num_hops in blinded_path" => Bolt12RequirementIds.OfferReader,
            "Contains type > 1999999999" => Bolt12RequirementIds.TlvRange,
            _ => Bolt12RequirementIds.TlvStream
        };
        var reasonPart = description switch
        {
            "Malformed: truncated offer_paths" => "truncated before enclen",
            "Malformed: zero num_hops in blinded_path" => "num_hops 0",
            "Malformed: truncated onionmsg_hop in blinded_path" => "enclen 5 runs past the end",
            "Malformed: bad first_node_id in blinded_path" => "first_node_id is not a point on the curve",
            "Malformed: bad path_key in blinded_path" => "first_path_key is not a point on the curve",
            "Malformed: bad blinded_node_id in onionmsg_hop" => "blinded_node_id is not a point on the curve",
            _ => null
        };
        Assert.False(s_offers[index].Valid);
        var builder = new Bolt12TlvStreamBuilder().SetUtf8(Bolt12TlvTypes.OfferDescription, "ALICE");
        if (paths is not null)
            builder.Set(Bolt12TlvTypes.OfferPaths, paths);
        else
            builder.Set(type, [(byte)1]);

        // Act
        var ok = Offer.TryParse(builder.Build(), out _, out var violation);

        // Assert: the rule the case is about is the one that rejects the stream
        Assert.False(ok);
        Assert.Equal(requirementId, violation!.RequirementId);
        if (paths is not null)
            Assert.Contains(reasonPart, violation.Reason);
        else
            Assert.Equal(type, violation.Field);
    }

    [Theory]
    [MemberData(nameof(SignatureCases))]
    public void Given_SignatureVector_When_ComputingTheMerkleTree_Then_LeavesBranchesAndRootMatch(
        int index, string comment)
    {
        // Arrange
        var vector = s_signatures[index];
        var stream = vector.Bolt12 is { } bolt12
                         ? InvoiceRequest.Parse(bolt12).Stream
                         : Bolt12TlvStream.Parse(vector.GetSignedTlvStream());

        // Act
        var leaves = Bolt12MerkleTree.ComputeLeaves(stream);
        var root = Bolt12MerkleTree.ComputeRoot(stream);

        // Assert
        Assert.Equal(vector.FirstTlv, Bolt12TlvStream.EncodeRecord(stream.Records[0]));
        Assert.Equal(vector.Leaves.Count, leaves.Count);
        for (var i = 0; i < leaves.Count; i++)
        {
            Assert.Equal(vector.Leaves[i].Tlv, Bolt12TlvStream.EncodeRecord(stream.Records[i]));
            Assert.Equal(vector.Leaves[i].Leaf, (byte[])leaves[i].Leaf);
            Assert.Equal(vector.Leaves[i].Nonce, (byte[])leaves[i].Nonce);
            Assert.Equal(vector.Leaves[i].Branch, (byte[])leaves[i].Branch);
        }

        foreach (var branch in vector.Branches)
        {
            Assert.True(branch.Input.AsSpan(0, 32).SequenceCompareTo(branch.Input.AsSpan(32)) < 0);
            Assert.Equal(branch.Output,
                         Bolt12MerkleTree.TaggedHash("LnBranch"u8, branch.Input));
        }

        Assert.Equal(vector.Merkle, (byte[])root);
        Assert.NotNull(comment);
    }

    [Fact]
    public void Given_SignatureVectorInvoiceRequest_When_Parsing_Then_ItIsValidAndItsDigestMatches()
    {
        // Arrange
        var vector = s_signatures.Single(v => v.Bolt12 is not null);

        // Act
        var invoiceRequest = InvoiceRequest.Parse(vector.Bolt12!);
        var root = Bolt12MerkleTree.ComputeRoot(invoiceRequest.Stream);
        var digest = Bolt12MerkleTree.GetSignatureDigest(vector.SignatureTag!, root);

        // Assert
        // A USD offer: valid with a conversion, rejected (fail closed) without one
        Assert.Null(InvoiceRequestValidator.Validate(invoiceRequest, convertToMsat: (_, cents) => cents));
        Assert.Equal(Bolt12RequirementIds.InvoiceRequestAmounts,
                     InvoiceRequestValidator.Validate(invoiceRequest)!.RequirementId);
        Assert.Equal(Bolt12Constants.InvoiceRequestSignatureTag, vector.SignatureTag);
        Assert.Equal(vector.SignatureDigest, (byte[])digest);
        Assert.Equal(vector.Signature, invoiceRequest.Signature!.Value.ToArray());
        Assert.Equal("A Mathematical Treatise", invoiceRequest.OfferFields.Description);
        Assert.Equal("USD", invoiceRequest.OfferFields.Currency);
        Assert.Equal(100UL, invoiceRequest.OfferFields.Amount);
        Assert.Equal(new byte[8], invoiceRequest.Fields.Metadata!.Value.ToArray());
        Assert.Equal(vector.Bolt12, invoiceRequest.ToBolt12String());
    }

    private static TheoryData<int, string> Cases<T>(IReadOnlyList<T> vectors, Func<T, string> name,
                                                   Func<T, bool>? filter = null)
    {
        var data = new TheoryData<int, string>();
        for (var i = 0; i < vectors.Count; i++)
            if (filter is null || filter(vectors[i]))
                data.Add(i, name(vectors[i]));

        return data;
    }
}