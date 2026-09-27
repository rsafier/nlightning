using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Offers;

using Crypto.Contexts;
using Domain.Bitcoin.Interfaces;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;

/// <summary>
/// BOLT 12 BIP-340 signatures (plan B1-T1, §3.6): verification here, signing delegated to
/// <see cref="ILightningSigner.SignBolt12"/> so no secret ever reaches this class.
/// </summary>
/// <remarks>Stateless and thread-safe.</remarks>
public sealed class Bolt12Signer : IBolt12Signer
{
    private const int SchnorrSignatureLength = 64;

    private readonly ILightningSigner _lightningSigner;

    public Bolt12Signer(ILightningSigner lightningSigner)
    {
        _lightningSigner = lightningSigner;
    }

    /// <inheritdoc />
    public bool Verify(string tag, Hash merkleRoot, CompactPubKey signerId, ReadOnlyMemory<byte> signature)
    {
        var root = (byte[])merkleRoot;
        var signerIdBytes = (byte[])signerId;
        if (tag is null || root is null || signerIdBytes is null
         || signature.Length != SchnorrSignatureLength
         || signerIdBytes.Length != CryptoConstants.CompactPubkeyLen)
            return false;

        // BIP-340 keys are x-only: the parity byte of the compressed key is ignored
        if (!ECPubKey.TryCreate(signerIdBytes, NLightningCryptoContext.Instance, out var compressed, out var pubKey)
         || !compressed || pubKey is null)
            return false;

        if (!SecpSchnorrSignature.TryCreate(signature.Span, out var schnorrSignature) || schnorrSignature is null)
            return false;

        var digest = Bolt12TaggedHash.Compute(tag, root);
        return pubKey.ToXOnlyPubKey().SigVerifyBIP340(schnorrSignature, digest);
    }

    /// <inheritdoc />
    public byte[] SignAsNode(string tag, Hash merkleRoot) =>
        _lightningSigner.SignBolt12(Bolt12SigningKey.Node, tag, merkleRoot);

    /// <inheritdoc />
    public CompactPubKey DerivePayerId(ReadOnlyMemory<byte> invoiceRequestMetadata) =>
        _lightningSigner.GetBolt12PayerId(invoiceRequestMetadata);

    /// <inheritdoc />
    public byte[] SignAsPayer(ReadOnlyMemory<byte> invoiceRequestMetadata, string tag, Hash merkleRoot) =>
        _lightningSigner.SignBolt12(Bolt12SigningKey.Payer(invoiceRequestMetadata), tag, merkleRoot);

    /// <inheritdoc />
    public byte[] SignAsBlindedRecipient(CompactPubKey pathKey, string tag, Hash merkleRoot) =>
        _lightningSigner.SignBolt12(Bolt12SigningKey.BlindedRecipient(pathKey), tag, merkleRoot);
}