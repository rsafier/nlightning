namespace NLightning.Bolt11.Interfaces;

using Models;

public interface IInvoiceValidationService
{
    /// <summary>
    /// Validates the rules every invoice (decoded or encoded) must follow: required fields and field combinations.
    /// </summary>
    ValidationResult ValidateInvoice(Invoice invoice);

    /// <summary>
    /// Validates everything <see cref="ValidateInvoice"/> does plus the writer rules on the <c>9</c> field
    /// (<see cref="ValidateFeatures"/>). <see cref="Invoice.Encode(NBitcoin.Key)"/> runs it before signing.
    /// </summary>
    ValidationResult ValidateForEncoding(Invoice invoice);

    ValidationResult ValidateRequiredFields(Invoice invoice);
    ValidationResult ValidateFieldCombinations(Invoice invoice);

    /// <summary>
    /// Validates the writer rules on the <c>9</c> field: present, var_onion_optin and payment_secret set, every
    /// BOLT 9 dependency set, and no known feature that BOLT 9 does not allow in invoices.
    /// </summary>
    ValidationResult ValidateFeatures(Invoice invoice);
}