namespace NLightning.Domain.Protocol.Payloads;

using Constants;
using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The simple taproot <c>closing_tlvs</c> of <c>closing_sig</c> (types 5, 6 and 7): the closee's 32-byte MuSig2 partial
/// signature (its nonce is the one it sent before: <c>shutdown_nonce</c> or the last <c>next_closee_nonce</c>), next to
/// the ECDSA <see cref="ClosingSignatures"/> (types 1, 2 and 3) of a non-taproot channel.
/// </summary>
public sealed record ClosingPartialSignatures(MusigPartialSignature? CloserOutputOnly = null,
                                              MusigPartialSignature? CloseeOutputOnly = null,
                                              MusigPartialSignature? CloserAndCloseeOutputs = null)
{
    /// <summary>The TLV types of these signatures (5, 6 and 7).</summary>
    public static readonly IReadOnlySet<BigSize> TlvTypes = ClosingPartialSignaturesWithNonce.TlvTypes;

    /// <summary>The signature for <paramref name="kind"/>, or null when absent.</summary>
    public MusigPartialSignature? Get(ClosingSigKind kind) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => CloserOutputOnly,
        ClosingSigKind.CloseeOutputOnly => CloseeOutputOnly,
        ClosingSigKind.CloserAndCloseeOutputs => CloserAndCloseeOutputs,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
    };

    /// <summary>The kinds present, in TLV order.</summary>
    public IReadOnlyList<ClosingSigKind> Kinds =>
        KindsOf(CloserOutputOnly.HasValue, CloseeOutputOnly.HasValue, CloserAndCloseeOutputs.HasValue);

    /// <summary>A set with only <paramref name="signature"/> for <paramref name="kind"/>.</summary>
    public static ClosingPartialSignatures Single(ClosingSigKind kind, MusigPartialSignature signature) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => new ClosingPartialSignatures(CloserOutputOnly: signature),
        ClosingSigKind.CloseeOutputOnly => new ClosingPartialSignatures(CloseeOutputOnly: signature),
        ClosingSigKind.CloserAndCloseeOutputs => new ClosingPartialSignatures(CloserAndCloseeOutputs: signature),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
    };

    /// <summary>The TLV type of <paramref name="kind"/> (5, 6 or 7).</summary>
    public static BigSize TypeOf(ClosingSigKind kind) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => TaprootTlvConstants.CloserNoClosee,
        ClosingSigKind.CloseeOutputOnly => TaprootTlvConstants.NoCloserClosee,
        ClosingSigKind.CloserAndCloseeOutputs => TaprootTlvConstants.CloserAndClosee,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
    };

    internal static IReadOnlyList<ClosingSigKind> KindsOf(bool closerOutputOnly, bool closeeOutputOnly,
                                                          bool closerAndCloseeOutputs)
    {
        var kinds = new List<ClosingSigKind>(3);
        if (closerOutputOnly)
            kinds.Add(ClosingSigKind.CloserOutputOnly);
        if (closeeOutputOnly)
            kinds.Add(ClosingSigKind.CloseeOutputOnly);
        if (closerAndCloseeOutputs)
            kinds.Add(ClosingSigKind.CloserAndCloseeOutputs);
        return kinds;
    }
}