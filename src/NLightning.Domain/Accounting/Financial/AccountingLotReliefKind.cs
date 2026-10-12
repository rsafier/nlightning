namespace NLightning.Domain.Accounting.Financial;

/// <summary>What a relief did with the part of a lot it took (<c>AccountingLotReliefs.Kind</c>, NL-657). Never
/// renumber.</summary>
public enum AccountingLotReliefKind : byte
{
    /// <summary>A disposal: the sats left us; the realized gain is the proceeds less the cost.</summary>
    Disposal = 0,

    /// <summary>A transfer between our buckets: the part moved into a new lot of the destination bucket
    /// (<see cref="AccountingLot.ParentLotId"/> names this lot), at its cost; nothing is realized.</summary>
    Move = 1,

    /// <summary>The settlement of a bucket's debt (<see cref="AccountingLotOrigin.Debt"/>): msat that came back from the
    /// bucket that lent them; no lot moves and nothing is realized.</summary>
    Settlement = 2
}