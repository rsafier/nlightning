using Microsoft.Extensions.Configuration;
using NLightning.Domain.Enums;
using NLightning.Domain.Money;
using NLightning.Domain.Node.Options;

namespace NLightning.Daemon.Configuration;

/// <summary>The proven prototype subset is applied before feature negotiation and checked after all configuration.</summary>
internal static class VlsCapabilityProfile
{
    internal static void Apply(NodeOptions options)
    {
        options.MaxDustHtlcExposureMsat = 0;
        options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
        options.Routing.FeeProportionalMillionths = 0;
        var features = options.Features;
        features.OptionAnchors = FeatureSupport.No;
        features.DualFund = FeatureSupport.No;
        features.OptionQuiesce = FeatureSupport.No;
        features.OptionSplice = FeatureSupport.No;
        features.OptionSimpleTaproot = FeatureSupport.No;
        features.OptionSimpleClose = FeatureSupport.No;
        features.OptionGossipV2 = FeatureSupport.No;
        features.OptionRouteBlinding = FeatureSupport.No;
        features.OptionOnionMessages = FeatureSupport.No;
        features.OptionProvideStorage = FeatureSupport.No;
        features.OptionTrampolineRouting = FeatureSupport.No;
        features.BeyondSegwitShutdown = FeatureSupport.No;
        features.ZeroConf = FeatureSupport.No;
    }

    internal static IReadOnlyList<string> GetValidationErrors(NodeOptions options)
    {
        var errors = new List<string>();
        if (!string.Equals(options.BitcoinNetwork.Name, "regtest", StringComparison.OrdinalIgnoreCase))
            errors.Add("The VLS prototype supports regtest only.");
        if (options.MaxDustHtlcExposureMsat != 0 || options.HtlcMinimumAmount < LightningMoney.Satoshis(1_000))
            errors.Add("The VLS prototype requires zero dust HTLC exposure and a minimum HTLC of 1,000 satoshis.");
        if (options.Routing.FeeProportionalMillionths != 0 || options.Routing.FeeBaseMsat % 1_000 != 0)
            errors.Add("The VLS prototype requires fixed whole-satoshi forwarding fees.");
        var features = options.Features;
        if (features.OptionAnchors != FeatureSupport.No || features.DualFund != FeatureSupport.No
         || features.OptionQuiesce != FeatureSupport.No || features.OptionSplice != FeatureSupport.No
         || features.OptionSimpleTaproot != FeatureSupport.No || features.OptionSimpleClose != FeatureSupport.No
         || features.OptionGossipV2 != FeatureSupport.No || features.OptionRouteBlinding != FeatureSupport.No
         || features.OptionOnionMessages != FeatureSupport.No || features.OptionProvideStorage != FeatureSupport.No
         || features.OptionTrampolineRouting != FeatureSupport.No || features.BeyondSegwitShutdown != FeatureSupport.No
         || features.ZeroConf != FeatureSupport.No)
            errors.Add("The VLS prototype requires single-funded static-remotekey ECDSA channels and disables unsupported features.");
        return errors;
    }

    internal static IConfiguration Apply(IConfiguration configuration) => new ConfigurationBuilder()
        .AddConfiguration(configuration)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:MaxDustHtlcExposureMsat"] = "0",
            ["Node:Routing:FeeProportionalMillionths"] = "0",
            ["Gossip:AcceptPublicChannels"] = "false",
            ["Gossip:AllowPublicChannelsOnMainnet"] = "false",
            ["Node:Features:OptionAnchors"] = "No",
            ["Node:Features:DualFund"] = "No",
            ["Node:Features:OptionQuiesce"] = "No",
            ["Node:Features:OptionSplice"] = "No",
            ["Node:Features:OptionSimpleTaproot"] = "No",
            ["Node:Features:OptionSimpleClose"] = "No",
            ["Node:Features:OptionGossipV2"] = "No",
            ["Node:Features:OptionRouteBlinding"] = "No",
            ["Node:Features:OptionOnionMessages"] = "No",
            ["Node:Features:OptionProvideStorage"] = "No",
            ["Node:Features:OptionTrampolineRouting"] = "No",
            ["Node:Features:BeyondSegwitShutdown"] = "No",
            ["Node:Features:ZeroConf"] = "No"
        }).Build();
}