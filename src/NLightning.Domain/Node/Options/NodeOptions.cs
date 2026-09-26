namespace NLightning.Domain.Node.Options;

using Money;
using Protocol.Constants;
using Protocol.ValueObjects;

public class NodeOptions
{
    // private FeatureOptions _features;

    /// <summary>
    /// The network to connect to. Can be "mainnet", "testnet", or "regtest"
    /// </summary>
    public BitcoinNetwork BitcoinNetwork { get; set; } = NetworkConstants.Mainnet;

    /// <summary>
    /// True if NLTG should run in Daemon mode (background)
    /// </summary>
    public bool Daemon { get; set; }

    /// <summary>
    /// A list of dns seed servers to connect to
    /// </summary>
    public List<string> DnsSeedServers { get; set; } = ["nlseed.nlightn.ing"];

    /// <summary>
    /// Addresses/Interfaces to listen on for incoming connections
    /// </summary>
    /// <remarks>
    /// Addresses should be in the format "ip:port" or "hostname:port"
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

    public LightningMoney DustLimitAmount { get; set; } = LightningMoney.Satoshis(354);

    public ulong DefaultCltvExpiry { get; set; }

    public bool HasAnchorOutputs { get; set; }

    public ushort MaxAcceptedHtlcs { get; set; } = 5;
    public LightningMoney HtlcMinimumAmount { get; set; } = LightningMoney.Satoshis(1);
    public uint Locktime { get; set; }
    public ushort ToSelfDelay { get; set; } = 144;
    public uint AllowUpToPercentageOfChannelFundsInFlight { get; set; } = 80;
    public uint MinimumDepth { get; set; } = 3;
    public LightningMoney MinimumChannelSize { get; set; } = LightningMoney.Satoshis(20_000);

    /// <summary>
    /// Allows HTLCs (payments and forwarding) on our channels. Unset (the default) means "on for regtest only":
    /// until the node can fail a channel on chain (BOLT2 plan N9-T4) and sweep it (BOLT 5, NL-094), real funds must
    /// not sit in HTLCs. Set it explicitly to override; read the effective value from <see cref="HtlcsEnabled"/>.
    /// </summary>
    /// <remarks>Configuration key <c>Node:EnableHtlcs</c>.</remarks>
    public bool? EnableHtlcs { get; set; }

    /// <summary>
    /// The effective HTLC switch: <see cref="EnableHtlcs"/> when it is set, otherwise true only on regtest. It is
    /// computed on every read, so it follows a <see cref="BitcoinNetwork"/> set after binding (the daemon's
    /// <c>PostConfigure</c>).
    /// </summary>
    public bool HtlcsEnabled =>
        EnableHtlcs ?? string.Equals(BitcoinNetwork.Name, NetworkConstants.Regtest, StringComparison.OrdinalIgnoreCase);

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
    /// commitment state and kept with it (NL-242); channels whose state has none use this value for the receive and fee
    /// checks.</remarks>
    public ulong? MaxDustHtlcExposureMsat { get; set; } = DefaultMaxDustHtlcExposureMsat;

    /// <summary>
    /// Returns every configuration error of the options this class owns (currently <see cref="Routing"/>,
    /// <see cref="FeeUpdates"/> and the reconnect delays); empty when valid. Feature errors are reported by
    /// <see cref="FeatureOptions.GetValidationErrors"/>.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (ReconnectInitialDelay <= TimeSpan.Zero)
            errors.Add($"{nameof(ReconnectInitialDelay)} must be positive.");
        if (ReconnectMaxDelay < ReconnectInitialDelay)
            errors.Add($"{nameof(ReconnectMaxDelay)} must be at least {nameof(ReconnectInitialDelay)}.");

        errors.AddRange(Routing.GetValidationErrors());
        errors.AddRange(FeeUpdates.GetValidationErrors());
        return errors;
    }
}