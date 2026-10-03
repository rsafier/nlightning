using System.Security.Cryptography;

namespace NLightning.Domain.Tests.Offers.Signing;

using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Signing;

public class Bolt12MerkleTreeTests
{
    // signature-test.json "Simple n1 test, tlv1 = 1000"
    private const string SingleRecordStream = "010203e8";
    private const string SingleRecordRoot = "b013756c8fee86503a0b4abdab4cddeb1af5d344ca6fc2fa8b6c08938caa6f93";

    [Fact]
    public void Given_TaggedHash_When_Computed_Then_ItIsSha256OfBothTagHashesAndTheMessage()
    {
        // Arrange
        var tag = "LnLeaf"u8.ToArray();
        var message = new byte[] { 1, 2, 3 };
        var tagHash = SHA256.HashData(tag);

        // Act
        var hash = Bolt12MerkleTree.TaggedHash(tag, message);

        // Assert
        Assert.Equal(SHA256.HashData([.. tagHash, .. tagHash, .. message]), hash);
    }

    [Fact]
    public void Given_SingleRecord_When_ComputingTheRoot_Then_ItIsTheLeafBranch()
    {
        // Arrange
        var stream = Bolt12TlvStream.Parse(Convert.FromHexString(SingleRecordStream));

        // Act
        var root = Bolt12MerkleTree.ComputeRoot(stream);
        var leaf = Assert.Single(Bolt12MerkleTree.ComputeLeaves(stream));

        // Assert
        Assert.Equal(SingleRecordRoot, root.ToString());
        Assert.Equal(root, leaf.Branch);
        Assert.Equal(1UL, leaf.Type);
    }

    [Theory]
    [InlineData(240UL)]
    [InlineData(241UL)]
    [InlineData(1000UL)]
    public void Given_SignatureElement_When_ComputingTheRoot_Then_ItIsLeftOut(ulong type)
    {
        // Arrange
        var stream = new Bolt12TlvStreamBuilder(Bolt12TlvStream.Parse(Convert.FromHexString(SingleRecordStream)))
                    .Set(type, new byte[64])
                    .Build();

        // Act
        var root = Bolt12MerkleTree.ComputeRoot(stream);

        // Assert
        Assert.Equal(SingleRecordRoot, root.ToString());
    }

    [Theory]
    [InlineData(239UL)]
    [InlineData(1001UL)]
    [InlineData(1_000_000_001UL)]
    public void Given_RecordOutsideTheSignatureRange_When_ComputingTheRoot_Then_ItIsIncluded(ulong type)
    {
        // Arrange
        var stream = new Bolt12TlvStreamBuilder(Bolt12TlvStream.Parse(Convert.FromHexString(SingleRecordStream)))
                    .Set(type, new byte[1])
                    .Build();

        // Act
        var root = Bolt12MerkleTree.ComputeRoot(stream);

        // Assert
        Assert.NotEqual(SingleRecordRoot, root.ToString());
        Assert.Equal(2, Bolt12MerkleTree.ComputeLeaves(stream).Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Given_LeafCount_When_ComputingTheRoot_Then_TheTreeIsDeepestOnTheLowestLeaves(int count)
    {
        // Arrange
        var builder = new Bolt12TlvStreamBuilder();
        for (var i = 0; i < count; i++)
            builder.Set((ulong)(2 * i + 1), [(byte)i]);
        var stream = builder.Build();
        var branches = Bolt12MerkleTree.ComputeLeaves(stream).Select(l => (byte[])l.Branch).ToList();

        // Act
        var root = Bolt12MerkleTree.ComputeRoot(stream);

        // Assert
        Assert.Equal(ReferenceRoot(branches), (byte[])root);
    }

    [Fact]
    public void Given_StreamWithOnlySignatures_When_ComputingTheRoot_Then_ItThrows()
    {
        // Arrange
        var stream = new Bolt12TlvStreamBuilder().Set(Bolt12TlvTypes.Signature, new byte[64]).Build();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Bolt12MerkleTree.ComputeRoot(stream));
        Assert.Throws<ArgumentException>(() => Bolt12MerkleTree.ComputeRoot(new Bolt12TlvStream([])));
    }

    [Fact]
    public void Given_Root_When_ComputingTheSignatureDigest_Then_ItIsTheTaggedHashOfTheRoot()
    {
        // Arrange
        var root = Bolt12MerkleTree.ComputeRoot(Bolt12TlvStream.Parse(Convert.FromHexString(SingleRecordStream)));

        // Act
        var digest = Bolt12MerkleTree.GetSignatureDigest(Bolt12Constants.InvoiceSignatureTag, root);

        // Assert
        Assert.Equal(Bolt12MerkleTree.TaggedHash(System.Text.Encoding.ASCII.GetBytes("lightninginvoicesignature"), root),
                     (byte[])digest);
    }

    /// <summary>
    /// The spec's recursive description: split the leaves at the largest power of two below the count, so the left
    /// (lower-order) subtree is the deeper one.
    /// </summary>
    private static byte[] ReferenceRoot(IReadOnlyList<byte[]> nodes)
    {
        if (nodes.Count == 1)
            return nodes[0];

        var split = 1;
        while (split * 2 < nodes.Count)
            split *= 2;

        var left = ReferenceRoot(nodes.Take(split).ToList());
        var right = ReferenceRoot(nodes.Skip(split).ToList());
        var (lesser, greater) = left.AsSpan().SequenceCompareTo(right) <= 0 ? (left, right) : (right, left);
        return Bolt12MerkleTree.TaggedHash("LnBranch"u8, [.. lesser, .. greater]);
    }
}