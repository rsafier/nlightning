namespace NLightning.Domain.Node.Interfaces;

/// <summary>
/// Whether the node is draining for a graceful shutdown (<c>shutdown</c>, NL-591): while it is, no new activity
/// starts (see <see cref="Constants.NodeDrain"/> for what is refused).
/// </summary>
/// <remarks>
/// One instance per node (singleton). A drain that cannot complete (HTLCs still in flight) is ended again with
/// <see cref="EndDrain"/>, and the node goes on as before.
/// </remarks>
public interface INodeDrainState
{
    /// <summary>True from <see cref="TryBeginDrain"/> until <see cref="EndDrain"/>.</summary>
    bool IsDraining { get; }

    /// <summary>Starts draining; false when a drain is already running.</summary>
    bool TryBeginDrain();

    /// <summary>Ends a drain that did not lead to a shutdown.</summary>
    void EndDrain();
}