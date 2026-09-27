namespace NLightning.Transport.Ipc;

using Domain.Payments.Keysend;

/// <summary>
/// Keysend custom records on the IPC wire: a map from TLV type to value bytes (lane lh1-l3).
/// </summary>
public static class CustomRecordsIpc
{
    /// <summary>The records as a map, or null when there are none.</summary>
    public static Dictionary<ulong, byte[]>? FromRecords(IReadOnlyList<CustomRecord>? records) =>
        records is not { Count: > 0 }
            ? null
            : records.ToDictionary(record => record.Type, record => record.Value.ToArray());

    /// <summary>The map as records, ascending by type (empty for null).</summary>
    public static IReadOnlyList<CustomRecord> ToRecords(Dictionary<ulong, byte[]>? records) =>
        records is null
            ? []
            : records.OrderBy(pair => pair.Key)
                     .Select(pair => new CustomRecord(pair.Key, pair.Value ?? []))
                     .ToList();
}