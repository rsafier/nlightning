namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

using Crypto.ValueObjects;

public sealed record SilentPaymentRecipient(CompactPubKey ScanKey, CompactPubKey SpendKey);