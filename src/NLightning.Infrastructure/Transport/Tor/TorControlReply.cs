using System.Text.RegularExpressions;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// One reply of Tor's control port (control-spec §2.3): the status code and the text of every line, mid lines
/// (<c>250-</c>), data lines (<c>250+</c>, with their data block appended as further lines) and the end line
/// (<c>250 </c>), without the code and separator.
/// </summary>
/// <param name="Status">The three-digit status (250 is success, 5xx an error).</param>
/// <param name="Lines">The lines' text in order.</param>
/// <remarks>
/// <see cref="ToString"/> (what exception messages and logs show) hides every onion service private key: the
/// <c>PrivateKey=</c> value of an <c>ADD_ONION</c> reply and any <c>ED25519-V3:</c> blob (NL-581).
/// </remarks>
public sealed partial record TorControlReply(int Status, IReadOnlyList<string> Lines)
{
    private const string Redacted = "[redacted]";

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
    public override string ToString() => Redact($"{Status} {string.Join(" | ", Lines)}");

    /// <summary>
    /// <paramref name="text"/> with every <c>PrivateKey=</c> value and <c>ED25519-V3:</c> key blob replaced (NL-581).
    /// </summary>
    public static string Redact(string text)
    {
        text = PrivateKeyRegex().Replace(text, "${prefix}" + Redacted);
        return KeyBlobRegex().Replace(text, "${prefix}" + Redacted);
    }

    [GeneratedRegex(@"(?<prefix>PrivateKey=)[^\s|]*", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?<prefix>ED25519-V3:)(?!\[redacted\])[^\s|]*", RegexOptions.IgnoreCase)]
    private static partial Regex KeyBlobRegex();
}