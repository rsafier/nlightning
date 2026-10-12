namespace NLightning.Domain.Channels.Splicing.Enums;

/// <summary>
/// How a channel funding came to exist (splicing plan §3.3).
/// </summary>
/// <remarks>Persisted by lane SP1-C (<c>ChannelFundings</c>): never renumber.</remarks>
public enum ChannelFundingKind : byte
{
    /// <summary>The funding transaction of the channel open (v1 <c>funding_created</c> or a v2 dual-funded open).</summary>
    Initial = 0,

    /// <summary>A splice transaction negotiated with <c>splice_init</c>/<c>splice_ack</c>.</summary>
    Splice = 1,

    /// <summary>An RBF attempt of a pending splice (<c>tx_init_rbf</c>/<c>tx_ack_rbf</c>, wave SPR).</summary>
    SpliceRbf = 2
}