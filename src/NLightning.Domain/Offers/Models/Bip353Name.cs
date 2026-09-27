namespace NLightning.Domain.Offers.Models;

/// <summary>
/// The BOLT 12 <c>invreq_bip_353_name</c>: <c>u8 name_len || name || u8 domain_len || domain</c>, kept as raw bytes
/// (the reader checks the character set, <see cref="HasValidCharacters"/>).
/// </summary>
/// <param name="Name">The post-₿, pre-@ part of the human-readable name.</param>
/// <param name="Domain">The post-@ part.</param>
public sealed record Bip353Name(ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Domain)
{
    /// <summary>
    /// BOLT 12 invoice_request reader: <c>name</c> and <c>domain</c> hold only <c>0-9</c>, <c>a-z</c>, <c>A-Z</c>,
    /// <c>-</c>, <c>_</c> and <c>.</c>.
    /// </summary>
    public bool HasValidCharacters => IsValid(Name.Span) && IsValid(Domain.Span);

    private static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b is not ((>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'z')
                          or (>= (byte)'A' and <= (byte)'Z') or (byte)'-' or (byte)'_' or (byte)'.'))
                return false;

        return true;
    }
}