namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

public sealed record SilentPaymentScanCandidate(uint OutputIndex, byte[] OutputKey32);