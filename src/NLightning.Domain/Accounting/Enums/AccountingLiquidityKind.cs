namespace NLightning.Domain.Accounting.Enums;

/// <summary>
/// When a liquidity purchase was made (liquidity ads, NL-850): the detail <c>kind</c> of
/// <see cref="AccountingEventKind.LiquidityFeePaid"/> and <see cref="AccountingEventKind.LiquidityFeeEarned"/>
/// (<c>open</c>, <c>rbf</c>, <c>splice</c>). Not persisted as a number.
/// </summary>
public enum AccountingLiquidityKind
{
    /// <summary>A dual-funded open.</summary>
    Open = 1,

    /// <summary>An RBF attempt of a dual-funded open.</summary>
    Rbf = 2,

    /// <summary>A splice (or one of its RBF attempts).</summary>
    Splice = 3
}