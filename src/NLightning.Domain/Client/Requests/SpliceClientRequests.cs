namespace NLightning.Domain.Client.Requests;

using Channels.Splicing.Models;
using Channels.ValueObjects;

/// <summary>
/// Splices wallet funds into a channel (<c>ClientCommand.SpliceIn</c>, splicing plan §3.10). DTO shell of the SP1
/// contracts; the handlers and the CLI are lane SP1-E's.
/// </summary>
public sealed class SpliceInClientRequest
{
    public SpliceInClientRequest(ChannelId channelId, ulong amountSat)
    {
        ChannelId = channelId;
        AmountSat = amountSat;
    }

    public ChannelId ChannelId { get; }

    /// <summary>The amount added to our channel balance, in satoshis.</summary>
    public ulong AmountSat { get; }

    /// <summary>The splice transaction's feerate, or null for the fee service's estimate.</summary>
    public uint? FeeRatePerKw { get; init; }

    /// <summary>The <c>ISpliceService</c> request: a positive contribution.</summary>
    public SpliceRequest ToSpliceRequest() => new(ChannelId, checked((long)AmountSat), FeeRatePerKw);
}

/// <summary>
/// Splices funds out of a channel (<c>ClientCommand.SpliceOut</c>, splicing plan §3.10). DTO shell of the SP1
/// contracts; the handlers and the CLI are lane SP1-E's.
/// </summary>
public sealed class SpliceOutClientRequest
{
    public SpliceOutClientRequest(ChannelId channelId, ulong amountSat)
    {
        ChannelId = channelId;
        AmountSat = amountSat;
    }

    public ChannelId ChannelId { get; }

    /// <summary>The amount taken out of our channel balance, in satoshis.</summary>
    public ulong AmountSat { get; }

    /// <summary>Where the amount goes, or null for a new address of our wallet.</summary>
    public string? Address { get; init; }

    /// <summary>The splice transaction's feerate, or null for the fee service's estimate.</summary>
    public uint? FeeRatePerKw { get; init; }

    /// <summary>The <c>ISpliceService</c> request: a negative contribution.</summary>
    public SpliceRequest ToSpliceRequest() => new(ChannelId, -checked((long)AmountSat), FeeRatePerKw, Address);
}