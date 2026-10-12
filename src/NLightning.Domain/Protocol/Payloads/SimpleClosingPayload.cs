namespace NLightning.Domain.Protocol.Payloads;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Interfaces;
using Money;

/// <summary>
/// The fields <c>closing_complete</c> (40) and <c>closing_sig</c> (41) share (BOLT 2 <c>option_simple_close</c>):
/// <c>channel_id</c>, <c>closer_scriptpubkey</c>, <c>closee_scriptpubkey</c>, <c>fee_satoshis</c> and
/// <c>locktime</c>. The signatures travel in the message's <c>closing_tlvs</c>.
/// </summary>
public abstract class SimpleClosingPayload : IChannelMessagePayload
{
    /// <summary>The channel being closed.</summary>
    public ChannelId ChannelId { get; }

    /// <summary>The output script of the side that pays the fee (the sender of <c>closing_complete</c>).</summary>
    public BitcoinScript CloserScriptPubKey { get; }

    /// <summary>The output script of the other side.</summary>
    public BitcoinScript CloseeScriptPubKey { get; }

    /// <summary>The fee the closer pays, in whole satoshis.</summary>
    public LightningMoney FeeSatoshis { get; }

    /// <summary>The <c>nLockTime</c> of the closing transaction.</summary>
    public uint LockTime { get; }

    protected SimpleClosingPayload(ChannelId channelId, BitcoinScript closerScriptPubKey,
                                   BitcoinScript closeeScriptPubKey, LightningMoney feeSatoshis, uint lockTime)
    {
        ArgumentNullException.ThrowIfNull(feeSatoshis);
        if (closerScriptPubKey.Length > ushort.MaxValue || closeeScriptPubKey.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(closerScriptPubKey), "A script is longer than a u16 length");

        ChannelId = channelId;
        CloserScriptPubKey = closerScriptPubKey;
        CloseeScriptPubKey = closeeScriptPubKey;
        FeeSatoshis = feeSatoshis;
        LockTime = lockTime;
    }
}