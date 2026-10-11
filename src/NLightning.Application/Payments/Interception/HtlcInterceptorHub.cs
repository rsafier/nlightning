using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Interception;

using Domain.Bitcoin.Events;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Payments.Interception;
using Domain.Protocol.Onion.Enums;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The forward interception hub (LND's <c>InterceptableSwitch</c>, NL-1183, NL-1182): at most one client (a second is
/// refused, LND's <c>ErrInterceptorAlreadyExists</c>); while it is connected every forward the switch offers is held,
/// keyed by the incoming channel's short channel id and HTLC id, until the client resolves it, its incoming HTLC gets
/// within <see cref="HtlcInterceptorSettings.CltvRejectDelta"/> blocks of its expiry (failed back with
/// <c>temporary_channel_failure</c>), or the client disconnects (resumed, unless an interceptor is required).
/// </summary>
/// <remarks>
/// <para>With <see cref="HtlcInterceptorSettings.RequireInterceptor"/> (LND's <c>requireinterceptor</c>) a forward that
/// arrives while no client is connected is failed with <c>temporary_channel_failure</c> when it is new (the interceptor
/// never saw it) and held when it is a replay (after a restart or a reconnection the interceptor may still answer it);
/// a disconnect keeps every hold, and the next client is offered them.</para>
/// <para>A forward whose incoming channel is closing on chain is held on chain (<see cref="InterceptOnChain"/>, LND's
/// on-chain interception): it replaces an off-chain hold of the same HTLC, is offered with its incoming expiry as the
/// deadline, accepts only a settle (resume and fail answer <see cref="InterceptResolveResult.NotAllowedOnChain"/>), is
/// kept across disconnects and is dropped, without any callback, once the chain reaches that expiry or the switch
/// releases it.</para>
/// <para>The switch's callbacks run outside the hub's lock; a resolution is carried out before
/// <see cref="ResolveAsync"/> returns, the expiry and disconnect resolutions in the background. A hold stays until its
/// callback succeeds (NL-1234).</para>
/// </remarks>
public sealed class HtlcInterceptorHub : IHtlcForwardInterceptor, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<(ChannelId, ulong), Held> _held = new();
    private readonly Dictionary<(ShortChannelId, ulong), (ChannelId, ulong)> _byKey = new();
    private readonly ILogger<HtlcInterceptorHub> _logger;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private IHtlcInterceptorClient? _client;
    private HtlcInterceptorSettings _settings;

    public HtlcInterceptorHub(ILogger<HtlcInterceptorHub> logger, IBlockchainMonitor? blockchainMonitor = null,
                              IOptions<HtlcInterceptorSettings>? options = null)
    {
        _logger = logger;
        _blockchainMonitor = blockchainMonitor;
        _settings = options?.Value ?? new HtlcInterceptorSettings();
        _blockchainMonitor?.OnNewBlockDetected += OnNewBlock;
    }

    /// <inheritdoc />
    public bool IsActive => Volatile.Read(ref _client) is not null;

    /// <inheritdoc />
    public bool IsRequired
    {
        get
        {
            lock (_lock)
                return _settings.RequireInterceptor;
        }
    }

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
    /// to it. <paramref name="settings"/>, when given, replace the hub's.
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

            if (settings is not null)
                _settings = settings;
            Volatile.Write(ref _client, client);
            replay = _held.Values.Select(h => h.Forward).ToList();
        }

        _logger.LogInformation("HTLC interceptor connected ({Held} held forward(s) offered)", replay.Count);
        foreach (var forward in replay)
            client.TryOffer(forward);
        return new Connection(this, client);
    }

    /// <inheritdoc />
    public ForwardInterceptOutcome Intercept(InterceptedForward forward, uint currentHeight, bool isReplay,
                                             Func<ForwardInterceptResolution, Task> resolve)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(resolve);
        IHtlcInterceptorClient? client;
        InterceptedForward held;
        lock (_lock)
        {
            client = _client;
            var key = (forward.IncomingChannelId, forward.IncomingHtlcId);
            if (_held.ContainsKey(key))
                return ForwardInterceptOutcome.Held;

            // LND: an auto-fail height beyond its signed 32-bit field is expiry_too_far
            if ((long)forward.IncomingExpiry - _settings.CltvRejectDelta > int.MaxValue)
                return ForwardInterceptOutcome.ExpiryTooFar;

            // LND: no time left to resolve it before the expiry: fail it at once instead of offering it
            if (forward.IncomingExpiry < currentHeight + (ulong)_settings.CltvInterceptDelta)
                return ForwardInterceptOutcome.ExpiryTooSoon;

            // Expiry safety applies even without a connected or required interceptor (LND handleExpired).
            if (client is null && !_settings.RequireInterceptor)
                return ForwardInterceptOutcome.NotIntercepted;

            // NL-1182, LND's requireinterceptor: a new forward the interceptor never saw can still be failed; a replay
            // waits, since the interceptor may have decided on it before the restart
            if (client is null && !isReplay)
                return ForwardInterceptOutcome.InterceptorRequired;

            if (_held.Count >= _settings.MaxHeld)
                return ForwardInterceptOutcome.Full;

            held = forward with
            {
                AutoFailHeight = forward.IncomingExpiry - _settings.CltvRejectDelta,
                IsOnChain = false
            };
            Add(key, new Held(held, resolve));
        }

        if (client is null)
            _logger.LogInformation("Holding replayed forward of HTLC {HtlcId} of channel {ChannelId} until an HTLC "
                                 + "interceptor connects (an interceptor is required)", forward.IncomingHtlcId,
                                   forward.IncomingChannelId);
        else if (!client.TryOffer(held))
            _logger.LogWarning("Could not offer the held forward of HTLC {HtlcId} of channel {ChannelId} to the "
                             + "interceptor; it stays held", forward.IncomingHtlcId, forward.IncomingChannelId);
        return ForwardInterceptOutcome.Held;
    }

    /// <inheritdoc />
    public bool InterceptOnChain(InterceptedForward forward, Func<ForwardInterceptResolution, Task> resolve)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(resolve);
        IHtlcInterceptorClient? client;
        InterceptedForward held;
        lock (_lock)
        {
            var key = (forward.IncomingChannelId, forward.IncomingHtlcId);
            if (_held.TryGetValue(key, out var existing))
            {
                // LND: a duplicate on-chain offer is a no-op; an off-chain hold is promoted (unless its resolution is
                // running: the callback then reports the channel on chain and the hub promotes it itself)
                if (existing.OnChain)
                    return true;
                if (existing.Resolving)
                    return true;
            }
            else if (_held.Count >= _settings.MaxHeld)
                return false;

            held = forward with { AutoFailHeight = forward.IncomingExpiry, IsOnChain = true };
            if (existing is not null)
                Remove(key, existing);
            Add(key, new Held(held, resolve) { OnChain = true });
            client = _client;
        }

        _logger.LogInformation("Holding the forward of HTLC {HtlcId} of channel {ChannelId}, which is closing on chain, "
                             + "for an interceptor settle until height {Deadline}", forward.IncomingHtlcId,
                               forward.IncomingChannelId, forward.IncomingExpiry);
        client?.TryOffer(held);
        return true;
    }

    /// <inheritdoc />
    public void Release(ChannelId incomingChannelId, ulong incomingHtlcId)
    {
        lock (_lock)
        {
            var key = (incomingChannelId, incomingHtlcId);
            if (_held.TryGetValue(key, out var held) && !held.Resolving)
                Remove(key, held);
        }
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

            // LND: an HTLC on chain can only be settled (ErrCannotFailOnChain, ErrCannotResumeOnChain); it stays held
            if (held.OnChain && resolution.Action != ForwardInterceptAction.Settle)
                return InterceptResolveResult.NotAllowedOnChain;

            if (resolution.Action == ForwardInterceptAction.Settle
             && (resolution.Preimage is not { } preimage
              || !SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])held.Forward.PaymentHash)))
                return InterceptResolveResult.PreimageMismatch;

            if (held.Resolving)
                return InterceptResolveResult.InProgress;
            held.Resolving = true;
        }

        return await RunAsync(held, resolution) switch
        {
            RunResult.Done => InterceptResolveResult.Resolved,
            RunResult.MovedOnChain => InterceptResolveResult.NotAllowedOnChain,
            _ => InterceptResolveResult.Failed
        };
    }

    /// <summary>
    /// Fails back every forward held off chain whose auto-fail height is reached (LND's <c>failExpiredHtlcs</c>), retries
    /// the resumes a disconnect could not carry out, and drops the forwards held on chain whose incoming expiry is
    /// reached (the peer can take them by the timeout path now).
    /// </summary>
    public void ExpireHeld(uint height)
    {
        List<Held> expired;
        lock (_lock)
        {
            foreach (var (key, onChain) in _held.Where(h => h.Value is { OnChain: true, Resolving: false }
                                                         && h.Value.Forward.IncomingExpiry <= height).ToList())
            {
                Remove(key, onChain);
                _logger.LogInformation("Dropped the on-chain hold of HTLC {HtlcId} of channel {ChannelId} at height "
                                     + "{Height}: its incoming expiry is reached", onChain.Forward.IncomingHtlcId,
                                       onChain.Forward.IncomingChannelId, height);
            }

            expired = _held.Values.Where(h => h is { OnChain: false, Resolving: false }
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

            // NL-1182, LND's requireinterceptor: every hold waits for the next client
            if (_settings.RequireInterceptor)
            {
                _logger.LogInformation("HTLC interceptor disconnected, retaining {Count} held forward(s)",
                                       _held.Count);
                return;
            }

            // On-chain holds have no link flow to resume: they stay until settled or expired
            foreach (var held in _held.Values.Where(h => !h.OnChain))
                held.ResumeOnDisconnect = true;
            released = _held.Values.Where(h => h is { OnChain: false, Resolving: false }).ToList();
            foreach (var held in released)
                held.Resolving = true;
        }

        // LND without requireinterceptor: the held forwards go on as if they had never been held
        _logger.LogInformation("HTLC interceptor disconnected, resuming {Count} held forward(s)", released.Count);
        foreach (var held in released)
            _ = RunAsync(held, ForwardInterceptResolution.Resume);
    }

    private void Add((ChannelId, ulong) key, Held held)
    {
        _held[key] = held;
        _byKey[(held.Forward.IncomingShortChannelId, held.Forward.IncomingHtlcId)] = key;
    }

    private void Remove((ChannelId, ulong) key, Held held)
    {
        if (_held.TryGetValue(key, out var current) && ReferenceEquals(current, held))
            _held.Remove(key);
        if (_byKey.TryGetValue((held.Forward.IncomingShortChannelId, held.Forward.IncomingHtlcId), out var mapped)
         && mapped == key && !_held.ContainsKey(key))
            _byKey.Remove((held.Forward.IncomingShortChannelId, held.Forward.IncomingHtlcId));
    }

    private async Task<RunResult> RunAsync(Held held, ForwardInterceptResolution resolution)
    {
        try
        {
            await Task.Yield();
            await held.Resolve(resolution);
            lock (_lock)
                Remove((held.Forward.IncomingChannelId, held.Forward.IncomingHtlcId), held);
            return RunResult.Done;
        }
        catch (ForwardHeldOnChainException e)
        {
            // NL-1182: the incoming channel went on chain after the hold: only a settle is left, until its expiry
            InterceptedForward promoted;
            IHtlcInterceptorClient? client;
            lock (_lock)
            {
                held.Resolving = false;
                held.ResumeOnDisconnect = false;
                held.OnChain = true;
                held.Forward = held.Forward with { AutoFailHeight = held.Forward.IncomingExpiry, IsOnChain = true };
                promoted = held.Forward;
                client = _client;
            }

            _logger.LogInformation("The held forward of HTLC {HtlcId} of channel {ChannelId} is now held on chain: "
                                 + "{Reason}", held.Forward.IncomingHtlcId, held.Forward.IncomingChannelId, e.Message);
            client?.TryOffer(promoted);
            return RunResult.MovedOnChain;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Resolving the held forward of HTLC {HtlcId} of channel {ChannelId} ({Action}) failed",
                             held.Forward.IncomingHtlcId, held.Forward.IncomingChannelId, resolution.Action);
            lock (_lock)
                held.Resolving = false;
            return RunResult.Failed;
        }
    }

    private void OnNewBlock(object? sender, NewBlockEventArgs e) => ExpireHeld(e.Height);

    private enum RunResult
    {
        Done,
        Failed,
        MovedOnChain
    }

    private sealed class Held(InterceptedForward forward, Func<ForwardInterceptResolution, Task> resolve)
    {
        public InterceptedForward Forward { get; set; } = forward;
        public Func<ForwardInterceptResolution, Task> Resolve { get; } = resolve;
        public bool Resolving { get; set; }
        public bool ResumeOnDisconnect { get; set; }
        public bool OnChain { get; set; }
    }

    private sealed class Connection(HtlcInterceptorHub hub, IHtlcInterceptorClient client) : IDisposable
    {
        public void Dispose() => hub.Disconnect(client);
    }
}