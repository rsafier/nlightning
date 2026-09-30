namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// One reply of Tor's control port (control-spec §2.3): the status code and the text of every line, mid lines
/// (<c>250-</c>), data lines (<c>250+</c>, with their data block appended as further lines) and the end line
/// (<c>250 </c>), without the code and separator.
/// </summary>
/// <param name="Status">The three-digit status (250 is success, 5xx an error).</param>
/// <param name="Lines">The lines' text in order.</param>
public sealed record TorControlReply(int Status, IReadOnlyList<string> Lines)
{
    /// <summary>True for status 250.</summary>
    public bool IsOk => Status == 250;

    /// <summary>The value of the first <c>KEY=value</c> line whose key is <paramref name="key"/> (case-sensitive, as
    /// Tor writes it); null when absent.</summary>
    public string? GetValue(string key)
    {
        var prefix = key + "=";
        return Lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    /// <inheritdoc />
    public override string ToString() => $"{Status} {string.Join(" | ", Lines)}";
}