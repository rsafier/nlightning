using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NLightning.Infrastructure.Persistence.ValueConverters;

using Domain.Channels.ValueObjects;

/// <summary>
/// EF Core value converter for the ShortChannelId value object (stored as its 8 wire bytes)
/// </summary>
public class ShortChannelIdConverter()
    : ValueConverter<ShortChannelId, byte[]>(shortChannelId => shortChannelId, bytes => new ShortChannelId(bytes));