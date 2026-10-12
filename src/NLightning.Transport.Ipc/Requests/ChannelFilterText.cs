namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;

/// <summary>
/// The channel filter of the list commands as text: a 64-hex-character channel id, or a <c>short_channel_id</c> as
/// <c>block x tx x output</c> (or its decimal BOLT 7 form).
/// </summary>
internal static class ChannelFilterText
{
    /// <summary>
    /// Parses <paramref name="text"/> (null or blank = no filter).
    /// </summary>
    /// <exception cref="ClientException">The text is neither form (<see cref="ErrorCodes.InvalidOperation"/>).</exception>
    public static (ChannelId? ChannelId, ShortChannelId? ShortChannelId) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        // A channel id is 64 hex characters; a short channel id is block x tx x output (or its decimal form)
        if (text.Length == 64)
        {
            try
            {
                return (new ChannelId(Convert.FromHexString(text)), null);
            }
            catch (FormatException)
            {
                throw Invalid();
            }
        }

        if (ulong.TryParse(text, out var scidValue))
            return (null, new ShortChannelId(scidValue));

        try
        {
            return (null, ShortChannelId.Parse(text));
        }
        catch (FormatException)
        {
            throw Invalid();
        }
    }

    private static ClientException Invalid() =>
        new(ErrorCodes.InvalidOperation,
            "Invalid channel: expected a 64-hex channel id or a short_channel_id like 800000x1234x0.");
}