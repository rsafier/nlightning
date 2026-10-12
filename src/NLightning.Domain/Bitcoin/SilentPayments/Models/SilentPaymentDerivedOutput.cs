namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

public sealed record SilentPaymentDerivedOutput(int RecipientIndex, byte[] OutputKey32, byte[] Tweak32);