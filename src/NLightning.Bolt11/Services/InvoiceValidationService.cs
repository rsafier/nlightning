namespace NLightning.Bolt11.Services;

using Domain.Enums;
using Domain.Node;
using Interfaces;
using Models;
using Models.TaggedFields;

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
            ValidateBlindedPathsForEncoding(invoice),
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

        // Payment secret is required (s field), except in a bLIP 39 invoice with blinded paths, which carries none
        if (invoice.PaymentSecret is null && invoice.BlindedPaymentPaths.Count == 0)
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

        // bLIP 39: an invoice containing the `b` field MUST not contain the `r` field (LND refuses such an invoice
        // too): route hints and blinded paths would name two ways to a recipient that hides behind the paths
        if (invoice.BlindedPaymentPaths.Count > 0 && invoice.RouteHints.Count > 0)
            errors.Add($"{nameof(invoice.BlindedPaymentPaths)} and {nameof(invoice.RouteHints)} cannot both be "
                     + "present");

        return new ValidationResult(errors.Count == 0, errors);
    }

    public ValidationResult ValidateBlindedPathsForEncoding(Invoice invoice)
    {
        // bLIP 39 writer: an invoice with blinded paths MUST not contain the `s` field (LND's reader accepts one, so
        // the decoder does too)
        if (invoice.BlindedPaymentPaths.Count > 0 && invoice.PaymentSecret is not null)
            return ValidationResult.Failure($"An invoice with {nameof(invoice.BlindedPaymentPaths)} must not carry a "
                                          + $"{nameof(invoice.PaymentSecret)} (bLIP 39)");

        return ValidationResult.Success();
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

        // BOLT 11: a reader MUST fail on unknown even bits, so never sign an invoice that sets one
        foreach (var bit in FeaturesTaggedField.GetUnknownRequiredBits(features))
            errors.Add($"Feature bit {bit} is an unknown compulsory feature");

        // BOLT 9: the origin node MUST NOT set both the optional and mandatory bits
        var setBits = features.GetSetBits();
        foreach (var bit in setBits)
        {
            if (bit % 2 != 0 || !setBits.Contains(bit + 1))
                continue;

            var name = Enum.IsDefined((Feature)(bit + 1)) ? ((Feature)(bit + 1)).ToString() : $"Feature bits {bit}/{bit + 1}";
            errors.Add($"{name} sets both optional and compulsory bits");
        }

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