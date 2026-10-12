using System.Security.Cryptography;

namespace NLightning.Domain.Offers.Signing;

using Constants;
using Crypto.ValueObjects;
using Encoding;
using Protocol.Tlv;

/// <summary>
/// The BOLT 12 Merkle tree ("Signature Calculation") over a TLV stream's non-signature records.
/// </summary>
/// <remarks>
/// <para>Leaves, in ascending TLV order, leaving out the signature elements 240-1000 inclusive: for each record,
/// <c>H("LnLeaf", tlv)</c> paired with its nonce leaf <c>H("LnNonce" || first_tlv, type)</c>, where <c>tlv</c> is
/// the record's full encoding, <c>first_tlv</c> the full encoding of the numerically first record, and <c>type</c>
/// the record's BigSize type; each pair is joined as <c>H("LnBranch", lesser || greater)</c>. Inner nodes are also
/// <c>H("LnBranch", lesser || greater)</c> (ordered by value), and with a leaf count that is not a power of two the
/// tree is deepest on the lowest-order leaves (pairing at distance 1, 2, 4, ...). <c>H(tag, msg) =
/// SHA256(SHA256(tag) || SHA256(tag) || msg)</c>.</para>
/// <para>Byte-exact against <c>bolt12/signature-test.json</c>; the BIP-340 signature over
/// <see cref="GetSignatureDigest"/> is <see cref="Interfaces.IBolt12Signer"/>'s.</para>
/// </remarks>
public static class Bolt12MerkleTree
{
    private static readonly byte[] s_leafTag = System.Text.Encoding.ASCII.GetBytes(Bolt12Constants.MerkleLeafTag);
    private static readonly byte[] s_nonceTag = System.Text.Encoding.ASCII.GetBytes(Bolt12Constants.MerkleNonceTag);
    private static readonly byte[] s_branchTag = System.Text.Encoding.ASCII.GetBytes(Bolt12Constants.MerkleBranchTag);

    /// <summary>
    /// The Merkle root of <paramref name="stream"/>'s non-signature records.
    /// </summary>
    /// <exception cref="ArgumentException">The stream has no record outside the signature range.</exception>
    public static Hash ComputeRoot(Bolt12TlvStream stream)
    {
        var level = ComputeLeafBranches(stream).ToArray();
        for (var step = 1; step < level.Length; step *= 2)
            for (var i = 0; i + step < level.Length; i += 2 * step)
                level[i] = Branch(level[i], level[i + step]);

        return new Hash(level[0]);
    }

    /// <summary>
    /// The per-record leaf data of <paramref name="stream"/>, in order: the leaf, its nonce leaf, and their branch.
    /// </summary>
    /// <exception cref="ArgumentException">The stream has no record outside the signature range.</exception>
    public static IReadOnlyList<Bolt12MerkleLeaf> ComputeLeaves(Bolt12TlvStream stream)
    {
        var records = GetSignedRecords(stream);
        var nonceTag = Concat(s_nonceTag, Bolt12TlvStream.EncodeRecord(records[0]));

        var leaves = new List<Bolt12MerkleLeaf>(records.Count);
        Span<byte> type = stackalloc byte[9];
        foreach (var record in records)
        {
            var leaf = TaggedHash(s_leafTag, Bolt12TlvStream.EncodeRecord(record));
            var typeLength = BigSizeCodec.Write(record.Type, type);
            var nonce = TaggedHash(nonceTag, type[..typeLength]);
            leaves.Add(new Bolt12MerkleLeaf(record.Type, new Hash(leaf), new Hash(nonce),
                                            new Hash(Branch(leaf, nonce))));
        }

        return leaves;
    }

    /// <summary>
    /// <c>H(tag, merkleRoot)</c>: the 32-byte message a BIP-340 signature signs (BOLT 12 "Signature Calculation").
    /// </summary>
    /// <param name="tag">The signature tag, e.g. <see cref="Bolt12Constants.InvoiceSignatureTag"/>.</param>
    /// <param name="merkleRoot">The Merkle root.</param>
    public static Hash GetSignatureDigest(string tag, Hash merkleRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        return new Hash(TaggedHash(System.Text.Encoding.UTF8.GetBytes(tag), merkleRoot));
    }

    /// <summary>
    /// <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c> (BIP-340 tagged hash).
    /// </summary>
    public static byte[] TaggedHash(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> message)
    {
        Span<byte> tagHash = stackalloc byte[32];
        SHA256.HashData(tag, tagHash);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(tagHash);
        sha.AppendData(tagHash);
        sha.AppendData(message);
        return sha.GetHashAndReset();
    }

    private static IEnumerable<byte[]> ComputeLeafBranches(Bolt12TlvStream stream) =>
        ComputeLeaves(stream).Select(l => (byte[])l.Branch);

    private static List<Bolt12TlvRecord> GetSignedRecords(Bolt12TlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var records = stream.Records.Where(r => !Bolt12TlvRanges.IsSignatureField(r.Type)).ToList();
        if (records.Count == 0)
            throw new ArgumentException("The stream has no record outside the signature range.", nameof(stream));

        return records;
    }

    private static byte[] Branch(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Span<byte> pair = stackalloc byte[64];
        if (a.SequenceCompareTo(b) <= 0)
        {
            a.CopyTo(pair);
            b.CopyTo(pair[32..]);
        }
        else
        {
            b.CopyTo(pair);
            a.CopyTo(pair[32..]);
        }

        return TaggedHash(s_branchTag, pair);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

/// <summary>
/// One record's leaves in the BOLT 12 Merkle tree.
/// </summary>
/// <param name="Type">The record's TLV type.</param>
/// <param name="Leaf"><c>H("LnLeaf", tlv)</c>.</param>
/// <param name="Nonce"><c>H("LnNonce" || first_tlv, type)</c>.</param>
/// <param name="Branch"><c>H("LnBranch", lesser || greater)</c> of the two.</param>
public sealed record Bolt12MerkleLeaf(ulong Type, Hash Leaf, Hash Nonce, Hash Branch);