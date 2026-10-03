namespace NLightning.Domain.Protocol.Onion.Enums;

/// <summary>
/// How the <c>hop_payloads</c> length of a trampoline onion is chosen (see
/// <see cref="Models.TrampolineOnionSizePolicy"/>).
/// </summary>
public enum TrampolineOnionSizeMode
{
    /// <summary>
    /// Exactly the framed hop payloads (<c>bigsize(len) || payload || hmac</c> per hop), no trailing filler. The
    /// PR 836 test vectors are built this way.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// A caller-given length; the framed payloads must fit in it.
    /// </summary>
    Fixed = 1,

    /// <summary>
    /// <see cref="Constants.TrampolineOnionConstants.RecommendedHopPayloadsLength"/> (650) when the framed payloads
    /// fit in it and it does not exceed the caller's maximum, else the exact length, never above the maximum.
    /// </summary>
    Auto = 2
}