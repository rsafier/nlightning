namespace NLightning.Domain.Bitcoin.SilentPayments.Models;

/// <summary>Tweak32 is t_k, without the separately identified label tweak.</summary>
public sealed record SilentPaymentScanMatch(uint OutputIndex, byte[] OutputKey32, byte[] Tweak32, uint? Label);