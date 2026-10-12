using System.Net.Sockets;

namespace NLightning.Daemon.Provisioning;

/// <summary>
/// The provisioning Unix socket (NL-1349). Its directory is created owner-only (0700) or must already be owner-only,
/// the socket itself is 0600, and a socket file left by a crashed daemon is replaced only when nothing answers on it.
/// The socket file is removed when the endpoint is disposed (after the unlock).
/// </summary>
public sealed class UnixSocketProvisioningEndpoint(string socketPath) : IProvisioningEndpoint
{
    private const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                                                     | UnixFileMode.GroupExecute
                                                                     | UnixFileMode.OtherRead
                                                                     | UnixFileMode.OtherWrite
                                                                     | UnixFileMode.OtherExecute;

    private Socket? _listener;
    private bool _bound;

    public string SocketPath { get; } = Path.GetFullPath(socketPath);
    public string Description => $"Unix socket {SocketPath}";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The provisioning socket needs Unix domain sockets; use "
                                                  + "Node:Startup:Provisioner=Stdin.");

        PrepareDirectory(Path.GetDirectoryName(SocketPath)!);
        RemoveStaleSocket();

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _bound = true;
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
        }
        catch
        {
            listener.Dispose();
            DeleteSocketFile();
            throw;
        }

        _listener = listener;
        return Task.CompletedTask;
    }

    public async Task<ProvisioningConnection?> AcceptAsync(CancellationToken cancellationToken)
    {
        var listener = _listener ?? throw new InvalidOperationException("The endpoint is not started.");
        var socket = await listener.AcceptAsync(cancellationToken);
        var stream = new NetworkStream(socket, ownsSocket: true);
        return new ProvisioningConnection(stream, stream);
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        _listener = null;
        DeleteSocketFile();
        return ValueTask.CompletedTask;
    }

    private static void PrepareDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            return;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                                       | UnixFileMode.UserExecute);
            return;
        }

        var info = new DirectoryInfo(directory);
        if (info.LinkTarget is not null)
            throw new IOException($"The provisioning socket's directory {directory} must not be a symbolic link.");
        if ((info.UnixFileMode & GroupOrOther) != 0)
            throw new IOException($"The provisioning socket's directory {directory} must be owner-only (chmod 700).");
    }

    private void RemoveStaleSocket()
    {
        var info = new FileInfo(SocketPath);
        if (!info.Exists && info.LinkTarget is null)
            return;
        if (info.LinkTarget is not null || info.Length != 0)
            throw new IOException($"{SocketPath} exists and is not a socket; remove it or set Node:Startup:SocketPath.");

        using (var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(SocketPath));
                throw new IOException($"Another process listens on {SocketPath}.");
            }
            catch (SocketException)
            {
                // Nobody answers: the file was left by a daemon that did not stop cleanly
            }
        }

        File.Delete(SocketPath);
    }

    private void DeleteSocketFile()
    {
        if (!_bound)
            return;

        _bound = false;
        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
            // Best effort: a stale socket file is replaced at the next locked start
        }
    }
}