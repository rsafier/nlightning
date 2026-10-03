namespace NLightning.Domain.Protocol.Payloads;

/// <summary>Which closing transaction a <c>closing_tlvs</c> signature is for (its TLV type).</summary>
public enum ClosingSigKind : byte
{
    /// <summary>TLV 1 <c>closer_output_only</c>: the transaction with only the closer's output.</summary>
    CloserOutputOnly = 1,

    /// <summary>TLV 2 <c>closee_output_only</c>: the transaction with only the closee's output.</summary>
    CloseeOutputOnly = 2,

    /// <summary>TLV 3 <c>closer_and_closee_outputs</c>: the transaction with both outputs.</summary>
    CloserAndCloseeOutputs = 3
}