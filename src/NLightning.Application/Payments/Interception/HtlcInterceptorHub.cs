using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Interception;

using Domain.Bitcoin.Events;
using Domain.Channels.ValueObjects;
using Domain.Payments.Interception;
using Domain.Protocol.Onion.Enums;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The forward interception hub (LND's <c>InterceptableSwitch</c> with <c>requireinterceptor</c> off, NL-1183): at most
/// one client (a second is refused, LND's <c>ErrInterceptorAlreadyExists</c>); while it is connected every forward the
/// switch offers is held, keyed by the incoming channel's short channel id and HTLC id, until the client resolves it,
/// its incoming HTLC gets within <see cref="HtlcInterceptorSettings.CltvRejectDelta"/> blocks of its expiry (failed
/// back with <c>temporary_channel_failure</c>), or the client disconnects (resumed).
/// </summary>
/// <remarks>
/// The switch's callbacks run outside the hub's lock; a resolution is carried out before
/// <see cref="ResolveAsync"/> returns, the expiry and disconnect resolutions in the background.
/// </remarks>
public sealed class HtlcInterceptorHub : IHtlcForwardInterceptor, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<(ChannelId, ulong), Held> _held = new();
    private readonly Dictionary<(ShortChannelId, ulong), (ChannelId, ulong)> _byKey = new();
    private readonly ILogger<HtlcInterceptorHub> _logger;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private IHtlcInterceptorClient? _client;
    private HtlcInterceptorSettings _settings = new();

    public HtlcInterceptorHub(ILogger<HtlcInterceptorHub> logger, IBlockchainMonitor? blockchainMonitor = null)
    {
        _logger = logger;
        _blockchainMonitor = blockchainMonitor;
        _blockchainMonitor?.OnNewBlockDetected += OnNewBlock;
    }

    /// <inheritdoc />
    public bool IsActive => Volatile.Read(ref _client) is not null;

    /// <summary>The forwards held now (tests, diagnostics).</summary>
    public int HeldCount
    {
        get
        {
            lock (_lock)
                return _held.Count;
        }
    }

    /// <summary>
    /// Connects <paramref name="client"/> until the returned handle is disposed; the forwards still held are offered
    /// to it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another client is connected.</exception>
    public IDisposable Connect(IHtlcInterceptorClient client, HtlcInterceptorSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        List<InterceptedForward> replay;
        lock (_lock)
        {
            if (_client is not null)
                throw new InvalidOperationException("interceptor already exists");

            _settings = settings ?? new HtlcInterceptorSettings();
            Volatile.Write(ref _client, client);
            replay = _held.Values.Select(h => h.Forward).ToList();
        }

        _logger.LogInformation("HTLC interceptor connected ({Held} held forward(s) offered)", replay.Count);
        foreach (var forward in replay)
            client.TryOffer(forward);
        return new Connection(this, client);
    }

    /// <inheritdoc />
    public ForwardInterceptOutcome Intercept(InterceptedForward forward, uint currentHeight,
                                             Func<ForwardInterceptResolution, Task> resolve)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(resolve);
        IHtlcInterceptorClient client;
        InterceptedForward held;
        lock (_lock)
        {
            if (_client is null)
                return ForwardInterceptOutcome.NotIntercepted;

            var key = (forward.IncomingChannelId, forward.IncomingHtlcId);
            if (_held.ContainsKey(key))
                return ForwardInterceptOutcome.Held;

            // LND: no time left to resolve it before the expiry: fail it at once instead of offering it
            if (forward.IncomingExpiry < currentHeight + (ulong)_settings.CltvInterceptDelta)
                return ForwardInterceptOutcome.ExpiryTooSoon;
            if (_held.Count >= _settings.MaxHeld)
                return ForwardInterceptOutcome.Full;

            held = forward with { AutoFailHeight = forward.IncomingExpiry - _settings.CltvRejectDelta };
            _held[key] = new Held(held, resolve);
            _byKey[(held.IncomingShortChannelId, held.IncomingHtlcId)] = key;
            client = _client;
        }

        if (!client.TryOffer(held))
            _logger.LogWarning("Could not offer the held forward of HTLC {HtlcId} of channel {ChannelId} to the "
                             + "interceptor; it stays held", forward.IncomingHtlcId, forward.IncomingChannelId);
        return ForwardInterceptOutcome.Held;
    }

    /// <summary>
    /// Carries out the client's <paramref name="resolution"/> of the forward held under the incoming
    /// <paramref name="incomingShortChannelId"/> and <paramref name="incomingHtlcId"/>.
    /// </summary>
    public async Task<InterceptResolveResult> ResolveAsync(ShortChannelId incomingShortChannelId, ulong incomingHtlcId,
                                                           ForwardInterceptResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        Held held;
        lock (_lock)
        {
            if (!_byKey.TryGetValue((incomingShortChannelId, incomingHtlcId), out var key)
             || !_held.TryGetValue(key, out held!))
                return InterceptResolveResult.NotFound;

            if (resolution.Action == ForwardInterceptAction.Settle
             && (resolution.Preimage is not { } preimage
              || !SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])held.Forward.PaymentHash)))
                return InterceptResolveResult.PreimageMismatch;

            if (held.Resolving)
                return InterceptResolveResult.InProgress;
            held.Resolving = true;
        }

        return await RunAsync(held, resolution)
                   ? InterceptResolveResult.Resolved
                   : InterceptResolveResult.Failed;
    }

    /// <summary>Fails back every held forward whose auto-fail height is reached (LND's <c>failExpiredHtlcs</c>).</summary>
    public void ExpireHeld(uint height)
    {
        List<Held> expired;
        lock (_lock)
        {
            expired = _held.Values.Where(h => !h.Resolving
                                           && (h.Forward.AutoFailHeight <= height || h.ResumeOnDisconnect)).ToList();
            foreach (var held in expired)
                held.Resolving = true;
        }

        foreach (var held in expired)
        {
            _logger.LogInformation("Retrying resolution of held HTLC {HtlcId} of channel {ChannelId} at height {Height}",
                                   held.Forward.IncomingHtlcId, held.Forward.IncomingChannelId, height);
            _ = RunAsync(held, held.Forward.AutoFailHeight <= height
                                  ? new ForwardInterceptResolution(ForwardInterceptAction.Fail,
                                                                   FailureCode: FailureCode.TemporaryChannelFailure)
                                  : ForwardInterceptResolution.Resume);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _blockchainMonitor?.OnNewBlockDetected -= OnNewBlock;
    }

    private void Disconnect(IHtlcInterceptorClient client)
    {
        List<Held> released;
        lock (_lock)
        {
            if (!ReferenceEquals(_client, client))
                return;

            Volatile.Write(ref _client, null);
            foreach (var held in _held.Values)
                held.ResumeOnDisconnect = true;
            released = _held.Values.Where(h => !h.Resolving).ToList();
            foreach (var held in released)
                held.Resolving = true;
        }

        // LND without requireinterceptor: the held forwards go on as if they had never been held
        _logger.LogInformation("HTLC interceptor disconnected, resuming {Count} held forward(s)", released.Count);
        foreach (var held in released)
            _ = RunAsync(held, ForwardInterceptResolution.Resume);
    }

    private void Remove((ChannelId, ulong) key, Held held)
    {
        _held.Remove(key);
        _byKey.Remove((held.Forward.IncomingShortChannelId, held.Forward.IncomingHtlcId));
    }

    private async Task<bool> RunAsync(Held held, ForwardInterceptResolution resolution)
    {
        try
        {
            await Task.Yield();
            await held.Resolve(resolution);
            lock (_lock)
                Remove((held.Forward.IncomingChannelId, held.Forward.IncomingHtlcId), held);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Resolving the held forward of HTLC {HtlcId} of channel {ChannelId} ({Action}) failed",
                             held.Forward.IncomingHtlcId, held.Forward.IncomingChannelId, resolution.Action);
            lock (_lock)
                held.Resolving = false;
            return false;
        }
    }

    private void OnNewBlock(object? sender, NewBlockEventArgs e) => ExpireHeld(e.Height);

    private sealed record Held(InterceptedForward Forward, Func<ForwardInterceptResolution, Task> Resolve)
    {
        public bool Resolving { get; set; }
        public bool ResumeOnDisconnect { get; set; }
    }

    private sealed class Connection(HtlcInterceptorHub hub, IHtlcInterceptorClient client) : IDisposable
    {
        public void Dispose() => hub.Disconnect(client);
    }
}