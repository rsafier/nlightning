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
    /// Empty since NL-332 (owner decision 2026-10-02): attribution_data, the last member, left it and is advertised
    /// Optional by default. Features that left the set before: quiesce, dual_fund and splice by splicing plan D13 after
    /// Proofs SP2, SPR and DF (wave d13); provide_storage with the peer_storage handlers and route_blinding with the
    /// blinded payloads (onion M5), wave rf1; onion_messages with the onion message service after Proof M6, wave M6,
    /// plan D9.
    /// Add a feature here while it is not implemented, and remove it from this set when it is.
    /// </remarks>
    public static readonly IReadOnlySet<Feature> ExperimentalFeatures = new HashSet<Feature>();

    /// <summary>
    /// The experimental set these options are gated by: <see cref="ExperimentalFeatures"/>, replaced only by tests of
    /// the gate itself (the configuration binder never sets an internal property).
    /// </summary>
    internal IReadOnlySet<Feature> ExperimentalFeatureSet { get; init; } = ExperimentalFeatures;

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
    /// Optional: peers then wait for our <c>gossip_timestamp_filter</c> instead of dumping their graph on us, our sync
    /// queries them (<c>GossipSyncManager</c>, G3-T2) and their queries are answered from the graph
    /// (<c>QueryResponder</c>, G3-T1; from an empty graph while <c>Gossip:Enabled</c> is off).
    /// </remarks>
    public FeatureSupport GossipQueries { get; set; } = FeatureSupport.Optional;

    public FeatureSupport VarOnionOptIn { get; private set; } = FeatureSupport.Compulsory;

    /// <summary>
    /// Enable expanded gossip queries (gossip_queries_ex, bits 10/11).
    /// </summary>
    /// <remarks>
    /// Optional since BOLT 7 plan G3-T4: the graph answers <c>query_option</c> with <c>timestamps_tlv</c> and
    /// <c>checksums_tlv</c> (byte-identical to Core Lightning's reply for the same channel, the captured
    /// <c>Bolt7QueryVectors</c>) and <c>query_flags</c> bits 0-4, and our range sync asks for timestamps and sends
    /// <c>query_flags</c> when both sides offer it.
    /// </remarks>
    public FeatureSupport ExpandedGossipQueries { get; set; } = FeatureSupport.Optional;

    public FeatureSupport OptionStaticRemoteKey { get; private set; } = FeatureSupport.Compulsory;

    public FeatureSupport PaymentSecret { get; private set; } = FeatureSupport.Compulsory;

    /// <summary>
    /// Enable basic MPP.
    /// </summary>
    /// <remarks>
    /// Defaults to Optional: the final hop holds the parts of a multi-part payment until <c>total_msat</c> arrives
    /// (BOLT 4 <c>basic_mpp</c>, ABCD W6-B), and our invoices advertise it. We never split our own payments. No turns
    /// multi-part receiving off (a part with <c>total_msat</c> != <c>amt_to_forward</c> is failed, BOLT 4) and removes
    /// the bit from init and from our invoices.
    /// </remarks>
    public FeatureSupport BasicMpp { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable large channels.
    /// </summary>
    public FeatureSupport LargeChannels { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable zero fee anchor tx (option_anchors, BOLT 3 <c>option_anchors_zero_fee_htlc_tx</c>).
    /// </summary>
    /// <remarks>
    /// Optional since wave O7b (BOLT 5 plan O7-T4): our commitment is CPFP-bumped through our anchor (also as a
    /// <c>submitpackage</c> package below the mempool minimum), the peer's through our anchor on it, our anchors HTLC
    /// transactions take wallet fee inputs, and every anchors channel keeps an on-chain reserve
    /// (<c>Node:Anchors</c>, <see cref="AnchorReserveOptions"/>). When both sides support it the opener picks the
    /// anchors channel type; with a peer without it the channel stays <c>option_static_remotekey</c>. Set No to open and
    /// accept <c>option_static_remotekey</c> channels only.
    /// </remarks>
    public FeatureSupport OptionAnchors { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable route blinding.
    /// </summary>
    /// <remarks>
    /// Optional by default since onion M5 (lane rf1-m5): blinded payloads are forwarded as introduction or intermediate
    /// node and received as the final node, proven against the BOLT 4 vectors and LND 0.20.
    /// </remarks>
    public FeatureSupport OptionRouteBlinding { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable beyond segwit shutdown.
    /// </summary>
    public FeatureSupport BeyondSegwitShutdown { get; set; } = FeatureSupport.No;

    /// <summary>
    /// Enable dual fund (BOLT 2 "Channel Establishment v2", BOLT 9 <c>option_dual_fund</c> 28/29).
    /// </summary>
    /// <remarks>
    /// Optional by default since splicing plan D13 (wave d13), on every network: a peer's <c>open_channel2</c> is
    /// accepted (<c>IDualFundedOpenService</c>, as accepter we contribute <c>Node:DualFund:AcceptContributionSat</c>,
    /// 0 by default) and <c>openchannel --dual-fund</c> opens a v2 channel; <c>openchannel</c> without the flag still
    /// opens a v1 channel. RBF of a v2 open is allowed too (<c>Node:DualFund:AllowRbf</c>, true by default since lane
    /// dfrbf). BOLT 9 lists no dependency. Set No to accept and open v1 channels only.
    /// </remarks>
    public FeatureSupport DualFund { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable quiescence (<c>stfu</c>, BOLT 9 <c>option_quiesce</c> 34/35).
    /// </summary>
    /// <remarks>
    /// Optional by default since splicing plan D13 (wave d13), together with <see cref="OptionSplice"/>: a peer may
    /// quiesce a channel with us (<c>IQuiescenceService</c>) for a splice or its RBF. BOLT 9 lists no dependency.
    /// </remarks>
    public FeatureSupport OptionQuiesce { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable splicing (BOLT 2 "Channel Splicing", BOLT 9 <c>option_splice</c> 62/63).
    /// </summary>
    /// <remarks>
    /// Optional by default since splicing plan D13 (wave d13, after Proofs SP2 and SPR), on every network, together
    /// with <see cref="OptionQuiesce"/>. BOLT 9 lists no dependency, but a splice needs quiescence too: splicing checks
    /// that both 35 and 63 were negotiated at use (D14), so turning <see cref="OptionQuiesce"/> off turns splicing off
    /// as well. Pre-standard bits (154/155) are never used.
    /// </remarks>
    public FeatureSupport OptionSplice { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable attribution data (BOLT 4 attributable failures and hold times, BOLT 9 bits 36/37).
    /// </summary>
    /// <remarks>
    /// Optional by default on every network since NL-332 (owner decision 2026-10-02) and no longer in
    /// <see cref="ExperimentalFeatures"/>: implemented in onion M3b (NL-072) and used by the switch and payments since
    /// ABCD wave 7, proven between NLightning nodes (Docker <c>AttributionFlowTests</c>). BOLT 4 ties every
    /// requirement to our own advertisement, not to the peer's: while advertised, the switch adds
    /// <c>attribution_data</c> (TLV 1, odd) to the <c>update_fail_htlc</c>/<c>update_fulfill_htlc</c> of an incoming
    /// HTLC without <c>path_key</c>. A peer without the feature (LND 0.20) ignores the odd TLV and reads the legacy
    /// reason/preimage unchanged. A received <c>attribution_data</c> is verified whatever this setting.
    /// </remarks>
    public FeatureSupport OptionAttributionData { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Enable onion messages (BOLT 4, wave M6): Optional by default since Proof M6 against CLN (plan D9).
    /// </summary>
    public FeatureSupport OptionOnionMessages { get; set; } = FeatureSupport.Optional;

    /// <summary>
    /// Offer BOLT 1 peer storage: keep the latest <c>peer_storage</c> blob of each peer we have a channel with and hand
    /// it back with <c>peer_storage_retrieval</c> after every init.
    /// </summary>
    /// <remarks>
    /// Implemented by the Application <c>PeerStorageService</c> (<c>AddPeerStorageServices</c>, called by the daemon's
    /// <c>AddNltgNodeServices</c>). Optional by default; a host that advertises it without registering that service
    /// drops every <c>peer_storage</c>, against the BOLT 1 MUST (the peer factory logs that misconfiguration).
    /// Negotiated with a peer, it also makes us send that peer our own encrypted backup blob.
    /// </remarks>
    public FeatureSupport OptionProvideStorage { get; set; } = FeatureSupport.Optional;

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

    /// <summary>
    /// option_simple_close (BOLT 2 closing_complete/closing_sig, BOLT2 plan N11; LND's "rbf-coop-close").
    /// </summary>
    /// <remarks>
    /// Implemented (no longer experimental); defaults to No so the legacy closing_signed negotiation stays the default.
    /// Needs <see cref="BeyondSegwitShutdown"/> (BOLT 9 dependency). Negotiated only when both sides signal it.
    /// </remarks>
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

            if (!AllowExperimentalFeatures && ExperimentalFeatureSet.Contains(feature))
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
        { Feature.OptionSplice, OptionSplice },
    };

    /// <summary>
    /// Whether a feature configured with <paramref name="support"/> goes into our feature bits: never when it is
    /// disabled, and never for an <see cref="ExperimentalFeatures">experimental</see> one unless
    /// <see cref="AllowExperimentalFeatures"/> is set.
    /// </summary>
    private bool IsAdvertised(Feature feature, FeatureSupport support)
    {
        return support != FeatureSupport.No
            && (AllowExperimentalFeatures || !ExperimentalFeatureSet.Contains(feature));
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

        if (IsAdvertised(Feature.OptionSplice, OptionSplice))
        {
            features.SetFeature(Feature.OptionSplice, OptionSplice == FeatureSupport.Compulsory);
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
            OptionSplice = featureSet.IsFeatureSet(Feature.OptionSplice, true)
                               ? FeatureSupport.Compulsory
                               : featureSet.IsFeatureSet(Feature.OptionSplice, false)
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