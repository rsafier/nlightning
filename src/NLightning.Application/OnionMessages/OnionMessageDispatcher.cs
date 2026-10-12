namespace NLightning.Application.OnionMessages;

using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// Picks the <see cref="IOnionMessageHandler"/> of a delivered message by its final-hop payload field (BOLT 4: types 64
/// and up; BOLT 12 uses 64, 66 and 68).
/// </summary>
public sealed class OnionMessageDispatcher
{
    private readonly Dictionary<ulong, IOnionMessageHandler> _handlers = new();

    /// <exception cref="ArgumentException">Two handlers claim the same type, or a handler claims a type below 64.
    /// </exception>
    public OnionMessageDispatcher(IEnumerable<IOnionMessageHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        foreach (var handler in handlers)
        {
            foreach (var type in handler.PayloadTypes)
            {
                if (type < OnionMessageConstants.FirstPayloadFieldType)
                    throw new ArgumentException($"Onion message payload fields start at 64, got {type}",
                                                nameof(handlers));
                if (!_handlers.TryAdd(type, handler))
                    throw new ArgumentException($"Two onion message handlers claim payload type {type}",
                                                nameof(handlers));
            }
        }
    }

    /// <summary>The payload types that have a handler.</summary>
    public IReadOnlyCollection<ulong> HandledTypes => _handlers.Keys;

    /// <summary>
    /// The payload field of <paramref name="contents"/> (the one record of type 64 or more), or null when it has none.
    /// </summary>
    /// <remarks>The caller has already ignored messages with more than one payload field.</remarks>
    public static ulong? GetPayloadType(OnionMessageContents contents) =>
        contents.Records.FirstOrDefault(r => r.Type >= OnionMessageConstants.FirstPayloadFieldType)?.Type;

    /// <summary>
    /// The handler for <paramref name="payloadType"/>, or null.
    /// </summary>
    public IOnionMessageHandler? GetHandler(ulong payloadType) =>
        _handlers.GetValueOrDefault(payloadType);
}