namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

/// <summary>All transaction outpoints are included; only eligible inputs carry a private key. The caller owns and wipes keys.</summary>
public sealed record SilentPaymentSenderInput(byte[] Outpoint36, byte[]? PrivateKey32, bool IsTaproot);