using System.Net;

namespace NLightning.Domain.Node.Options;

using Domain.Crypto.Constants;
using Domain.Protocol.ValueObjects;
using Enums;
using Protocol.Constants;
using Protocol.Models;
using Protocol.Tlv;

public class FeatureOptions
{
    /// <summary>
    /// Features this node does not implement yet. They are never advertised (and a configuration that enables them
    /// fails <see cref="GetValidationErrors"/>) unless <see cref="AllowExperimentalFeatures"/> is set.
    /// </summary>
    /// <remarks>
    /// Advertising a feature makes peers act on it: anchors need BOLT 5 CPFP / fee bumping (BOLT 2 plan N11), quiesce
    /// needs stfu handling, dual_fund the interactive-tx handlers, route_blinding blinded payloads (onion M5),
    /// attribution_data error attribution (onion M3b), simple_close the closing_complete/closing_sig flow, basic_mpp
    /// final-hop HTLC sets (onion M4), onion_messages the onion_message handler and provide_storage peer_storage.
    /// Remove a feature from this set when it is implemented.
    /// </remarks>
    public static readonly IReadOnlySet<Feature> ExperimentalFeatures = new HashSet<Feature>
    {
        Feature.OptionAnchors,
        Feature.OptionQuiesce,
        Feature.OptionDualFund,
        Feature.OptionRouteBlinding,
        Feature.OptionAttributionData,
        Feature.OptionSimpleClose,
        Feature.BasicMpp,
        Feature.OptionOnionMessages,
        Feature.OptionProvideStorage
    };

    /// <summary>
    /// Allow advertising the <see cref="ExperimentalFeatures"/> (not implemented yet). Off by default; only turn it on
    /// for development and interop testing, never with real funds.
    /// </summary>
    public bool AllowExperimentalFeatures { get; set; }

    /// <summary>
    /// option_data_loss_protect.
    /// </summary>
    /// <remarks>
    /// BOLT 9 marks it ASSUMED, but LND/CLN still expect the bit, so it is still advertised, as Optional rather than
    /// Compulsory because channel_reestablish itself is not implemented yet (NL-035). ASSUMED bits are sent in init
    /// and node_announcement for interop even though BOLT 9 gives them no context; a peer that omits them is treated
    /// as supporting them (see <see cref="FeatureSet.IsCompatible"/>).
    /// </remarks>
    public FeatureSupport OptionDataLossProtect { get; private set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable an upfront shutdown script.
    /// </summary>
    /// <remarks>
    /// Defaults to No: we never generate a local upfront script and shutdown does not enforce the peer's one yet,
    /// which BOLT 2 requires once option_upfront_shutdown_script is negotiated.
    /// </remarks>
    public FeatureSupport UpfrontShutdownScript { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable gossip queries.
    /// </summary>
    /// <remarks>
    /// Kept Optional even though BOLT 7 gossip is not implemented: without it, peers dump the full gossip map on us
    /// instead of waiting for a gossip_timestamp_filter. Incoming gossip must be known-and-ignored (NL-100), and
    /// gossip queries are answered with "no channels known" (see PeerService).
    /// </remarks>
    public FeatureSupport GossipQueries { get; set; } = FeatureSupport.Optional;

    public FeatureSupport VarOnionOptIn { get; private set; } = FeatureSupport.Compulsory;

    /// <summary>
    /// Enable expanded gossip queries.
    /// </summary>
    /// <remarks>
    /// Defaults to No: we keep no gossip, so query_option / query_flags are parsed but never answered with timestamps
    /// or checksums.
    /// </remarks>
    public FeatureSupport ExpandedGossipQueries { get; set; } = FeatureSupport.No;

    public FeatureSupport OptionStaticRemoteKey { get; private set; } = FeatureSupport.Compulsory;

    public FeatureSupport PaymentSecret { get; private set; } = FeatureSupport.Compulsory;

    /// <summary>
    /// Enable basic MPP.
    /// </summary>
    /// <remarks>
    /// Defaults to No: receiving payments (HTLCs) is not implemented.
    /// </remarks>
    public FeatureSupport BasicMpp { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable large channels.
    /// </summary>
    public FeatureSupport LargeChannels { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable zero fee anchor tx.
    /// </summary>
    /// <remarks>
    /// Experimental (see <see cref="ExperimentalFeatures"/>): anchor channels are unsafe without BOLT 5 CPFP fee
    /// bumping (BOLT 2 plan N11).
    /// </remarks>
    public FeatureSupport OptionAnchors { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable route blinding.
    /// </summary>
    /// <remarks>
    /// Defaults to No until blinded payloads are handled (onion M5).
    /// </remarks>
    public FeatureSupport OptionRouteBlinding { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable beyond segwit shutdown.
    /// </summary>
    public FeatureSupport BeyondSegwitShutdown { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable dual fund.
    /// </summary>
    /// <remarks>
    /// Defaults to No: the interactive-tx / v2 open handlers are not implemented.
    /// </remarks>
    public FeatureSupport DualFund { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable quiescence (stfu).
    /// </summary>
    /// <remarks>
    /// Defaults to No: stfu is not handled.
    /// </remarks>
    public FeatureSupport OptionQuiesce { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable attribution data.
    /// </summary>
    /// <remarks>
    /// Defaults to No until error onions carry attribution data (onion M3b).
    /// </remarks>
    public FeatureSupport OptionAttributionData { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable onion messages.
    /// </summary>
    public FeatureSupport OptionOnionMessages { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable peer storage.
    /// </summary>
    /// <remarks>
    /// Defaults to No: peer_storage messages are not handled.
    /// </remarks>
    public FeatureSupport OptionProvideStorage { get; set; } = FeatureSupport.No;

    public FeatureSupport OptionChannelType { get; private set; } = FeatureSupport.Compulsory;

    /// <summary>
    /// Enable scid alias.
    /// </summary>
    /// <remarks>
    /// Defaults to No: aliases are only partially handled (no alias-based forwarding or real-scid rejection).
    /// </remarks>
    public FeatureSupport ScidAlias { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable payment metadata.
    /// </summary>
    public FeatureSupport PaymentMetadata { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable zero conf.
    /// </summary>
    public FeatureSupport ZeroConf { get; set; } = FeatureSupport.No;

    public FeatureSupport OptionSimpleClose { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable initial routing sync.
    /// </summary>
    /// [Deprecated]
    public FeatureSupport InitialRoutingSync { get; set; } = FeatureSupport.No;

    /// <summary>
    /// The chain hashes of the node.
    /// </summary>
    /// <remarks>
    /// Initialized as Mainnet if not set.
    /// </remarks>
    public IEnumerable<ChainHash> ChainHashes { get; set; } = [];

    /// <summary>
    /// The remote address of the node.
    /// </summary>
    /// <remarks>
    /// This is used to connect to our node.
    /// </remarks>
    public IPAddress? RemoteAddress { get; set; } = null;

    /// <summary>
    /// Get Features set for the node.
    /// </summary>
    /// <param name="context">The context the features will be presented in (defaults to <c>init</c>).</param>
    /// <returns>The features set for the node, filtered to the features allowed in <paramref name="context"/>.</returns>
    public FeatureSet GetNodeFeatures(FeatureContext context = FeatureContext.Init)
    {
        return BuildFeatureSet().FilterByContext(context);
    }

    /// <summary>
    /// Validates the configured features.
    /// </summary>
    /// <returns>A list of human-readable errors; empty when the configuration is valid.</returns>
    /// <remarks>
    /// BOLT 9 requires every advertised feature to have its dependencies set. <see cref="FeatureSet.SetFeature(Feature, bool, bool)"/>
    /// would silently turn on a dependency that was configured as <see cref="FeatureSupport.No"/>, so reject that
    /// combination up front instead. Enabling one of the <see cref="ExperimentalFeatures"/> without
    /// <see cref="AllowExperimentalFeatures"/> is also an error, so the node refuses to start instead of silently
    /// dropping the setting.
    /// </remarks>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        var configured = GetConfiguredFeatures();
        foreach (var (feature, support) in configured)
        {
            if (support == FeatureSupport.No)
                continue;

            if (!AllowExperimentalFeatures && ExperimentalFeatures.Contains(feature))
            {
                errors.Add($"Feature {feature} is not implemented yet; set {nameof(AllowExperimentalFeatures)} to "
                         + "advertise it anyway");
            }

            foreach (var dependency in FeatureSet.GetDependencies(feature))
            {
                if (configured.TryGetValue(dependency, out var dependencySupport)
                 && dependencySupport == FeatureSupport.No)
                {
                    errors.Add($"Feature {feature} requires {dependency}, which is disabled");
                }
            }
        }

        return errors;
    }

    private Dictionary<Feature, FeatureSupport> GetConfiguredFeatures() => new()
    {
        { Feature.OptionDataLossProtect, OptionDataLossProtect },
        { Feature.OptionUpfrontShutdownScript, UpfrontShutdownScript },
        { Feature.GossipQueries, GossipQueries },
        { Feature.VarOnionOptin, VarOnionOptIn },
        { Feature.GossipQueriesEx, ExpandedGossipQueries },
        { Feature.OptionStaticRemoteKey, OptionStaticRemoteKey },
        { Feature.PaymentSecret, PaymentSecret },
        { Feature.BasicMpp, BasicMpp },
        { Feature.OptionSupportLargeChannel, LargeChannels },
        { Feature.OptionAnchors, OptionAnchors },
        { Feature.OptionRouteBlinding, OptionRouteBlinding },
        { Feature.OptionShutdownAnySegwit, BeyondSegwitShutdown },
        { Feature.OptionDualFund, DualFund },
        { Feature.OptionQuiesce, OptionQuiesce },
        { Feature.OptionAttributionData, OptionAttributionData },
        { Feature.OptionOnionMessages, OptionOnionMessages },
        { Feature.OptionProvideStorage, OptionProvideStorage },
        { Feature.OptionChannelType, OptionChannelType },
        { Feature.OptionScidAlias, ScidAlias },
        { Feature.OptionPaymentMetadata, PaymentMetadata },
        { Feature.OptionZeroconf, ZeroConf },
        { Feature.OptionSimpleClose, OptionSimpleClose },
    };

    /// <summary>
    /// Whether a feature configured with <paramref name="support"/> goes into our feature bits: never when it is
    /// disabled, and never for an <see cref="ExperimentalFeatures">experimental</see> one unless
    /// <see cref="AllowExperimentalFeatures"/> is set.
    /// </summary>
    private bool IsAdvertised(Feature feature, FeatureSupport support)
    {
        return support != FeatureSupport.No
            && (AllowExperimentalFeatures || !ExperimentalFeatures.Contains(feature));
    }

    private FeatureSet BuildFeatureSet()
    {
        var features = new FeatureSet();

        // FeatureSet sets data_loss_protect as compulsory by default; honour the configured support level
        if (OptionDataLossProtect == FeatureSupport.No)
            features.SetFeature(Feature.OptionDataLossProtect, false, false);
        else
            features.SetFeature(Feature.OptionDataLossProtect, OptionDataLossProtect == FeatureSupport.Compulsory);

        if (UpfrontShutdownScript != FeatureSupport.No)
        {
            features.SetFeature(Feature.OptionUpfrontShutdownScript,
                                UpfrontShutdownScript == FeatureSupport.Compulsory);
        }

        if (GossipQueries != FeatureSupport.No)
        {
            features.SetFeature(Feature.GossipQueries, GossipQueries == FeatureSupport.Compulsory);
        }

        if (ExpandedGossipQueries != FeatureSupport.No)
        {
            features.SetFeature(Feature.GossipQueriesEx, ExpandedGossipQueries == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.BasicMpp, BasicMpp))
        {
            features.SetFeature(Feature.BasicMpp, BasicMpp == FeatureSupport.Compulsory);
        }

        if (LargeChannels != FeatureSupport.No)
        {
            features.SetFeature(Feature.OptionSupportLargeChannel, LargeChannels == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionAnchors, OptionAnchors))
        {
            features.SetFeature(Feature.OptionAnchors, OptionAnchors == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionRouteBlinding, OptionRouteBlinding))
        {
            features.SetFeature(Feature.OptionRouteBlinding, OptionRouteBlinding == FeatureSupport.Compulsory);
        }

        if (BeyondSegwitShutdown != FeatureSupport.No)
        {
            features.SetFeature(Feature.OptionShutdownAnySegwit, BeyondSegwitShutdown == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionDualFund, DualFund))
        {
            features.SetFeature(Feature.OptionDualFund, DualFund == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionQuiesce, OptionQuiesce))
        {
            features.SetFeature(Feature.OptionQuiesce, OptionQuiesce == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionAttributionData, OptionAttributionData))
        {
            features.SetFeature(Feature.OptionAttributionData, OptionAttributionData == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionOnionMessages, OptionOnionMessages))
        {
            features.SetFeature(Feature.OptionOnionMessages, OptionOnionMessages == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionProvideStorage, OptionProvideStorage))
        {
            features.SetFeature(Feature.OptionProvideStorage, OptionProvideStorage == FeatureSupport.Compulsory);
        }

        if (OptionChannelType != FeatureSupport.No)
        {
            features.SetFeature(Feature.OptionChannelType, OptionChannelType == FeatureSupport.Compulsory);
        }

        if (ScidAlias != FeatureSupport.No)
        {
            features.SetFeature(Feature.OptionScidAlias, ScidAlias == FeatureSupport.Compulsory);
        }

        if (PaymentMetadata != FeatureSupport.No)
        {
            features.SetFeature(Feature.OptionPaymentMetadata, PaymentMetadata == FeatureSupport.Compulsory);
        }

        if (ZeroConf != FeatureSupport.No)
        {
            features.SetFeature(Feature.OptionZeroconf, ZeroConf == FeatureSupport.Compulsory);
        }

        if (IsAdvertised(Feature.OptionSimpleClose, OptionSimpleClose))
        {
            features.SetFeature(Feature.OptionSimpleClose, OptionSimpleClose == FeatureSupport.Compulsory);
        }

        return features;
    }

    /// <summary>
    /// Get the Init extension for the node.
    /// </summary>
    /// <returns>The Init extension for the node.</returns>
    /// <remarks>
    /// If there are no ChainHashes, Mainnet is used as default.
    /// </remarks>
    internal NetworksTlv GetInitTlvs()
    {
        // If there are no ChainHashes, use Mainnet as default
        if (!ChainHashes.Any())
        {
            ChainHashes = [ChainConstants.Main];
        }

        return new NetworksTlv(ChainHashes);

        // TODO: Review this when implementing BOLT7
        // // If RemoteAddress is set, add it to the extension
        // if (RemoteAddress != null)
        // {
        //     extension.Add(new(new BigSize(3), RemoteAddress.GetAddressBytes()));
        // }
    }

    /// <summary>
    /// Get the node options from the features and extension.
    /// </summary>
    /// <param name="featureSet">The features of the node.</param>
    /// <param name="extension">The extension of the node.</param>
    /// <returns>The node options.</returns>
    public static FeatureOptions GetNodeOptions(FeatureSet featureSet, TlvStream? extension)
    {
        var options = new FeatureOptions
        {
            // These options describe what was already negotiated, not what we advertise, so nothing is gated here
            AllowExperimentalFeatures = true,
            OptionDataLossProtect = featureSet.IsFeatureSet(Feature.OptionDataLossProtect, true)
                                        ? FeatureSupport.Compulsory
                                        : featureSet.IsFeatureSet(Feature.OptionDataLossProtect, false)
                                            ? FeatureSupport.Optional
                                            : FeatureSupport.No,
            UpfrontShutdownScript = featureSet.IsFeatureSet(Feature.OptionUpfrontShutdownScript, true)
                                        ? FeatureSupport.Compulsory
                                        : featureSet.IsFeatureSet(Feature.OptionUpfrontShutdownScript, false)
                                            ? FeatureSupport.Optional
                                            : FeatureSupport.No,
            GossipQueries = featureSet.IsFeatureSet(Feature.GossipQueries, true)
                                ? FeatureSupport.Compulsory
                                : featureSet.IsFeatureSet(Feature.GossipQueries, false)
                                    ? FeatureSupport.Optional
                                    : FeatureSupport.No,
            VarOnionOptIn = featureSet.IsFeatureSet(Feature.VarOnionOptin, true)
                                ? FeatureSupport.Compulsory
                                : featureSet.IsFeatureSet(Feature.VarOnionOptin, false)
                                    ? FeatureSupport.Optional
                                    : FeatureSupport.No,
            ExpandedGossipQueries = featureSet.IsFeatureSet(Feature.GossipQueriesEx, true)
                                        ? FeatureSupport.Compulsory
                                        : featureSet.IsFeatureSet(Feature.GossipQueriesEx, false)
                                            ? FeatureSupport.Optional
                                            : FeatureSupport.No,
            OptionStaticRemoteKey = featureSet.IsFeatureSet(Feature.OptionStaticRemoteKey, true)
                                        ? FeatureSupport.Compulsory
                                        : featureSet.IsFeatureSet(Feature.OptionStaticRemoteKey, false)
                                            ? FeatureSupport.Optional
                                            : FeatureSupport.No,
            PaymentSecret = featureSet.IsFeatureSet(Feature.PaymentSecret, true)
                                ? FeatureSupport.Compulsory
                                : featureSet.IsFeatureSet(Feature.PaymentSecret, false)
                                    ? FeatureSupport.Optional
                                    : FeatureSupport.No,
            BasicMpp = featureSet.IsFeatureSet(Feature.BasicMpp, true)
                           ? FeatureSupport.Compulsory
                           : featureSet.IsFeatureSet(Feature.BasicMpp, false)
                               ? FeatureSupport.Optional
                               : FeatureSupport.No,
            LargeChannels = featureSet.IsFeatureSet(Feature.OptionSupportLargeChannel, true)
                                ? FeatureSupport.Compulsory
                                : featureSet.IsFeatureSet(Feature.OptionSupportLargeChannel, false)
                                    ? FeatureSupport.Optional
                                    : FeatureSupport.No,
            OptionAnchors = featureSet.IsFeatureSet(Feature.OptionAnchors, true)
                                ? FeatureSupport.Compulsory
                                : featureSet.IsFeatureSet(Feature.OptionAnchors, false)
                                    ? FeatureSupport.Optional
                                    : FeatureSupport.No,
            OptionRouteBlinding = featureSet.IsFeatureSet(Feature.OptionRouteBlinding, true)
                                      ? FeatureSupport.Compulsory
                                      : featureSet.IsFeatureSet(Feature.OptionRouteBlinding, false)
                                          ? FeatureSupport.Optional
                                          : FeatureSupport.No,
            BeyondSegwitShutdown = featureSet.IsFeatureSet(Feature.OptionShutdownAnySegwit, true)
                                       ? FeatureSupport.Compulsory
                                       : featureSet.IsFeatureSet(Feature.OptionShutdownAnySegwit, false)
                                           ? FeatureSupport.Optional
                                           : FeatureSupport.No,
            DualFund = featureSet.IsFeatureSet(Feature.OptionDualFund, true)
                           ? FeatureSupport.Compulsory
                           : featureSet.IsFeatureSet(Feature.OptionDualFund, false)
                               ? FeatureSupport.Optional
                               : FeatureSupport.No,
            OptionQuiesce = featureSet.IsFeatureSet(Feature.OptionQuiesce, true)
                                ? FeatureSupport.Compulsory
                                : featureSet.IsFeatureSet(Feature.OptionQuiesce, false)
                                    ? FeatureSupport.Optional
                                    : FeatureSupport.No,
            OptionAttributionData = featureSet.IsFeatureSet(Feature.OptionAttributionData, true)
                                        ? FeatureSupport.Compulsory
                                        : featureSet.IsFeatureSet(Feature.OptionAttributionData, false)
                                            ? FeatureSupport.Optional
                                            : FeatureSupport.No,
            OptionOnionMessages = featureSet.IsFeatureSet(Feature.OptionOnionMessages, true)
                                      ? FeatureSupport.Compulsory
                                      : featureSet.IsFeatureSet(Feature.OptionOnionMessages, false)
                                          ? FeatureSupport.Optional
                                          : FeatureSupport.No,
            OptionProvideStorage = featureSet.IsFeatureSet(Feature.OptionProvideStorage, true)
                                       ? FeatureSupport.Compulsory
                                       : featureSet.IsFeatureSet(Feature.OptionProvideStorage, false)
                                           ? FeatureSupport.Optional
                                           : FeatureSupport.No,
            OptionChannelType = featureSet.IsFeatureSet(Feature.OptionChannelType, true)
                                    ? FeatureSupport.Compulsory
                                    : featureSet.IsFeatureSet(Feature.OptionChannelType, false)
                                        ? FeatureSupport.Optional
                                        : FeatureSupport.No,
            ScidAlias = featureSet.IsFeatureSet(Feature.OptionScidAlias, true)
                            ? FeatureSupport.Compulsory
                            : featureSet.IsFeatureSet(Feature.OptionScidAlias, false)
                                ? FeatureSupport.Optional
                                : FeatureSupport.No,
            PaymentMetadata = featureSet.IsFeatureSet(Feature.OptionPaymentMetadata, true)
                                  ? FeatureSupport.Compulsory
                                  : featureSet.IsFeatureSet(Feature.OptionPaymentMetadata, false)
                                      ? FeatureSupport.Optional
                                      : FeatureSupport.No,
            ZeroConf = featureSet.IsFeatureSet(Feature.OptionZeroconf, true)
                           ? FeatureSupport.Compulsory
                           : featureSet.IsFeatureSet(Feature.OptionZeroconf, false)
                               ? FeatureSupport.Optional
                               : FeatureSupport.No,
            OptionSimpleClose = featureSet.IsFeatureSet(Feature.OptionSimpleClose, true)
                                    ? FeatureSupport.Compulsory
                                    : featureSet.IsFeatureSet(Feature.OptionSimpleClose, false)
                                        ? FeatureSupport.Optional
                                        : FeatureSupport.No,
        };

        if (extension?.TryGetTlv(new BigSize(1), out var chainHashes) ?? false)
        {
            options.ChainHashes = Enumerable.Range(0, chainHashes!.Value.Length / CryptoConstants.Sha256HashLen)
                                            .Select(i => new ChainHash(
                                                        chainHashes.Value.Skip(i * 32).Take(32).ToArray()));
        }

        // TODO: Add network when implementing BOLT7

        return options;
    }
}