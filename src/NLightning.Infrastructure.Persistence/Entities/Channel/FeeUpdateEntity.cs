// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// One fee update of the commitment state machine (<c>FeeUpdate</c>): sequence 0 is the opening feerate.
/// </summary>
public class FeeUpdateEntity
{
    /// <summary>
    /// The channel the fee update belongs to.
    /// </summary>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// Strictly increasing per channel; 0 is the opening feerate.
    /// </summary>
    public required ulong Sequence { get; set; }

    /// <summary>
    /// The feerate in satoshi per 1000 weight units.
    /// </summary>
    public required uint FeeratePerKw { get; set; }

    /// <summary>
    /// The <c>HtlcState</c> (add half of the HTLC machine, owned by the funder).
    /// </summary>
    public required byte State { get; set; }

    /// <summary>
    /// Default constructor for EF Core.
    /// </summary>
    internal FeeUpdateEntity()
    {
    }
}