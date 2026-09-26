namespace NLightning.Domain.Channels.ValueObjects;

using Bitcoin.ValueObjects;
using Money;

/// <summary>
/// The channel parameters one party announced in its <c>open_channel</c> or <c>accept_channel</c> (BOLT 2).
/// </summary>
/// <remarks>
/// Each value keeps the meaning it has on the wire of the party that sent it:
/// <list type="bullet">
/// <item><see cref="DustLimitAmount"/> applies to this party's own commitment transaction.</item>
/// <item><see cref="ChannelReserveAmount"/> is what this party requires the <b>other</b> party to keep.</item>
/// <item><see cref="HtlcMinimumAmount"/>, <see cref="MaxAcceptedHtlcs"/> and <see cref="MaxHtlcValueInFlight"/> bind
/// the HTLCs the other party offers to this party.</item>
/// <item><see cref="ToSelfDelay"/> is the delay this party imposes on the <b>other</b> party's <c>to_local</c> output,
/// so it is used when building the other party's commitment transaction.</item>
/// </list>
/// </remarks>
public readonly record struct ChannelParty
{
    /// <summary>The dust limit of this party's commitment transaction.</summary>
    public LightningMoney DustLimitAmount { get; }

    /// <summary>The reserve this party requires the other party to keep.</summary>
    public LightningMoney ChannelReserveAmount { get; }

    /// <summary>The smallest HTLC this party accepts from the other party.</summary>
    public LightningMoney HtlcMinimumAmount { get; }

    /// <summary>The most HTLCs the other party may offer to this party at once.</summary>
    public ushort MaxAcceptedHtlcs { get; }

    /// <summary>The largest total value of HTLCs the other party may have offered to this party at once.</summary>
    public LightningMoney MaxHtlcValueInFlight { get; }

    /// <summary>The CSV delay this party imposes on the other party's <c>to_local</c> output.</summary>
    public ushort ToSelfDelay { get; }

    /// <summary>The upfront shutdown script this party announced, if any.</summary>
    public BitcoinScript? UpfrontShutdownScript { get; }

    public ChannelParty(LightningMoney dustLimitAmount, LightningMoney channelReserveAmount,
                        LightningMoney htlcMinimumAmount, ushort maxAcceptedHtlcs,
                        LightningMoney maxHtlcValueInFlight, ushort toSelfDelay,
                        BitcoinScript? upfrontShutdownScript = null)
    {
        DustLimitAmount = dustLimitAmount;
        ChannelReserveAmount = channelReserveAmount;
        HtlcMinimumAmount = htlcMinimumAmount;
        MaxAcceptedHtlcs = maxAcceptedHtlcs;
        MaxHtlcValueInFlight = maxHtlcValueInFlight;
        ToSelfDelay = toSelfDelay;
        UpfrontShutdownScript = upfrontShutdownScript;
    }

    /// <summary>
    /// The parameters of a party we have not heard from yet (the peer, before its <c>accept_channel</c>).
    /// </summary>
    public static ChannelParty Unknown => new(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero, 0,
                                              LightningMoney.Zero, 0);
}