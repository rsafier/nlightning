namespace NLightning.Application.Channels.Close.Simple;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Payloads;

/// <summary>One closing transaction variant we signed in our <c>closing_complete</c>.</summary>
/// <param name="Unsigned">The transaction without witness.</param>
/// <param name="OurSignature">Our ECDSA signature of it; null on a simple taproot channel.</param>
/// <param name="OurPartialSignature">Simple taproot channels: our MuSig2 partial signature of it with our closer nonce;
/// null otherwise.</param>
public sealed record SignedClosingVariant(SignedTransaction Unsigned, CompactSignature? OurSignature,
                                          MusigPartialSignatureWithNonce? OurPartialSignature = null);

/// <summary>
/// Our <c>closing_complete</c> waiting for its <c>closing_sig</c> (BOLT 2 <c>option_simple_close</c>): what we sent
/// and each variant we signed, so the peer's single signature can complete one of them.
/// </summary>
/// <param name="Payload">The fields we sent (the <c>closing_sig</c> must repeat them).</param>
/// <param name="Terms">The proposal's numbers and scripts.</param>
/// <param name="Variants">Every variant we signed, by its <c>closing_tlvs</c> field.</param>
/// <param name="RemoteCloseeNonce">Simple taproot channels: the peer's closee nonce our partial signatures were made
/// against (its <c>closing_sig</c> signs with it); null otherwise.</param>
public sealed record SimpleCloseProposal(ClosingCompletePayload Payload, SimpleClosingTerms Terms,
                                         IReadOnlyDictionary<ClosingSigKind, SignedClosingVariant> Variants,
                                         MusigPublicNonce? RemoteCloseeNonce = null);