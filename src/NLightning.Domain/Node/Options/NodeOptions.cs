using System.Text;

namespace NLightning.Domain.Node.Options;

using Money;
using Protocol.Constants;
using Protocol.ValueObjects;

public class NodeOptions
{
    // private FeatureOptions _features;

    /// <summary>
    /// The network to connect to: "mainnet", "testnet", "testnet4", "regtest" or "signet" (a custom signet such as
    /// Mutinynet is "signet" here, named by <see cref="CustomSignet"/>). Set it from configuration through
    /// <see cref="BitcoinNetwork.Resolve"/>, which fails on an unknown name.
    /// </summary>
    public BitcoinNetwork BitcoinNetwork { get; set; } = NetworkConstants.Mainnet;

    /// <summary>
    /// The custom signet this node runs on, if any (configuration section <c>Node:CustomSignet</c>). Requires
    /// <see cref="BitcoinNetwork"/> signet.
    /// </summary>
    /// <see cref="CustomSignetOptions"/>
    public CustomSignetOptions? CustomSignet { get; set; }

    /// <summary>
    /// True if NLTG should run in Daemon mode (background)
    /// </summary>
    public bool Daemon { get; set; }

    /// <summary>
    /// The old DNS seed list, read by nothing before BOLT 10 bootstrap (NL-113) and ignored since: older templates
    /// wrote mainnet seeds into it on every network. When it holds seeds the old template did not write, the host sets
    /// <see cref="BootstrapOptions.ObsoleteSeedsIgnored"/> and an enabled bootstrap warns; the seeds come from
    /// <c>Node:Bootstrap:Seeds</c> or the network's defaults. Empty by default.
    /// </summary>
    [Obsolete("Use Bootstrap.Seeds (Node:Bootstrap:Seeds).")]
    public List<string> DnsSeedServers { get; set; } = [];

    /// <summary>
    /// BOLT 10 DNS seed bootstrap (NL-113), from <c>Node:Bootstrap</c>; on by default on mainnet only.
    /// </summary>
    /// <see cref="BootstrapOptions"/>
    public BootstrapOptions Bootstrap { get; set; } = new();

    /// <summary>
    /// Tor: onion peers through Tor's SOCKS5 port and our own onion service, from <c>Node:Tor</c>; off by default.
    /// </summary>
    /// <see cref="TorOptions"/>
    public TorOptions Tor { get; set; } = new();

    /// <summary>
    /// Addresses/Interfaces to listen on for incoming connections
    /// </summary>
    /// <remarks>
    /// <c>ip:port</c> for IPv4, <c>[ipv6]:port</c> for IPv6 (e.g. <c>[::]:9735</c>), or a bare IP address, which takes
    /// the default port 9735 (NL-107); the wildcard <c>::</c> listens dual-stack.
    /// </remarks>
    public List<string> ListenAddresses { get; set; } = ["127.0.0.1:9735"];

    /// <summary>
    /// List of Features the node offers/expects to/from peers
    /// </summary>
    /// <see cref="FeatureOptions"/>
    public FeatureOptions Features { get; set; } = new();

    /// <summary>
    /// Network timeout
    /// </summary>
    public TimeSpan NetworkTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public bool MustTrimHtlcOutputs { get; set; }

    /// <summary>
    /// The <see cref="LightningMoney"/> members (<see cref="DustLimitAmount"/>, <see cref="HtlcMinimumAmount"/>,
    /// <see cref="MinimumChannelSize"/>), which the configuration cannot set: a <see cref="LightningMoney"/> has no
    /// settable members, so the binder leaves them unchanged, and the daemon refuses a file that names one (NL-338).
    /// </summary>
    public static readonly IReadOnlyList<string> UnboundMoneyKeys =
        [nameof(DustLimitAmount), nameof(HtlcMinimumAmount), nameof(MinimumChannelSize)];

    public LightningMoney DustLimitAmount { get; set; } = LightningMoney.Satoshis(354);

    public ulong DefaultCltvExpiry { get; set; }

    public bool HasAnchorOutputs { get; set; }

    public ushort MaxAcceptedHtlcs { get; set; } = 5;
    public LightningMoney HtlcMinimumAmount { get; set; } = LightningMoney.Satoshis(1);
    public uint Locktime { get; set; }
    public ushort ToSelfDelay { get; set; } = 144;

    /// <summary>
    /// The default of <see cref="MaxAcceptedToSelfDelay"/>: 2016 blocks (two weeks), LND's and LDK's limit.
    /// </summary>
    public const ushort DefaultMaxAcceptedToSelfDelay = 2016;

    /// <summary>
    /// The largest <c>to_self_delay</c> we accept from a peer in <c>open_channel</c>, <c>accept_channel</c>,
    /// <c>open_channel2</c> and <c>accept_channel2</c> (BOLT 2: the receiver may fail a delay it considers unreasonably
    /// large). It binds our own funds on the peer's commitment, so it is independent of the <see cref="ToSelfDelay"/>
    /// we ask of the peer (NL-550: Eclair asks for 720 by default, LND up to 2016 for large channels).
    /// </summary>
    /// <remarks>Configuration key <c>Node:MaxAcceptedToSelfDelay</c>; must be positive.</remarks>
    public ushort MaxAcceptedToSelfDelay { get; set; } = DefaultMaxAcceptedToSelfDelay;

    public uint AllowUpToPercentageOfChannelFundsInFlight { get; set; } = 80;

    /// <summary>
    /// The default of <see cref="MinAcceptedMaxHtlcValueInFlightPercent"/>: 1 %.
    /// </summary>
    public const uint DefaultMinAcceptedMaxHtlcValueInFlightPercent = 1;

    /// <summary>
    /// The smallest <c>max_htlc_value_in_flight_msat</c> we accept from a peer, as a percentage of the channel
    /// (BOLT 2: the receiver may fail a limit it considers too small), in v1 and v2 opens alike (NL-552). The peer's
    /// limit only caps what we can offer it at once, so the floor is low: LDK offers 10 % by default, Eclair 45 %.
    /// 0 accepts any limit.
    /// </summary>
    /// <remarks>Configuration key <c>Node:MinAcceptedMaxHtlcValueInFlightPercent</c>; 0 to 100.</remarks>
    public uint MinAcceptedMaxHtlcValueInFlightPercent { get; set; } = DefaultMinAcceptedMaxHtlcValueInFlightPercent;

    /// <summary>
    /// The default of <see cref="MaxAcceptedChannelReservePercent"/>: 10 %.
    /// </summary>
    public const uint DefaultMaxAcceptedChannelReservePercent = 10;

    /// <summary>
    /// The largest <c>channel_reserve_satoshis</c> we accept from a peer in <c>open_channel</c> and
    /// <c>accept_channel</c>, as a percentage of the channel; a reserve up to 1,000 sat is always accepted (LDK's
    /// minimum). BOLT 2 lets the receiver fail a reserve it considers unreasonably large; the peer's reserve is what we
    /// must keep on our side. NL-562: the old 1.2 x our own 1 % refused LDK's 1,000 sat on channels under ~84k sat;
    /// LND refuses above 20 %.
    /// </summary>
    /// <remarks>Configuration key <c>Node:MaxAcceptedChannelReservePercent</c>; 0 to 100.</remarks>
    public uint MaxAcceptedChannelReservePercent { get; set; } = DefaultMaxAcceptedChannelReservePercent;

    /// <summary>
    /// The default of <see cref="MinCommitmentFeeRatePerKw"/>: 275 sat/kw (1.1 sat/vB).
    /// </summary>
    public const uint DefaultMinCommitmentFeeRatePerKw = 275;

    /// <summary>
    /// The lowest commitment feerate we offer when we pick it from the fee estimate for a channel we fund: the
    /// <c>feerate_per_kw</c> of <c>open_channel</c>, the <c>commitment_feerate_perkw</c> of <c>open_channel2</c> and
    /// the <c>update_fee</c> we send. It keeps a margin above BOLT 3's 253 sat/kw floor, because LDK refuses a
    /// feerate below its own low estimate (its 1,008-block estimate, e.g. 254 sat/kw: "Peer's feerate much too low",
    /// NL-564). A feerate the operator names explicitly is used as given. What we accept from peers is unchanged:
    /// anything from 253 sat/kw (NL-289).
    /// </summary>
    /// <remarks>Configuration key <c>Node:MinCommitmentFeeRatePerKw</c>; at least 253.</remarks>
    public uint MinCommitmentFeeRatePerKw { get; set; } = DefaultMinCommitmentFeeRatePerKw;

    /// <summary>
    /// The commitment feerate we offer for a fee estimate on a channel we fund: the estimate, at least
    /// <see cref="MinCommitmentFeeRatePerKw"/> (and never below 253 sat/kw; NL-564).
    /// </summary>
    public uint GetCommitmentFeeRatePerKw(long estimatePerKw)
    {
        var floor = Math.Max(MinCommitmentFeeRatePerKw, FeeUpdateOptions.FeeratePerKwFloor);
        return (uint)Math.Clamp(estimatePerKw, floor, uint.MaxValue);
    }

    public uint MinimumDepth { get; set; } = 3;
    public LightningMoney MinimumChannelSize { get; set; } = LightningMoney.Satoshis(20_000);

    /// <summary>
    /// Allows HTLCs (payments and forwarding) on our channels. Unset (the default) means on, on every network
    /// including mainnet: the node fails channels on chain (BOLT2 plan N9-T4) and resolves every output of a
    /// non-anchor commitment (BOLT 5 plan O2-O6, O8), so the mainnet gate (BOLT 5 plan O6-T4, NL-094) is open. Set it
    /// to false to refuse every HTLC; read the effective value from <see cref="HtlcsEnabled"/>.
    /// </summary>
    /// <remarks>Configuration key <c>Node:EnableHtlcs</c>. Until O6-T4 (gossip wave G-D) unset meant regtest only.
    /// </remarks>
    public bool? EnableHtlcs { get; set; }

    /// <summary>
    /// The effective HTLC switch: <see cref="EnableHtlcs"/> when it is set, otherwise true (every network).
    /// </summary>
    public bool HtlcsEnabled => EnableHtlcs ?? true;

    /// <summary>
    /// Wait before the first reconnection attempt to a peer with active channels that dropped or could not be reached
    /// at startup. It doubles after every failed attempt, up to <see cref="ReconnectMaxDelay"/>.
    /// </summary>
    /// <remarks>Configuration key <c>Node:ReconnectInitialDelay</c> (a <see cref="TimeSpan"/>, e.g. <c>"00:00:01"</c>).
    /// Tests use a short value.</remarks>
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest wait between two reconnection attempts.
    /// </summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Our forwarding policy and invoice defaults.
    /// </summary>
    /// <see cref="RoutingOptions"/>
    public RoutingOptions Routing { get; set; } = new();

    /// <summary>
    /// Our <c>update_fee</c> policy for the channels we fund (BOLT2 plan N9-T1).
    /// </summary>
    /// <see cref="FeeUpdateOptions"/>
    public FeeUpdateOptions FeeUpdates { get; set; } = new();

    /// <summary>The shutdown behavior beyond the IPC command (NL-592).</summary>
    public ShutdownOptions Shutdown { get; set; } = new();

    /// <summary>
    /// The on-chain wallet reserve kept for <c>option_anchors</c> channels (NL-379), from <c>Node:Anchors</c>.
    /// </summary>
    /// <see cref="AnchorReserveOptions"/>
    public AnchorReserveOptions Anchors { get; set; } = new();

    /// <summary>
    /// Spontaneous (keysend) payments, from <c>Node:Keysend</c> (lane lh1-l3).
    /// </summary>
    /// <see cref="KeysendOptions"/>
    public KeysendOptions Keysend { get; set; } = new();

    /// <summary>
    /// The quiescence timeouts (BOLT 2 "Channel Quiescence"; splicing plan Q1-T5), from <c>Node:Quiescence</c>.
    /// </summary>
    /// <see cref="QuiescenceOptions"/>
    public QuiescenceOptions Quiescence { get; set; } = new();

    /// <summary>The longest <see cref="Alias"/> in UTF-8 bytes (the <c>alias</c> field of <c>node_announcement</c>).</summary>
    public const int AliasMaxBytes = 32;

    /// <summary>The default <see cref="Color"/> (LND's).</summary>
    public const string DefaultColor = "3399ff";

    /// <summary>
    /// The alias of our <c>node_announcement</c> (BOLT 7; plan G1-T6): UTF-8, at most <see cref="AliasMaxBytes"/>
    /// bytes, zero padded on the wire. Empty (the default) announces an all-zero alias. Configuration key
    /// <c>Node:Alias</c>.
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// The <c>rgb_color</c> of our <c>node_announcement</c>: six hex digits <c>RRGGBB</c>, an optional leading
    /// <c>#</c>. Configuration key <c>Node:Color</c>; default <see cref="DefaultColor"/>.
    /// </summary>
    public string Color { get; set; } = DefaultColor;

    /// <summary>
    /// <see cref="Color"/> as the three <c>rgb_color</c> bytes (red, green, blue).
    /// </summary>
    /// <exception cref="FormatException">The color is not six hex digits.</exception>
    public byte[] GetColorBytes()
    {
        var hex = (Color ?? string.Empty).Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            throw new FormatException($"Node:Color '{Color}' is not six hex digits (RRGGBB).");

        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// The default <see cref="MaxDustHtlcExposureMsat"/>: 50,000 sat, as CLN and Eclair.
    /// </summary>
    public const ulong DefaultMaxDustHtlcExposureMsat = 50_000_000;

    /// <summary>
    /// BOLT 2 <c>max_dust_htlc_exposure_msat</c>: the most (msat) that trimmed HTLCs, which would go to miners if the
    /// channel closed on chain, may hold on either commitment of a channel (BOLT2 plan N9-T3, NL-254). We don't offer an
    /// HTLC that would push a commitment over it (the forward then fails upstream with
    /// <c>temporary_channel_failure</c>), we fail an incoming trimmed HTLC that pushed one over it once it is locked in
    /// (no preimage is revealed), and we don't raise the feerate of a channel without <c>option_anchors</c> when that
    /// would trim HTLCs over it. Null (an empty configuration value) disables the check.
    /// </summary>
    /// <remarks>Configuration key <c>Node:MaxDustHtlcExposureMsat</c>. The value is stored with a channel's first
    /// commitment state and kept with it (NL-242); a snapshot stored without one (taken before this option existed)
    /// runs under this value from its load on (NL-290), and its next state save stores it, so the engine's send-side
    /// rules (B2-DUST-03/04) bound its dust offers too.</remarks>
    public ulong? MaxDustHtlcExposureMsat { get; set; } = DefaultMaxDustHtlcExposureMsat;

    /// <summary>
    /// Returns every configuration error of the options this class owns (currently <see cref="Routing"/>,
    /// <see cref="FeeUpdates"/>, <see cref="Anchors"/>, <see cref="Bootstrap"/>, <see cref="CustomSignet"/>, the reconnect delays, the accepted open limits, <see cref="Alias"/> and <see cref="Color"/>); empty when valid. Feature errors are reported by
    /// <see cref="FeatureOptions.GetValidationErrors"/>.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (ReconnectInitialDelay <= TimeSpan.Zero)
            errors.Add($"{nameof(ReconnectInitialDelay)} must be positive.");
        if (ReconnectMaxDelay < ReconnectInitialDelay)
            errors.Add($"{nameof(ReconnectMaxDelay)} must be at least {nameof(ReconnectInitialDelay)}.");
        if (MaxAcceptedToSelfDelay == 0)
            errors.Add($"{nameof(MaxAcceptedToSelfDelay)} must be positive.");
        if (MinAcceptedMaxHtlcValueInFlightPercent > 100)
            errors.Add($"{nameof(MinAcceptedMaxHtlcValueInFlightPercent)} must be at most 100.");
        if (MaxAcceptedChannelReservePercent > 100)
            errors.Add($"{nameof(MaxAcceptedChannelReservePercent)} must be at most 100.");
        if (MinCommitmentFeeRatePerKw < FeeUpdateOptions.FeeratePerKwFloor)
            errors.Add($"{nameof(MinCommitmentFeeRatePerKw)} must be at least {FeeUpdateOptions.FeeratePerKwFloor} sat/kw.");

        if (Encoding.UTF8.GetByteCount(Alias ?? string.Empty) > AliasMaxBytes)
            errors.Add($"{nameof(Alias)} must be at most {AliasMaxBytes} UTF-8 bytes.");
        try
        {
            _ = GetColorBytes();
        }
        catch (FormatException e)
        {
            errors.Add(e.Message);
        }

        errors.AddRange(Routing.GetValidationErrors());
        errors.AddRange(FeeUpdates.GetValidationErrors());
        errors.AddRange(Anchors.GetValidationErrors());
        errors.AddRange(Keysend.GetValidationErrors());
        errors.AddRange(Quiescence.GetValidationErrors());
        errors.AddRange(Bootstrap.GetValidationErrors());
        errors.AddRange(Tor.GetValidationErrors(ListenAddresses));
        // Only an explicit Enabled = true: the mainnet default must never stop a node whose operator raised
        // NetworkTimeout (the bootstrap then waits NetworkTimeout, BootstrapOptions.GetEffectiveConnectTimeout)
        if (Bootstrap.Enabled == true && Bootstrap.ConnectTimeout < NetworkTimeout)
            errors.Add($"Bootstrap:ConnectTimeout ({Bootstrap.ConnectTimeout}) must be at least NetworkTimeout "
                     + $"({NetworkTimeout}), the TCP connect alone, when bootstrap is enabled.");
        if (CustomSignet is not null)
            errors.AddRange(CustomSignet.GetValidationErrors(BitcoinNetwork));
        return errors;
    }
}

/// <summary>
/// How long a channel may stay quiescing or quiescent (BOLT 2 "Channel Quiescence"; splicing plan §3.2, Q1-T5). Bound
/// from the <c>Node:Quiescence</c> configuration section (it is <see cref="NodeOptions.Quiescence"/>).
/// </summary>
/// <remarks>
/// Both are counted from the moment the channel started quiescing (the first <c>stfu</c> sent or received, or our own
/// request queued before it). When they pass, the connection is closed (a <c>warning</c>, then a disconnect; our
/// reconnect loop brings it back and the reconnection ends the quiescence, Q-R-04).
/// </remarks>
public class QuiescenceOptions
{
    /// <summary>
    /// BOLT 2 Q-R-03: "MUST disconnect after 60 seconds of quiescence if the HTLCs are pending". The default is the
    /// spec's 60 s (LND's <c>htlcswitch.quiescencetimeout</c> has the same default).
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Without HTLCs pending BOLT 2 sets no limit; we still disconnect after this long so a peer that never finishes
    /// (or never answers our <c>stfu</c>) cannot freeze the channel (a MAY of ours, not in the spec). Default 5 min.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The configuration errors, empty when valid (<see cref="NodeOptions.GetValidationErrors"/>).
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (Timeout <= TimeSpan.Zero)
            errors.Add($"Quiescence:{nameof(Timeout)} must be positive.");
        else if (Timeout > TimeSpan.FromSeconds(60))
            errors.Add($"Quiescence:{nameof(Timeout)} is {Timeout}; BOLT 2 requires a disconnect after at most 60 "
                     + "seconds of quiescence with HTLCs pending.");
        if (IdleTimeout < Timeout)
            errors.Add($"Quiescence:{nameof(IdleTimeout)} must be at least {nameof(Timeout)}.");
        return errors;
    }
}