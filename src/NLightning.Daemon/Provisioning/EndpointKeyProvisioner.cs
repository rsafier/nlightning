using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Provisioning;

using Contracts.Provisioning;

/// <summary>
/// The development key provisioner (NL-1349): reads <see cref="KeyProvisioningProtocol"/> frames from the connections
/// of an <see cref="IProvisioningEndpoint"/>. A <c>status</c> request is answered at once; an <c>unlock</c> request
/// carrying an encrypted key file and its password becomes a <see cref="KeyProvisioningAttempt"/>, and its connection
/// waits for that attempt's answer. Attempts are handed out one at a time; nothing is logged about their content.
/// </summary>
public sealed class EndpointKeyProvisioner : IKeyProvisioner
{
    private const int MaxConnections = 4;

    private readonly IProvisioningEndpoint _endpoint;
    private readonly Func<KeyProvisioningResponse> _status;
    private readonly ILogger _logger;
    private readonly TimeSpan? _requestTimeout;
    private readonly Channel<KeyProvisioningAttempt> _attempts = Channel.CreateUnbounded<KeyProvisioningAttempt>();
    private readonly SemaphoreSlim _connectionSlots = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = [];
    private Task? _acceptLoop;

    /// <param name="endpoint">The transport.</param>
    /// <param name="status">The answer to a status request (the node's state).</param>
    /// <param name="logger">Logs connection problems, never request content.</param>
    /// <param name="requestTimeout">How long a connection may take to send a request; null for no limit (stdin).</param>
    public EndpointKeyProvisioner(IProvisioningEndpoint endpoint, Func<KeyProvisioningResponse> status, ILogger logger,
                                  TimeSpan? requestTimeout)
    {
        _endpoint = endpoint;
        _status = status;
        _logger = logger;
        _requestTimeout = requestTimeout;
    }

    public string Description => _endpoint.Description;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _endpoint.StartAsync(cancellationToken);
        _acceptLoop = Task.Run(AcceptLoopAsync, CancellationToken.None);
    }

    public async Task<KeyProvisioningAttempt?> NextAttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _attempts.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _endpoint.DisposeAsync();

        // A read of the console's stdin does not always observe cancellation: wait a bounded time only
        Task[] pending;
        lock (_connections)
            pending = [.. _connections, _acceptLoop ?? Task.CompletedTask];
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(2)));

        // Attempts nobody took are wiped
        while (_attempts.Reader.TryRead(out var attempt))
            attempt.Dispose();

    }

    private async Task AcceptLoopAsync()
    {
        var token = _stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(token);
                ProvisioningConnection? connection;
                try
                {
                    connection = await _endpoint.AcceptAsync(token);
                }
                catch
                {
                    _connectionSlots.Release();
                    throw;
                }

                if (connection is null)
                {
                    _connectionSlots.Release();
                    break;
                }

                var task = Task.Run(() => HandleConnectionAsync(connection, token), CancellationToken.None);
                lock (_connections)
                {
                    _connections.RemoveAll(t => t.IsCompleted);
                    _connections.Add(task);
                }
            }
        }
        catch (Exception e) when (token.IsCancellationRequested
                               && e is OperationCanceledException or ObjectDisposedException
                                      or System.Net.Sockets.SocketException)
        {
            // Stopping
        }
        catch (Exception e)
        {
            _logger.LogError("The key provisioning endpoint failed ({ExceptionType}): {Message}", e.GetType().Name,
                             e.Message);
        }

        // An endpoint that accepts no more (stdin ended) delivers nothing more once its connections are done
        Task[] connections;
        lock (_connections)
            connections = _connections.ToArray();
        await Task.WhenAll(connections);
        _attempts.Writer.TryComplete();
    }

    private async Task HandleConnectionAsync(ProvisioningConnection connection, CancellationToken token)
    {
        try
        {
            using (connection)
            {
                while (!token.IsCancellationRequested)
                {
                    KeyProvisioningRequest? request;
                    using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        if (_requestTimeout is { } timeout)
                            readTimeout.CancelAfter(timeout);
                        try
                        {
                            request = await KeyProvisioningProtocol.ReadRequestAsync(connection.Input,
                                                                                     readTimeout.Token);
                        }
                        catch (InvalidDataException e)
                        {
                            await KeyProvisioningProtocol.WriteResponseAsync(connection.Output, Refused(e.Message),
                                                                             token);
                            return;
                        }
                    }

                    if (request is null)
                        return;

                    // Null: the attempt's answer was written by the locked start through RespondAsync
                    var response = await HandleRequestAsync(request, connection, token);
                    if (response is not null)
                        await KeyProvisioningProtocol.WriteResponseAsync(connection.Output, response, token);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client left, took too long, or the provisioner stops
        }
        finally
        {
            _connectionSlots.Release();
        }
    }

    private async Task<KeyProvisioningResponse?> HandleRequestAsync(KeyProvisioningRequest request,
                                                                    ProvisioningConnection connection,
                                                                    CancellationToken token)
    {
        if (string.Equals(request.Kind, KeyProvisioningProtocol.StatusKind, StringComparison.Ordinal))
            return _status();

        if (!string.Equals(request.Kind, KeyProvisioningProtocol.UnlockKind, StringComparison.Ordinal))
            return Refused("Unknown request kind.");

        if (!string.Equals(_status().State, KeyProvisioningProtocol.LockedState, StringComparison.Ordinal))
            return Refused("The node is already unlocked.");

        if (!string.Equals(request.Material, KeyProvisioningProtocol.EncryptedKeyFileMaterial,
                           StringComparison.Ordinal))
            return Refused("Unsupported key material; this provisioner takes an encrypted key file.");

        if (string.IsNullOrEmpty(request.KeyFile) || string.IsNullOrEmpty(request.Password))
            return Refused("An unlock request needs the key file and its password.");

        byte[] keyFile;
        try
        {
            keyFile = Convert.FromBase64String(request.KeyFile);
        }
        catch (FormatException)
        {
            return Refused("The key file is not valid base64.");
        }

        // The answer is written before RespondAsync returns, so a success reaches the client before the endpoint
        // closes
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = new KeyProvisioningAttempt(new EncryptedKeyFileMaterial(keyFile, request.Password),
                                                 request.Secrets, async (response, ct) =>
                                                 {
                                                     try
                                                     {
                                                         await KeyProvisioningProtocol.WriteResponseAsync(
                                                             connection.Output, response, ct);
                                                     }
                                                     finally
                                                     {
                                                         answered.TrySetResult();
                                                     }
                                                 });
        if (!_attempts.Writer.TryWrite(attempt))
        {
            attempt.Dispose();
            return Refused("The node is not accepting keys.");
        }

        await using (token.Register(() => answered.TrySetCanceled(token)))
            await answered.Task;
        return null;
    }

    private KeyProvisioningResponse Refused(string error)
    {
        var status = _status();
        return new KeyProvisioningResponse
        {
            Ok = false,
            State = status.State,
            Network = status.Network,
            NodeId = status.NodeId,
            Error = error
        };
    }
}