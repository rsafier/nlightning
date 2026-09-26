namespace NLightning.Domain.Protocol.Payloads;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The <c>closing_tlvs</c> of <c>closing_complete</c>/<c>closing_sig</c> (BOLT 2 <c>option_simple_close</c>): up to
/// three 64-byte compact signatures, one per closing transaction variant.
/// </summary>
public sealed record ClosingSignatures(CompactSignature? CloserOutputOnly = null,
                                       CompactSignature? CloseeOutputOnly = null,
                                       CompactSignature? CloserAndCloseeOutputs = null)
{
    /// <summary>TLV type 1, <c>closer_output_only</c>.</summary>
    public static readonly BigSize CloserOutputOnlyType = 1;

    /// <summary>TLV type 2, <c>closee_output_only</c>.</summary>
    public static readonly BigSize CloseeOutputOnlyType = 2;

    /// <summary>TLV type 3, <c>closer_and_closee_outputs</c>.</summary>
    public static readonly BigSize CloserAndCloseeOutputsType = 3;

    /// <summary>The TLV types of <c>closing_tlvs</c>.</summary>
    public static readonly IReadOnlySet<BigSize> TlvTypes =
        new HashSet<BigSize> { CloserOutputOnlyType, CloseeOutputOnlyType, CloserAndCloseeOutputsType };

    /// <summary>The signature for <paramref name="kind"/>, or null when absent.</summary>
    public CompactSignature? Get(ClosingSigKind kind) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => CloserOutputOnly,
        ClosingSigKind.CloseeOutputOnly => CloseeOutputOnly,
        ClosingSigKind.CloserAndCloseeOutputs => CloserAndCloseeOutputs,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
    };

    /// <summary>The kinds present, in TLV order.</summary>
    public IReadOnlyList<ClosingSigKind> Kinds
    {
        get
        {
            var kinds = new List<ClosingSigKind>(3);
            if (CloserOutputOnly is not null)
                kinds.Add(ClosingSigKind.CloserOutputOnly);
            if (CloseeOutputOnly is not null)
                kinds.Add(ClosingSigKind.CloseeOutputOnly);
            if (CloserAndCloseeOutputs is not null)
                kinds.Add(ClosingSigKind.CloserAndCloseeOutputs);
            return kinds;
        }
    }

    /// <summary>A set with only <paramref name="signature"/> for <paramref name="kind"/>.</summary>
    public static ClosingSignatures Single(ClosingSigKind kind, CompactSignature signature) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => new ClosingSignatures(CloserOutputOnly: signature),
        ClosingSigKind.CloseeOutputOnly => new ClosingSignatures(CloseeOutputOnly: signature),
        ClosingSigKind.CloserAndCloseeOutputs => new ClosingSignatures(CloserAndCloseeOutputs: signature),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
    };

    /// <summary>The TLV type of <paramref name="kind"/>.</summary>
    public static BigSize TypeOf(ClosingSigKind kind) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => CloserOutputOnlyType,
        ClosingSigKind.CloseeOutputOnly => CloseeOutputOnlyType,
        ClosingSigKind.CloserAndCloseeOutputs => CloserAndCloseeOutputsType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown closing signature kind")
    };
}