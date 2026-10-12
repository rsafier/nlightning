namespace NLightning.Domain.LiquidityAds.Enums;

/// <summary>
/// The funding attempt a liquidity purchase rides on (<c>LiquidityPurchaseModel</c>). Persisted as a byte: never
/// renumber.
/// </summary>
public enum LiquidityPurchaseKind : byte
{
    /// <summary>A dual-funded open (<c>open_channel2</c>/<c>accept_channel2</c>).</summary>
    ChannelOpen = 1,

    /// <summary>An RBF attempt of a dual-funded open (<c>tx_init_rbf</c>/<c>tx_ack_rbf</c>).</summary>
    OpenRbf = 2,

    /// <summary>A splice (<c>splice_init</c>/<c>splice_ack</c>).</summary>
    Splice = 3,

    /// <summary>An RBF attempt of a splice (<c>tx_init_rbf</c>/<c>tx_ack_rbf</c>).</summary>
    SpliceRbf = 4
}