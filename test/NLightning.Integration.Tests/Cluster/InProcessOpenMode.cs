namespace NLightning.Integration.Tests.Cluster;

/// <summary>Which BOLT 2 open an in-process node uses for a channel it funds.</summary>
public enum InProcessOpenMode
{
    /// <summary>
    /// v1 (<c>open_channel</c>), as <see cref="Docker.Utils.NLightningTestNode.OpenChannelAsync"/> does: the suites that
    /// open through the test node were proven on v1 channels. The default.
    /// </summary>
    V1,

    /// <summary>
    /// v2 (<c>open_channel2</c>, <c>openchannel --dual-fund</c>); needs <c>option_dual_fund</c> on both sides (CLN
    /// v26.06.8 only with <c>--experimental-dual-fund</c>) and no push amount.
    /// </summary>
    DualFund,

    /// <summary>
    /// The daemon's own choice (NL-551): v2 when <c>option_dual_fund</c> is negotiated and no push amount is given, v1
    /// otherwise.
    /// </summary>
    Auto
}