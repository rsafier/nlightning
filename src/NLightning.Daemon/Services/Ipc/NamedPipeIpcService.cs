using System.IO.Pipes;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Services.Ipc;

using Contracts.Utilities;
using Daemon.Ipc.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Interfaces;
using Factories;
using Transport.Ipc;

/// <summary>
/// Hosted service that listens to on a named pipe and processes IPC requests using injected components.
/// </summary>
internal sealed class NamedPipeIpcService : INamedPipeIpcService
{
    /// <summary>
    /// How many pipe instances (connected clients) can exist at once. A <c>PayInvoice</c> holds one for its whole wait
    /// (up to <c>PayInvoiceClientHandler.MaxTimeoutSeconds</c>), so the cap must leave room for the short commands
    /// (listchannels, listpayments) polled meanwhile; the CLI gives up connecting after 2 s.
    /// </summary>
    internal const int MaxServerInstances = 64;

    /// <summary>
    /// How long a client has to send its request, before it is authenticated. A connection that sends nothing (or
    /// trickles bytes) would otherwise hold one of the <see cref="MaxServerInstances"/> instances forever.
    /// </summary>
    internal static TimeSpan DefaultRequestReadTimeout { get; } = TimeSpan.FromSeconds(30);

    private readonly ILogger<NamedPipeIpcService> _logger;
    private readonly IIpcAuthenticator _authenticator;
    private readonly IIpcFraming _framing;
    private readonly IIpcRequestRouter _router;
    private readonly string _pipeName;
    private readonly string _cookiePath;

    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    /// <summary>
    /// See <see cref="DefaultRequestReadTimeout"/>; settable for tests.
    /// </summary>
    internal TimeSpan RequestReadTimeout { get; init; } = DefaultRequestReadTimeout;

    /// <summary>
    /// Stops the host after the answer to an accepted <c>shutdown</c> was written (NL-591); null without one.
    /// </summary>
    internal NodeShutdownTrigger? ShutdownTrigger { get; init; }

    public NamedPipeIpcService(IIpcAuthenticator authenticator, string configPath, IIpcFraming framing,
                               ILogger<NamedPipeIpcService> logger, IIpcRequestRouter router)
    {
        _logger = logger;
        _authenticator = authenticator;
        _framing = framing;
        _router = router;

        _pipeName = NodeUtils.GetNamedPipeFilePath(configPath);
        _cookiePath = NodeUtils.GetCookieFilePath(configPath);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        WriteNewCookie();

        _listenerTask = ListenToIpcClientAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        // Nothing to stop if StartAsync never ran
        if (_cts is null)
            return;

        await _cts.CancelAsync();

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }

        DeleteCookie();
    }

    private async Task ListenToIpcClientAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // CurrentUserOnly: on Unix the socket is created owner-only and a peer running as another user
                    // is refused; on Windows the pipe's ACL grants only the current user. The cookie stays the
                    // authentication; this keeps other local users away from the unauthenticated request parser.
                    var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxServerInstances,
                                                           PipeTransmissionMode.Byte,
                                                           PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    RestrictSocketPermissions();
                    await server.WaitForConnectionAsync(cancellationToken);

                    _ = Task.Run(() => HandleClientAsync(server, cancellationToken), cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "IPC server accept loop error");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("IPC server loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in IPC server loop");
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        var authenticated = false;
        try
        {
            IpcEnvelope request;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(RequestReadTimeout);
                try
                {
                    request = await _framing.ReadAsync(stream, readCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("IPC client sent no request within {Timeout}; closing the connection",
                                       RequestReadTimeout);
                    return;
                }
            }

            if (!await _authenticator.ValidateAsync(request.AuthToken, ct))
            {
                var err = IpcErrorFactory.CreateErrorEnvelope(request, ErrorCodes.AuthenticationFailure,
                                                              "Authentication failed.");
                await _framing.WriteAsync(stream, err, ct);
                return;
            }

            authenticated = true;
            var response = await _router.RouteAsync(request, ct);
            await _framing.WriteAsync(stream, response, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC client handling failed");
            try
            {
                // Try to write a generic error if we still can read an envelope. Only an authenticated client gets
                // the exception's message: before that, whoever opened the pipe could read internals from it.
                var env = new IpcEnvelope { Version = 1, CorrelationId = Guid.NewGuid(), Kind = IpcEnvelopeKind.Error };
                var err = IpcErrorFactory.CreateErrorEnvelope(env, ErrorCodes.ServerError,
                                                              authenticated ? ex.Message : "Invalid request.");
                await _framing.WriteAsync(stream, err, ct);
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            try { await stream.DisposeAsync(); }
            catch
            {
                //ignore
            }

            // After the answer: an accepted shutdown stops the host now (NL-591)
            ShutdownTrigger?.StopIfRequested();
        }
    }

    /// <summary>
    /// Writes a fresh random cookie on every start, so a leaked cookie stops working after a restart.
    /// </summary>
    private void WriteNewCookie()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cookiePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Delete first, so the file is recreated with owner-only permissions
            File.Delete(_cookiePath);

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using var writer = new StreamWriter(_cookiePath, options);
            writer.Write(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write the IPC cookie at {Path}", _cookiePath);
            throw;
        }
    }

    /// <summary>
    /// On Unix the pipe is a socket file created with the umask's mode (often 0755); make it owner-only (0600), like
    /// the cookie. .NET recreates the socket when the last server instance goes away, so this runs for every instance.
    /// </summary>
    private void RestrictSocketPermissions()
    {
        if (OperatingSystem.IsWindows() || !File.Exists(_pipeName))
            return;

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            if (File.GetUnixFileMode(_pipeName) != ownerOnly)
                File.SetUnixFileMode(_pipeName, ownerOnly);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restrict the IPC socket at {Path} to its owner", _pipeName);
        }
    }

    private void DeleteCookie()
    {
        try
        {
            File.Delete(_cookiePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the IPC cookie at {Path}", _cookiePath);
        }
    }
}