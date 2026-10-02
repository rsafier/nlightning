namespace NLightning.Integration.Tests.Fixtures.Cln;

using Docker.Utils;

/// <summary>
/// A CLN a test started next to the fixture's (<see cref="ClnFixture.StartClnAsync"/>). Disposing removes it.
/// </summary>
public abstract class ExtraClnNode : IAsyncDisposable
{
    protected ExtraClnNode(ClnNodeSpec spec, ClnClient client, string nodeId, string host, int port)
    {
        Spec = spec;
        Client = client;
        NodeId = nodeId;
        Host = host;
        Port = port;
    }

    public ClnNodeSpec Spec { get; }

    public string Name => Spec.Name;

    public ClnClient Client { get; }

    /// <summary>Its node id (hex, lower case).</summary>
    public string NodeId { get; }

    /// <summary>The host this process dials it at (stable across <see cref="RestartAsync"/> when restartable).</summary>
    public string Host { get; }

    /// <summary>The p2p port at <see cref="Host"/>.</summary>
    public int Port { get; }

    /// <summary>The <c>pubkey@host:port</c> an in-process node connects to.</summary>
    public string Address => $"{NodeId}@{Host}:{Port}";

    /// <summary>The <c>host:port</c> the fixture's CLN (or another node of the backend) dials it at.</summary>
    public string PeerHost => $"{Name}:{ClnFixture.ClnP2PPort}";

    /// <summary>
    /// Restarts it gracefully (its data kept) and waits until <c>getinfo</c> answers again. Only for a
    /// <see cref="ClnNodeSpec.Restartable"/> node.
    /// </summary>
    public abstract Task RestartAsync(CancellationToken cancellationToken);

    /// <summary>Writes the last <paramref name="tail"/> lines of its log to <see cref="Console"/>.</summary>
    public abstract Task DumpLogAsync(int tail);

    /// <summary>Removes it.</summary>
    public abstract ValueTask DisposeAsync();
}