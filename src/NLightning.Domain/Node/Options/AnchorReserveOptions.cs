namespace NLightning.Domain.Node.Options;

using Money;

/// <summary>
/// The on-chain wallet reserve we keep for <c>option_anchors</c> channels (NL-379, BOLT 5 plan O7-T4): the CPFP child
/// of our commitment and the fee inputs of our zero-fee HTLC transactions are paid from confirmed wallet outputs, so we
/// keep some for them, as LND does (10,000 sat per anchors channel, 100,000 sat at most). Bound from the
/// <c>Node:Anchors</c> configuration section (it is <see cref="NodeOptions.Anchors"/>).
/// </summary>
/// <remarks>
/// The reserve is <c>min(ReservePerChannel x anchors channels, MaxReserve)</c>. We refuse to open (as initiator) or
/// accept (as fundee) an anchors channel when the confirmed wallet balance does not cover the reserve including the new
/// channel, and a channel funding never takes the wallet below the reserve of the anchors channels (the new one
/// included). Fee-input reservations (<c>IFeeInputSelector</c>, used by the CPFP child and the anchors HTLC
/// transactions) may spend it: that is what it is for.
/// </remarks>
public class AnchorReserveOptions
{
    /// <summary>LND's per-channel reserve, in satoshis.</summary>
    public const ulong DefaultReservePerChannelSat = 10_000;

    /// <summary>LND's cap on the whole reserve, in satoshis.</summary>
    public const ulong DefaultMaxReserveSat = 100_000;

    private const ulong MaxMoneySat = 21_000_000UL * 100_000_000UL;

    /// <summary>
    /// Satoshis kept per <c>option_anchors</c> channel (<c>Node:Anchors:ReservePerChannel</c>). Zero turns the reserve
    /// off.
    /// </summary>
    public ulong ReservePerChannel { get; set; } = DefaultReservePerChannelSat;

    /// <summary>
    /// The most the whole reserve holds, in satoshis, however many anchors channels we have
    /// (<c>Node:Anchors:MaxReserve</c>).
    /// </summary>
    public ulong MaxReserve { get; set; } = DefaultMaxReserveSat;

    /// <summary>
    /// How long an anchors channel still being opened (a temporary channel, as opener or fundee) counts toward the
    /// reserve before it is funded (<c>Node:Anchors:PendingOpenTimeout</c>, default 10 minutes). Past it an abandoned
    /// open stops holding reserve; a funded channel counts as a channel from then on.
    /// </summary>
    public TimeSpan PendingOpenTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The reserve for <paramref name="anchorsChannelCount"/> anchors channels:
    /// <c>min(ReservePerChannel x count, MaxReserve)</c>.
    /// </summary>
    public LightningMoney GetRequiredReserve(int anchorsChannelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(anchorsChannelCount);
        if (anchorsChannelCount == 0 || ReservePerChannel == 0)
            return LightningMoney.Zero;

        // Saturate instead of overflowing: the cap applies anyway
        var count = (ulong)anchorsChannelCount;
        var total = ReservePerChannel > MaxReserve / count ? MaxReserve : ReservePerChannel * count;
        return LightningMoney.Satoshis(Math.Min(total, MaxReserve));
    }

    /// <summary>Returns every configuration error of these options; empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MaxReserve < ReservePerChannel)
            errors.Add($"Anchors:{nameof(MaxReserve)} must be at least Anchors:{nameof(ReservePerChannel)}.");
        if (MaxReserve > MaxMoneySat)
            errors.Add($"Anchors:{nameof(MaxReserve)} is more than 21 million bitcoin.");
        if (PendingOpenTimeout <= TimeSpan.Zero)
            errors.Add($"Anchors:{nameof(PendingOpenTimeout)} must be positive.");
        return errors;
    }
}