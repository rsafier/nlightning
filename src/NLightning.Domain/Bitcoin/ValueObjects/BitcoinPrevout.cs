namespace NLightning.Domain.Bitcoin.ValueObjects;

/// <summary>An input's previous output, in transaction input order.</summary>
public sealed record BitcoinPrevout(ulong AmountSat, BitcoinScript ScriptPubKey);