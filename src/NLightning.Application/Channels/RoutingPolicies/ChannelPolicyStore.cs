using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.RoutingPolicies;

using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;

/// <summary>
/// The per-channel routing policy overrides (wave sp1 lane SP1-G): kept in memory for the synchronous readers
/// (<see cref="IChannelPolicyProvider"/>) and persisted through <see cref="IUnitOfWork.ChannelPolicyDbRepository"/>
/// (table <c>ChannelPolicies</c>, lane SP1-C) in a scope and save of their own.
/// </summary>
/// <remarks>
/// <para>Every stored override is loaded once, by <see cref="LoadAsync"/> (the host may call it at startup, before the
/// peers connect) or by the first read or write, which waits for it (a write always loads first).</para>
/// <para>A unit of work without the repository (its default throws <see cref="NotSupportedException"/>: a build
/// without lane SP1-C's table, or a test double) turns the store memory-only: overrides then apply until the process
/// stops, and a warning says so once. Any other failure of the load is logged and retried by the next read; a failed
/// write throws and changes nothing in memory.</para>
/// <para>Rows of closed channels are not deleted (they are never read for a channel that is not loaded).</para>
/// </remarks>
public sealed class ChannelPolicyStore : IChannelPolicyProvider
{
    private readonly ConcurrentDictionary<ChannelId, ChannelPolicyOverride> _overrides = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly ILogger<ChannelPolicyStore> _logger;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly IServiceScopeFactory _scopeFactory;

    private volatile bool _loaded;
    private volatile bool _memoryOnly;

    public ChannelPolicyStore(IServiceScopeFactory scopeFactory, IOptions<NodeOptions> nodeOptions,
                              ILogger<ChannelPolicyStore> logger)
    {
        _scopeFactory = scopeFactory;
        _nodeOptions = nodeOptions;
        _logger = logger;
    }

    /// <summary>
    /// False once the unit of work turned out not to store overrides (they are then kept in memory only).
    /// </summary>
    public bool IsPersistent => !_memoryOnly;

    /// <summary>
    /// Loads every stored override once. Safe to call again (does nothing once loaded).
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
                return;

            IReadOnlyList<ChannelPolicyOverride> stored;
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                try
                {
                    stored = await unitOfWork.ChannelPolicyDbRepository.GetAllAsync();
                }
                catch (NotSupportedException e)
                {
                    SwitchToMemoryOnly(e);
                    stored = [];
                }
            }

            foreach (var policyOverride in stored)
                _overrides.TryAdd(policyOverride.ChannelId, policyOverride);

            _loaded = true;
            if (stored.Count > 0)
                _logger.LogInformation("Loaded {Count} channel routing policy override(s)", stored.Count);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <inheritdoc/>
    public ChannelPolicyOverride? GetOverride(ChannelId channelId)
    {
        EnsureLoaded();
        return _overrides.TryGetValue(channelId, out var policyOverride) ? policyOverride : null;
    }

    /// <inheritdoc/>
    public ConfiguredChannelPolicy GetConfiguredPolicy(ChannelId channelId) =>
        ConfiguredChannelPolicy.From(_nodeOptions.Value.Routing, GetOverride(channelId));

    /// <inheritdoc/>
    public EffectiveChannelPolicy GetEffectivePolicy(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return ChannelPolicyRules.Resolve(channel, _nodeOptions.Value.Routing, GetOverride(channel.ChannelId));
    }

    /// <summary>
    /// Stores <paramref name="policyOverride"/> (all of its values, nulls included), then applies it.
    /// </summary>
    public async Task SaveAsync(ChannelPolicyOverride policyOverride, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policyOverride);
        await LoadAsync(cancellationToken);

        if (!_memoryOnly)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.ChannelPolicyDbRepository.UpsertAsync(policyOverride);
                await unitOfWork.SaveChangesAsync();
            }
            catch (NotSupportedException e)
            {
                SwitchToMemoryOnly(e);
            }
        }

        _overrides[policyOverride.ChannelId] = policyOverride;
    }

    /// <summary>
    /// Removes the channel's override, if any, from the database then from memory.
    /// </summary>
    /// <returns>Whether the channel had an override.</returns>
    public async Task<bool> DeleteAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        if (!_overrides.ContainsKey(channelId))
            return false;

        if (!_memoryOnly)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.ChannelPolicyDbRepository.DeleteAsync(channelId);
                await unitOfWork.SaveChangesAsync();
            }
            catch (NotSupportedException e)
            {
                SwitchToMemoryOnly(e);
            }
        }

        return _overrides.TryRemove(channelId, out _);
    }

    /// <summary>
    /// The synchronous readers' load: waits for <see cref="LoadAsync"/> the first time (a small table, read once). A
    /// failure is logged and the node-wide values apply until a later read loads the overrides.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        try
        {
            LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not load the channel routing policy overrides; the Node:Routing values apply "
                              + "until they load");
        }
    }

    private void SwitchToMemoryOnly(NotSupportedException e)
    {
        if (_memoryOnly)
            return;

        _memoryOnly = true;
        _logger.LogWarning(e, "The database does not store channel routing policy overrides: they are kept in memory "
                            + "and forgotten on restart");
    }
}