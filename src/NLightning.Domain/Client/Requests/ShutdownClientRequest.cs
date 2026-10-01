namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Stops the node gracefully (<c>ClientCommand.Shutdown</c>, NL-591).
/// </summary>
/// <remarks>
/// Refused while a channel has HTLCs in flight. Otherwise the node refuses new activity from then on
/// (<see cref="Node.Constants.NodeDrain"/>) and the daemon stops once the answer is sent.
/// </remarks>
public sealed class ShutdownClientRequest;