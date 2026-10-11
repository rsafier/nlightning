namespace NLightning.Integration.Tests.Docker.Onchain;

using Domain.Enums;
using Domain.Node.Options;

/// <summary>
/// The legacy on-chain proofs (O2-O6, final hop, mempool, watch catch-up) prove <c>option_static_remotekey</c>
/// channels: their expected outputs (a to_remote without CSV, HTLC transactions with their own fee, no anchors) are
/// those of that channel type. Since wave O7b the node advertises <c>option_anchors</c> by default and LND picks it, so
/// these proofs pin the legacy type; the anchors proofs are in <c>Docker/Onchain/Anchors/</c>.
/// </summary>
internal static class LegacyChannelOptions
{
    /// <summary>Turns <c>option_anchors</c> off, so every channel of the node is <c>option_static_remotekey</c>.</summary>
    public static void PinStaticRemoteKey(NodeOptions options) => options.Features.OptionAnchors = FeatureSupport.No;

    /// <summary>
    /// <see cref="PinStaticRemoteKey"/> followed by <paramref name="configure"/> (when given).
    /// </summary>
    public static Action<NodeOptions> PinStaticRemoteKey(Action<NodeOptions>? configure) => options =>
    {
        PinStaticRemoteKey(options);
        configure?.Invoke(options);
    };
}