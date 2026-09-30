using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Gossip.Addresses;
using Domain.Node.Options;

/// <inheritdoc cref="ITorOnionService"/>
/// <remarks>
/// <para>The key lives in <c>Node:Tor:OnionServiceKeyFile</c> (<c>ED25519-V3:&lt;base64&gt;</c>, owner-only): created by
/// Tor on the first start (<c>ADD_ONION NEW:ED25519-V3</c>) and saved before the address is used, then handed back on
/// every start, so the onion address never changes. A key file that cannot be read is never replaced.</para>
/// <para>The service is added without <c>Detach</c>, so it lives as long as our control connection. When Tor closes
/// the connection (a restart) the service is added again with the same key, with a backoff from 5 s to 5 min while Tor
/// is away; the announced address stays the same throughout.</para>
/// </remarks>
public sealed class TorOnionService : ITorOnionService
{
    /// <summary>The oldest Tor series still maintained (0.4.8, the stable series since 2023).</summary>
    internal static readonly Version MinimumRecommendedVersion = new(0, 4, 8);

    private static readonly TimeSpan s_initialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly ILogger<TorOnionService> _logger;
    private readonly TorOptions _torOptions;
    private readonly IReadOnlyList<string> _listenAddresses;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private string? _onionHost;
    private AddressDescriptor? _descriptor;

    public TorOnionService(ILogger<TorOnionService> logger, IOptions<NodeOptions> nodeOptions)
    {
        _logger = logger;
        _torOptions = nodeOptions.Value.Tor;
        _listenAddresses = nodeOptions.Value.ListenAddresses;
    }

    /// <inheritdoc />
    public event EventHandler? AnnouncedAddressesChanged;

    /// <inheritdoc />
    public string? OnionHost
    {
        get
        {
            lock (_lock)
                return _onionHost;
        }
    }

    /// <inheritdoc />
    public ushort OnionPort => _torOptions.OnionServicePort;

    /// <summary>The background registration loop (tests).</summary>
    internal Task Loop => _loop;

    /// <inheritdoc />
    public IReadOnlyList<AddressDescriptor> GetAnnouncedAddresses()
    {
        lock (_lock)
            return _torOptions.AnnounceOnionService && _descriptor is not null ? [_descriptor] : [];
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_torOptions.IsOnionServiceEnabled || _cts is not null)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }

        _cts.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = s_initialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var client = await ConnectAndRegisterAsync(cancellationToken);
                delay = s_initialRetryDelay;

                // Nothing else is sent: wait for Tor to go away (its restart drops our service)
                await client.WaitForCloseAsync(cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    _logger.LogWarning("Tor closed the control connection; our onion service is down until it is "
                                     + "added again");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TorOnionKeyFileException e)
            {
                // Never paper over a key problem with a new address: stop trying
                _logger.LogError("Tor onion service not started: {Message}", e.Message);
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning("Tor onion service: {Message}; retrying in {Delay}",
                                   e is TorControlException or Domain.Exceptions.ConnectionException
                                       ? e.Message
                                       : $"{e.GetType().Name}: {e.Message}", delay);
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, s_maxRetryDelay.Ticks));
            }
        }
    }

    /// <summary>
    /// Connects, authenticates and adds the service with the saved key (or a new one, saved before it is used).
    /// </summary>
    private async Task<TorControlClient> ConnectAndRegisterAsync(CancellationToken cancellationToken)
    {
        if (!TorOptions.TryParseEndPoint(_torOptions.Control, out var endPoint))
            throw new TorControlException($"Node:Tor:Control '{_torOptions.Control}' is not a valid endpoint");

        var target = _torOptions.GetOnionServiceTarget(_listenAddresses)
                  ?? throw new TorControlException("No onion service target (Node:Tor:OnionServiceTarget)");

        TorControlClient client;
        try
        {
            client = await TorControlClient.ConnectAsync(endPoint, cancellationToken);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
        {
            throw new TorControlException($"Could not reach Tor's control port {_torOptions.Control} (is Tor running "
                                        + "with ControlPort or ControlSocket?)", e);
        }

        try
        {
            var protocolInfo = await client.GetProtocolInfoAsync(cancellationToken);
            await client.AuthenticateAsync(protocolInfo, _torOptions.ControlPassword, _torOptions.ControlCookieFile,
                                           _torOptions.AllowUnauthenticatedControlPort, cancellationToken);
            WarnOnOldTor(protocolInfo);

            var keyFile = _torOptions.OnionServiceKeyFile;
            var savedKey = TorOnionKeyFile.Read(keyFile);
            var (serviceId, newKey) = await client.AddOnionAsync(savedKey, _torOptions.OnionServicePort, target,
                                                                  cancellationToken);
            if (savedKey is null)
            {
                if (newKey is null)
                {
                    await client.DeleteOnionAsync(serviceId, CancellationToken.None);
                    throw new TorControlException("Tor created the onion service but returned no private key");
                }

                try
                {
                    TorOnionKeyFile.Write(keyFile, newKey);
                }
                catch
                {
                    // An address whose key is lost must never be announced
                    await client.DeleteOnionAsync(serviceId, CancellationToken.None);
                    throw;
                }

                _logger.LogInformation("Created a new Tor onion service key in {KeyFile}; back it up with the node key",
                                       keyFile);
            }

            if (!OnionV3Address.TryParse(serviceId, out var address, out var error))
                throw new TorControlException($"Tor returned an invalid service id: {error}");

            var host = OnionV3Address.ToHostName(address);
            var descriptor = AddressDescriptor.FromHost(AddressDescriptorType.TorV3, host, _torOptions.OnionServicePort);
            bool changed;
            lock (_lock)
            {
                changed = _descriptor is null || !_descriptor.Equals(descriptor);
                _onionHost = host;
                _descriptor = descriptor;
            }

            _logger.LogInformation("Tor onion service {OnionHost}:{Port} is up (to {Target}, Tor {TorVersion})", host,
                                   _torOptions.OnionServicePort, target, protocolInfo.TorVersion ?? "unknown");
            if (changed)
                AnnouncedAddressesChanged?.Invoke(this, EventArgs.Empty);

            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private void WarnOnOldTor(TorProtocolInfo protocolInfo)
    {
        var version = protocolInfo.GetNumericVersion();
        if (version is not null && version < MinimumRecommendedVersion)
            _logger.LogWarning("Tor {TorVersion} is older than {Minimum}, the oldest maintained series; upgrade Tor",
                               protocolInfo.TorVersion, MinimumRecommendedVersion);
    }
}