namespace NLightning.Domain.Offers.Validators;

/// <summary>
/// The first BOLT 12 requirement a string or TLV stream breaks.
/// </summary>
/// <param name="RequirementId">The requirement row of the BOLT 12 plan (<see cref="Bolt12RequirementIds"/>).</param>
/// <param name="Reason">What is wrong, for logs (never sent to a peer as is).</param>
/// <param name="Field">The offending TLV type, when there is one (an <c>invoice_error</c> <c>erroneous_field</c>
/// candidate).</param>
public sealed record Bolt12Violation(string RequirementId, string Reason, ulong? Field = null)
{
    public override string ToString() =>
        Field is { } field ? $"{RequirementId} (field {field}): {Reason}" : $"{RequirementId}: {Reason}";
}