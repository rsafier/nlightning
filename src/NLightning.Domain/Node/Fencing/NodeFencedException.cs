namespace NLightning.Domain.Node.Fencing;

/// <summary>
/// Thrown by a node write fence when this instance of the node may no longer commit state or act externally.
/// </summary>
public sealed class NodeFencedException : Exception
{
    public NodeFencedException()
    {
    }

    public NodeFencedException(string message) : base(message)
    {
    }

    public NodeFencedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}