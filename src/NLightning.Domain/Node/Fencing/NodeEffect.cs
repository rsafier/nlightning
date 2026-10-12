namespace NLightning.Domain.Node.Fencing;

/// <summary>An external effect the node write fence is asked about (<see cref="INodeWriteFence.CheckEffectAsync"/>).</summary>
public enum NodeEffect
{
    /// <summary>A message about to be sent to a peer through its outbox.</summary>
    PeerSend,

    /// <summary>A transaction about to be published to the Bitcoin network.</summary>
    Broadcast,

    /// <summary>A signature about to be made with the node's keys.</summary>
    Sign
}