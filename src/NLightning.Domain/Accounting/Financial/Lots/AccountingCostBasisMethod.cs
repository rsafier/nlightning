namespace NLightning.Domain.Accounting.Financial.Lots;

/// <summary>
/// The order in which a disposal relieves the cost-basis lots (<c>Accounting:CostBasis</c>, D-A2, D-A12; NL-602 A3-T4).
/// Specific identification is left out of A3.
/// </summary>
public enum AccountingCostBasisMethod
{
    /// <summary>First in, first out: the oldest lot first (the default, D-A2).</summary>
    Fifo = 0,

    /// <summary>Last in, first out: the newest lot first.</summary>
    Lifo = 1,

    /// <summary>Highest in, first out: the lot with the highest cost per msat first (lots without a cost last).</summary>
    Hifo = 2
}