namespace NLightning.Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// Where dropped onion messages are counted (NL-464): one <c>nlightning.onion_messages.dropped</c> counter set, with
/// the <c>reason</c> tag, shared by the onion-message service and the message reader (a 513 that fails to deserialize
/// never reaches the service, NL-444). Implemented by the Application's <c>OnionMessageMetrics</c>.
/// </summary>
/// <remarks>Thread-safe; recording never throws.</remarks>
public interface IOnionMessageDropCounter
{
    /// <summary>
    /// Counts one dropped message for <paramref name="reason"/>.
    /// </summary>
    /// <param name="reason">The drop reason (e.g. <c>malformed</c>, <c>rate_limited</c>).</param>
    void RecordDropped(string reason);
}