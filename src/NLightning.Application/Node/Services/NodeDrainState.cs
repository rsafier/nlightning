namespace NLightning.Application.Node.Services;

using Domain.Node.Interfaces;

/// <summary>
/// The node's drain flag for a graceful shutdown (NL-591); see <see cref="INodeDrainState"/>.
/// </summary>
public sealed class NodeDrainState : INodeDrainState
{
    private int _draining;

    /// <inheritdoc />
    public bool IsDraining => Volatile.Read(ref _draining) == 1;

    /// <inheritdoc />
    public bool TryBeginDrain() => Interlocked.CompareExchange(ref _draining, 1, 0) == 0;

    /// <inheritdoc />
    public void EndDrain() => Volatile.Write(ref _draining, 0);
}