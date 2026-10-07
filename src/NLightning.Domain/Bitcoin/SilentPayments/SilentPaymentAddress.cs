namespace NLightning.Domain.Bitcoin.SilentPayments;

using Crypto.ValueObjects;

/// <summary>A BIP 352 address, independent of curve arithmetic.</summary>
public readonly record struct SilentPaymentAddress(byte Version, CompactPubKey ScanKey, CompactPubKey SpendKey,
                                                   string Hrp);