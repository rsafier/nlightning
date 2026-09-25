namespace NLightning.Bolt11.Services;

using Domain.Enums;
using Domain.Node;
using Interfaces;
using Models;

public class InvoiceValidationService : IInvoiceValidationService
{
    private static readonly Feature[] s_knownFeatures = Enum.GetValues<Feature>();

    public ValidationResult ValidateInvoice(Invoice invoice)
    {
        var results = new List<ValidationResult>
        {
            ValidateRequiredFields(invoice),
            ValidateFieldCombinations(invoice),
        };

        var errors = results.SelectMany(r => r.Errors).ToList();
        return new ValidationResult(errors.Count == 0, errors);
    }

    public ValidationResult ValidateForEncoding(Invoice invoice)
    {
        var results = new List<ValidationResult>
        {
            ValidateInvoice(invoice),
            ValidateFeatures(invoice),
        };

        var errors = results.SelectMany(r => r.Errors).ToList();
        return new ValidationResult(errors.Count == 0, errors);
    }

    public ValidationResult ValidateRequiredFields(Invoice invoice)
    {
        var errors = new List<string>();

        // Payment hash is required (p field)
        if (invoice.PaymentHash is null)
            errors.Add($"{nameof(invoice.PaymentHash)} is required");

        // Payment secret is required (s field)
        if (invoice.PaymentSecret is null)
            errors.Add($"{nameof(invoice.PaymentSecret)} is required");

        // Either description or description hash is required (d or h field)
        var hasDescription = invoice.Description is not null;
        var hasDescriptionHash = invoice.DescriptionHash is not null;
        if (!hasDescription && !hasDescriptionHash)
            errors.Add($"Either {nameof(invoice.Description)} or {nameof(invoice.DescriptionHash)} is required");

        return new ValidationResult(errors.Count == 0, errors);
    }

    public ValidationResult ValidateFieldCombinations(Invoice invoice)
    {
        var errors = new List<string>();

        // Description and description hash are mutually exclusive
        var hasDescription = invoice.Description is not null;
        var hasDescriptionHash = invoice.DescriptionHash is not null;
        if (hasDescription && hasDescriptionHash)
            errors.Add($"{nameof(invoice.Description)} and {nameof(invoice.DescriptionHash)} cannot both be present");

        return new ValidationResult(errors.Count == 0, errors);
    }

    public ValidationResult ValidateFeatures(Invoice invoice)
    {
        var features = invoice.Features;
        if (features is null)
            return ValidationResult.Failure($"{nameof(invoice.Features)} is required");

        var errors = new List<string>();

        // BOLT 11: a writer sets payment_secret (the `s` field is mandatory); var_onion_optin is ASSUMED by payers
        if (!features.HasFeature(Feature.VarOnionOptin))
            errors.Add($"{nameof(Feature.VarOnionOptin)} must be set");

        if (!features.HasFeature(Feature.PaymentSecret))
            errors.Add($"{nameof(Feature.PaymentSecret)} must be set");

        // BOLT 9: a feature MUST NOT be set without the features it depends on
        foreach (var (feature, dependency) in features.GetMissingDependencies())
            errors.Add($"{feature} requires {dependency}");

        // BOLT 9: the origin node MUST NOT set feature bits in fields not specified by the table
        foreach (var feature in s_knownFeatures)
        {
            var contexts = FeatureSet.GetContexts(feature);
            if (contexts != FeatureContext.None
             && (contexts & FeatureContext.Invoice) == FeatureContext.None
             && features.HasFeature(feature))
                errors.Add($"{feature} may not be set in an invoice");
        }

        return new ValidationResult(errors.Count == 0, errors);
    }
}