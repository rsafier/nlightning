using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Services;

/// <summary>
/// Stops the host once a <c>shutdown</c> (NL-591) was accepted and its answer is on its way to the client.
/// </summary>
/// <remarks>
/// The <c>shutdown</c> handler only calls <see cref="RequestStop"/>; <see cref="Ipc.NamedPipeIpcService"/> calls
/// <see cref="StopIfRequested"/> after it wrote the response of a request, so the client gets its answer before the
/// IPC server goes down with the rest of the node (<c>NltgDaemonService.StopAsync</c>).
/// </remarks>
internal sealed class NodeShutdownTrigger
{
    private readonly IHostApplicationLifetime? _lifetime;
    private int _requested;
    private int _stopped;

    /// <param name="lifetime">The host's lifetime; without one (a node built without a host) nothing is stopped.</param>
    public NodeShutdownTrigger(IHostApplicationLifetime? lifetime)
    {
        _lifetime = lifetime;
    }

    /// <summary>True once a shutdown was accepted.</summary>
    public bool IsStopRequested => Volatile.Read(ref _requested) == 1;

    /// <summary>Marks the shutdown as accepted; the host stops after the answer is written.</summary>
    public void RequestStop() => Volatile.Write(ref _requested, 1);

    /// <summary>Stops the host, once, if a shutdown was accepted.</summary>
    public void StopIfRequested()
    {
        if (!IsStopRequested || Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        _lifetime?.StopApplication();
    }
}