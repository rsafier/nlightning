namespace NLightning.Domain.Accounting.Financial.Reports;

using Channels.Enums;
using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// The weights of the risk-weighted capital view (plan §6.2 "Audit", NL-602 A3-T6): how much of each settlement state
/// counts as ours. Each is a fraction from 0 to 1.
/// </summary>
/// <remarks>
/// The remote balance is never counted (it is the peer's), and neither is an incoming HTLC whose preimage we do not
/// know (it is not ours until we can claim it). An outgoing HTLC the peer fulfilled is paid: it counts 0.
/// </remarks>
public sealed record AccountingRiskWeights
{
    /// <summary>The default weights.</summary>
    public static AccountingRiskWeights Default { get; } = new();

    /// <summary>Wallet outputs with enough confirmations.</summary>
    public decimal WalletConfirmed { get; init; } = 1m;

    /// <summary>Wallet outputs not confirmed yet (a deposit can still be replaced).</summary>
    public decimal WalletUnconfirmed { get; init; } = 0.9m;

    /// <summary>Our local balance less our in-flight HTLCs (what a force close would give us, before fees).</summary>
    public decimal ChannelSettled { get; init; } = 1m;

    /// <summary>Our offered HTLCs not resolved yet: they come back on a failure and leave on a success.</summary>
    public decimal OutgoingInFlight { get; init; } = 0.5m;

    /// <summary>Incoming HTLCs whose preimage we hold: ours unless we fail to claim them in time.</summary>
    public decimal IncomingWithPreimage { get; init; } = 0.99m;

    /// <summary>Our outputs of force closes waiting for their delay or our sweep.</summary>
    public decimal PendingOnchain { get; init; } = 0.95m;

    /// <summary>HTLC outputs of force closes that either side may still take.</summary>
    public decimal PendingHtlcOnchain { get; init; } = 0.5m;

    /// <summary>Outputs we may take that the books book only once claimed (the peer's HTLCs, a breach's outputs).</summary>
    public decimal PendingUncounted { get; init; } = 0m;
}

/// <summary>
/// The node's capital by settlement state, each weighted by how sure it is ours (plan §6.2 "Audit", NL-602 A3-T6), from
/// a live snapshot (<c>INodeSnapshotSource</c>). Nothing here is booked: it is a reading.
/// </summary>
/// <param name="TakenAt">When the snapshot was taken.</param>
/// <param name="BlockHeight">The chain monitor's height then.</param>
/// <param name="Weights">The weights used.</param>
/// <param name="Lines">One line per settlement state with an amount, in a fixed order (wallet, channels, on chain).</param>
/// <param name="Channels">Per channel: its weighted capital.</param>
public sealed record AccountingRiskCapitalReport(
    DateTimeOffset TakenAt,
    uint BlockHeight,
    AccountingRiskWeights Weights,
    IReadOnlyList<AccountingRiskCapitalLine> Lines,
    IReadOnlyList<AccountingRiskCapitalChannel> Channels)
{
    /// <summary>The price of the fiat values, or null.</summary>
    public AccountingReportPrice? Price { get; init; }

    /// <summary>Every amount at full value (the remote balances and the incoming HTLCs without a preimage excluded).
    /// </summary>
    public long GrossMsat => Lines.Where(l => l.Weight > 0m).Sum(l => l.AmountMsat);

    /// <summary>The weighted sum, msat (rounded down to whole msat per line).</summary>
    public long WeightedMsat => Lines.Sum(l => l.WeightedMsat);

    /// <summary><see cref="WeightedMsat"/> at <see cref="Price"/>, exact.</summary>
    public decimal? WeightedFiat => Price is { } price ? AccountingFiat.Value(WeightedMsat, price.PricePerBitcoin) : null;

    /// <summary>The peers' balances (excluded; shown for reference).</summary>
    public long ExcludedRemoteMsat { get; init; }
}

/// <summary>One settlement state of the risk-weighted view.</summary>
/// <param name="State">The state's name (<c>wallet-confirmed</c>, <c>channel-settled</c>, ...).</param>
/// <param name="AmountMsat">The amount in that state.</param>
/// <param name="Weight">Its weight.</param>
public sealed record AccountingRiskCapitalLine(string State, long AmountMsat, decimal Weight)
{
    /// <summary><see cref="AmountMsat"/> x <see cref="Weight"/>, rounded down to a whole msat.</summary>
    public long WeightedMsat => (long)Math.Floor(AmountMsat * Weight);
}

/// <summary>One channel of the risk-weighted view.</summary>
public sealed record AccountingRiskCapitalChannel(
    ChannelId ChannelId,
    ShortChannelId? ShortChannelId,
    ChannelState State,
    CompactPubKey? Counterparty,
    long SettledMsat,
    long OutgoingInFlightMsat,
    long OutgoingFulfilledMsat,
    long IncomingWithPreimageMsat,
    long IncomingWithoutPreimageMsat,
    long RemoteMsat,
    long PendingOnchainMsat,
    long PendingHtlcOnchainMsat,
    long PendingUncountedMsat,
    long WeightedMsat);