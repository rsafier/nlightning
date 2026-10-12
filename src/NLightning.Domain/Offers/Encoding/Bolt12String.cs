using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers.Encoding;

using Validators;

/// <summary>
/// Decodes a BOLT 12 string and checks its human-readable part, mapping failures to requirement ids.
/// </summary>
internal static class Bolt12String
{
    public static bool TryDecode(string? text, IReadOnlyCollection<string> allowedHrps,
                                 [NotNullWhen(true)] out byte[]? data, [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        data = null;
        if (!Bolt12Bech32.TryDecode(text, out var hrp, out var bytes, out var reason, out var isContinuationRule))
        {
            violation = new Bolt12Violation(isContinuationRule
                                                ? Bolt12RequirementIds.Continuation
                                                : Bolt12RequirementIds.Encoding, reason);
            return false;
        }

        if (!allowedHrps.Contains(hrp))
        {
            violation = new Bolt12Violation(Bolt12RequirementIds.Encoding,
                                            $"The prefix '{hrp}' is not {string.Join(" or ", allowedHrps)}.");
            return false;
        }

        data = bytes;
        violation = null;
        return true;
    }
}