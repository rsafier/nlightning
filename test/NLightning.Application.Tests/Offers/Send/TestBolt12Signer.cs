using NBitcoin.Secp256k1;

namespace NLightning.Application.Tests.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Offers.Interfaces;
using Domain.Offers.Signing;

/// <summary>
/// A BIP-340 <see cref="IBolt12Signer"/> for tests (stand-in for lane B12-B's <c>Bolt12Signer</c>): signs with a given
/// node key and payer keys derived as <c>HMAC-SHA256(payerSecret, metadata)</c>.
/// </summary>
internal sealed class TestBolt12Signer : IBolt12Signer
{
    private readonly byte[] _nodeKey;
    private readonly byte[] _payerSecret;

    public TestBolt12Signer(byte[] nodeKey, byte[]? payerSecret = null)
    {
        _nodeKey = nodeKey;
        _payerSecret = payerSecret ?? Enumerable.Repeat((byte)0x5A, 32).ToArray();
    }

    public int PayerSignatures { get; private set; }

    public CompactPubKey NodeId => PubKeyOf(_nodeKey);

    public bool Verify(string tag, Hash merkleRoot, CompactPubKey signerId, ReadOnlyMemory<byte> signature)
    {
        if (signature.Length != 64 || !ECPubKey.TryCreate(((byte[])signerId).AsSpan(), Context.Instance, out _, out var key)
         || !SecpSchnorrSignature.TryCreate(signature.Span, out var schnorr))
            return false;

        return key.ToXOnlyPubKey().SigVerifyBIP340(schnorr, Bolt12MerkleTree.GetSignatureDigest(tag, merkleRoot));
    }

    public byte[] SignAsNode(string tag, Hash merkleRoot) => Sign(_nodeKey, tag, merkleRoot);

    public CompactPubKey DerivePayerId(ReadOnlyMemory<byte> invoiceRequestMetadata) =>
        PubKeyOf(PayerKey(invoiceRequestMetadata));

    public byte[] SignAsPayer(ReadOnlyMemory<byte> invoiceRequestMetadata, string tag, Hash merkleRoot)
    {
        PayerSignatures++;
        return Sign(PayerKey(invoiceRequestMetadata), tag, merkleRoot);
    }

    public byte[] SignAsBlindedRecipient(CompactPubKey pathKey, string tag, Hash merkleRoot) =>
        throw new NotSupportedException();

    public static byte[] Sign(byte[] privateKey, string tag, Hash merkleRoot)
    {
        var key = ECPrivKey.Create(privateKey);
        var signature = key.SignBIP340(Bolt12MerkleTree.GetSignatureDigest(tag, merkleRoot));
        var bytes = new byte[64];
        signature.WriteToSpan(bytes);
        return bytes;
    }

    public static CompactPubKey PubKeyOf(byte[] privateKey) =>
        new(ECPrivKey.Create(privateKey).CreatePubKey().ToBytes(true));

    private byte[] PayerKey(ReadOnlyMemory<byte> metadata) => System.Security.Cryptography.HMACSHA256.HashData(_payerSecret, metadata.Span);
}