namespace NLightning.Domain.Protocol.Payloads;

using Constants;
using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The simple taproot <c>closing_tlvs</c> of <c>closing_complete</c> (types 5, 6 and 7): up to three 98-byte MuSig2
/// partial signatures with the closer's nonce, one per closing transaction variant, next to the ECDSA
/// <see cref="ClosingSignatures"/> (types 1, 2 and 3) of a non-taproot channel.
/// </summary>
public sealed record ClosingPartialSignaturesWithNonce(MusigPartialSignatureWithNonce? CloserOutputOnly = null,
                                                       MusigPartialSignatureWithNonce? CloseeOutputOnly = null,
                                                       MusigPartialSignatureWithNonce? CloserAndCloseeOutputs = null)
{
    /// <summary>The TLV types of these signatures (5, 6 and 7).</summary>
    public static readonly IReadOnlySet<BigSize> TlvTypes = new HashSet<BigSize>
    {
        TaprootTlvConstants.CloserNoClosee, TaprootTlvConstants.NoCloserClosee, TaprootTlvConstants.CloserAndClosee
    };

    /// <summary>The signature for <paramref name="kind"/>, or null when absent.</summary>
    public MusigPartialSignatureWithNonce? Get(ClosingSigKind kind) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => CloserOutputOnly,
        ClosingSigKind.CloseeOutputOnly => CloseeOutputOnly,
        ClosingSigKind.CloserAndCloseeOutputs => CloserAndCloseeOutputs,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
    };

    /// <summary>The kinds present, in TLV order.</summary>
    public IReadOnlyList<ClosingSigKind> Kinds => ClosingPartialSignatures.KindsOf(
        CloserOutputOnly.HasValue, CloseeOutputOnly.HasValue, CloserAndCloseeOutputs.HasValue);

    /// <summary>A set with only <paramref name="signature"/> for <paramref name="kind"/>.</summary>
    public static ClosingPartialSignaturesWithNonce Single(ClosingSigKind kind,
                                                           MusigPartialSignatureWithNonce signature) => kind switch
                                                           {
                                                               ClosingSigKind.CloserOutputOnly => new ClosingPartialSignaturesWithNonce(CloserOutputOnly: signature),
                                                               ClosingSigKind.CloseeOutputOnly => new ClosingPartialSignaturesWithNonce(CloseeOutputOnly: signature),
                                                               ClosingSigKind.CloserAndCloseeOutputs =>
                                                                   new ClosingPartialSignaturesWithNonce(CloserAndCloseeOutputs: signature),
                                                               _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
                                                           };

    /// <summary>The TLV type of <paramref name="kind"/> (5, 6 or 7).</summary>
    public static BigSize TypeOf(ClosingSigKind kind) => ClosingPartialSignatures.TypeOf(kind);
}