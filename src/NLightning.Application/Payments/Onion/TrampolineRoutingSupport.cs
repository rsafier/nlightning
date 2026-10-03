namespace NLightning.Application.Payments.Onion;

using Domain.Enums;
using Domain.Node.Options;

/// <summary>
/// Whether this node does trampoline routing (BOLTs PR 836, NL-875). One switch covers advertising, relaying and
/// receiving (decision D-TR2): the node processes trampoline onions and sets bit 57 in its invoices exactly when it
/// advertises <c>trampoline_routing</c> in its own feature bits.
/// </summary>
public static class TrampolineRoutingSupport
{
    /// <summary>
    /// Whether <paramref name="features"/> advertise <c>trampoline_routing</c>: configured (not <c>No</c>) and, while
    /// it is experimental, allowed by <see cref="FeatureOptions.AllowExperimentalFeatures"/>.
    /// </summary>
    public static bool IsAdvertised(FeatureOptions? features) =>
        features is not null && features.OptionTrampolineRouting != FeatureSupport.No
                             && features.GetNodeFeatures().HasFeature(Feature.OptionTrampolineRouting);
}