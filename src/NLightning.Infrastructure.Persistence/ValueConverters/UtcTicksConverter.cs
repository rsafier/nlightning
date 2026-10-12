using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NLightning.Infrastructure.Persistence.ValueConverters;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as its UTC ticks (<c>long</c>), read back with a zero offset.
/// </summary>
/// <remarks>
/// The same column type on every provider, exact to the tick, and orderable on SQLite (whose EF provider cannot order
/// or compare <see cref="DateTimeOffset"/> columns); Npgsql would also refuse a non-UTC offset for <c>timestamptz</c>.
/// The instant survives, the original offset does not (<see cref="DateTimeOffset"/> equality compares instants).
/// </remarks>
public class UtcTicksConverter()
    : ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));