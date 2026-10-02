namespace NLightning.Domain.Accounting.Models;

/// <summary>
/// What one sealer round did.
/// </summary>
/// <param name="Sealed">Events that got a ledger sequence.</param>
/// <param name="Duplicates">Events marked duplicate (a key already sealed, or repeated in the round).</param>
/// <param name="Tip">The chain tip after the round.</param>
public sealed record AccountingSealRoundResult(int Sealed, int Duplicates, AccountingChainTip Tip);