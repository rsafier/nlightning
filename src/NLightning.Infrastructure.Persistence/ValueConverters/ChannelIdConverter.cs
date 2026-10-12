using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NLightning.Infrastructure.Persistence.ValueConverters;

using Domain.Channels.ValueObjects;

/// <summary>
/// EF Core value converter for ChannelId value object
/// </summary>
/// <remarks>
/// <c>(ChannelId)bytes</c> rather than <c>new ChannelId(bytes)</c>: the constructor takes a <c>ReadOnlySpan</c>, and
/// the compiled-model generator writes that conversion as an explicit <c>op_Implicit</c> call, which does not compile
/// (CS0571, NL-708). Both build the same value.
/// </remarks>
public class ChannelIdConverter()
    : ValueConverter<ChannelId, byte[]>(channelId => channelId, bytes => (ChannelId)bytes);