using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The local signer's <see cref="IChannelSignerGuardStore"/> over the node's database (NL-1345): each call opens a scope
/// of its own and goes through <see cref="IUnitOfWork.ChannelSignerGuardDbRepository"/>, so the singleton signer never
/// holds a unit of work, and a raise is committed when it returns.
/// </summary>
/// <remarks>
/// The signer's API is synchronous, so the database work runs on the thread pool and is waited for (no synchronization
/// context can deadlock it), as in <see cref="ChannelSigningInfoSource"/>. Failures are thrown to the signer, which
/// refuses the guarded operation.
/// </remarks>
public sealed class ChannelSignerGuardStore : IChannelSignerGuardStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ChannelSignerGuardStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public ChannelSignerGuard? Load(ChannelId channelId) =>
        Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await unitOfWork.ChannelSignerGuardDbRepository.GetAsync(channelId);
        }).GetAwaiter().GetResult();

    /// <inheritdoc />
    public void Raise(ChannelId channelId, ChannelSignerGuard guard) =>
        Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelSignerGuardDbRepository.RaiseAsync(channelId, guard);
        }).GetAwaiter().GetResult();
}