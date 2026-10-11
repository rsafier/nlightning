namespace NLightning.Daemon.Provisioning;

/// <summary>
/// Key provisioning over the process's stdin and stdout (NL-1349, <c>Node:Startup:Provisioner=Stdin</c>): one
/// connection, requests read from stdin until it ends, answers written to stdout.
/// </summary>
public sealed class StdioProvisioningEndpoint(Stream input, Stream output) : IProvisioningEndpoint
{
    private bool _accepted;

    public string Description => "stdin";

    public static StdioProvisioningEndpoint FromConsole() =>
        new(Console.OpenStandardInput(), Console.OpenStandardOutput());

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ProvisioningConnection?> AcceptAsync(CancellationToken cancellationToken)
    {
        if (_accepted)
            return Task.FromResult<ProvisioningConnection?>(null);

        _accepted = true;
        return Task.FromResult<ProvisioningConnection?>(new ProvisioningConnection(input, output, NoDispose.Instance));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The console streams stay open: the node keeps writing to stdout after the unlock
    private sealed class NoDispose : IDisposable
    {
        public static readonly NoDispose Instance = new();

        public void Dispose()
        {
        }
    }
}