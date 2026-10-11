namespace NLightning.Daemon.Provisioning;

/// <summary>
/// A transport the key provisioning protocol runs over (NL-1349): the owner-only Unix socket
/// (<see cref="UnixSocketProvisioningEndpoint"/>), stdin/stdout (<see cref="StdioProvisioningEndpoint"/>), and later a
/// vsock listener, all behind the same <see cref="EndpointKeyProvisioner"/>.
/// </summary>
public interface IProvisioningEndpoint : IAsyncDisposable
{
    string Description { get; }

    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the next connection; null when the endpoint will accept no more.</summary>
    Task<ProvisioningConnection?> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>One provisioning connection: requests are read from <see cref="Input"/>, answers written to
/// <see cref="Output"/> (the same stream for a socket).</summary>
public sealed class ProvisioningConnection(Stream input, Stream output, IDisposable? owner = null) : IDisposable
{
    public Stream Input { get; } = input;
    public Stream Output { get; } = output;

    public void Dispose()
    {
        if (owner is not null)
        {
            owner.Dispose();
            return;
        }

        Input.Dispose();
        if (!ReferenceEquals(Input, Output))
            Output.Dispose();
    }
}