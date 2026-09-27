namespace NLightning.Domain.Offers.Interfaces;

using Crypto.ValueObjects;

/// <summary>
/// BIP-340 signatures of BOLT 12 messages ("Signature Calculation"): the signature is over
/// <c>H(tag, merkle_root)</c> with <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c> and
/// <c>tag = "lightning" || messagename || fieldname</c> (<see cref="Constants.Bolt12Constants.InvoiceRequestSignatureTag"/>,
/// <see cref="Constants.Bolt12Constants.InvoiceSignatureTag"/>).
/// </summary>
/// <remarks>
/// <para>The Merkle root is computed by the caller from the message's non-signature TLVs (Domain,
/// <c>Bolt12MerkleTree</c>); this port only signs and verifies it. Keys on the wire are 33-byte compressed points;
/// BIP-340 uses their x coordinate (parity ignored). Signatures are 64 bytes.</para>
/// <para>No secret leaves the implementation (Infrastructure.Bitcoin <c>Bolt12Signer</c>, delegating to
/// <c>ILightningSigner</c>): the node key signs our invoices, and each invoice_request is signed with a transient payer
/// key derived from its <c>invreq_metadata</c> and a node secret, never stored (BOLT 12 plan D3).</para>
/// </remarks>
public interface IBolt12Signer
{
    /// <summary>
    /// Whether <paramref name="signature"/> is a valid BIP-340 signature by <paramref name="signerId"/> of
    /// <c>H(tag, merkleRoot)</c> (BOLT 12 readers: an invoice_request by <c>invreq_payer_id</c>, an invoice by
    /// <c>invoice_node_id</c>).
    /// </summary>
    /// <param name="tag">The signature tag.</param>
    /// <param name="merkleRoot">The message's Merkle root.</param>
    /// <param name="signerId">The signer's compressed public key.</param>
    /// <param name="signature">The 64-byte <c>signature</c> field.</param>
    /// <returns>False for a wrong length, a key that is not a point, or a signature that does not verify.</returns>
    bool Verify(string tag, Hash merkleRoot, CompactPubKey signerId, ReadOnlyMemory<byte> signature);

    /// <summary>
    /// Signs <c>H(tag, merkleRoot)</c> with the node key (an invoice of an offer whose <c>offer_issuer_id</c> is our
    /// node id).
    /// </summary>
    /// <returns>The 64-byte BIP-340 signature.</returns>
    byte[] SignAsNode(string tag, Hash merkleRoot);

    /// <summary>
    /// The transient <c>invreq_payer_id</c> for an invoice_request with <paramref name="invoiceRequestMetadata"/>
    /// (deterministic: the same metadata always gives the same key; different metadata gives unrelated keys).
    /// </summary>
    CompactPubKey DerivePayerId(ReadOnlyMemory<byte> invoiceRequestMetadata);

    /// <summary>
    /// Signs <c>H(tag, merkleRoot)</c> with the transient payer key of <paramref name="invoiceRequestMetadata"/>
    /// (BOLT 12 "Invoice Requests" writer: MUST sign with the key of <c>invreq_payer_id</c>).
    /// </summary>
    /// <returns>The 64-byte BIP-340 signature.</returns>
    byte[] SignAsPayer(ReadOnlyMemory<byte> invoiceRequestMetadata, string tag, Hash merkleRoot);

    /// <summary>
    /// Signs <c>H(tag, merkleRoot)</c> as the recipient of one of our blinded paths: with
    /// <c>HMAC256("blinded_node_id", ss) * node_key</c>, <c>ss = ECDH(pathKey, node_key)</c>, whose public key is our
    /// hop's <c>blinded_node_id</c> (BOLT 12 "Invoices": <c>invoice_node_id</c> of an offer without
    /// <c>offer_issuer_id</c>). Built, not used while every offer of ours sets <c>offer_issuer_id</c> (plan D2).
    /// </summary>
    /// <param name="pathKey">The path_key of our hop in that path.</param>
    /// <param name="tag">The signature tag.</param>
    /// <param name="merkleRoot">The message's Merkle root.</param>
    /// <returns>The 64-byte BIP-340 signature.</returns>
    byte[] SignAsBlindedRecipient(CompactPubKey pathKey, string tag, Hash merkleRoot);
}