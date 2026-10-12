using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.Interfaces;
using Domain.Persistence.Interfaces;

/// <summary>
/// The local signer's <see cref="IChannelKeyIndexStore"/> over the node's database (NL-1388): each call opens a scope
/// of its own and goes through <see cref="IUnitOfWork.ChannelKeyIndexDbRepository"/>, so the singleton signer never
/// holds a unit of work, and a claim is committed when it returns.
/// </summary>
/// <remarks>
/// The signer's API is synchronous, so the database work runs on the thread pool and is waited for (no synchronization
/// context can deadlock it), as in <see cref="ChannelSignerGuardStore"/>. Failures are thrown to the caller, which
/// refuses the new channel.
/// </remarks>
public sealed class ChannelKeyIndexStore : IChannelKeyIndexStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ChannelKeyIndexStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public uint? GetHighWater() =>
        Run(repository => repository.GetHighWaterAsync());

    /// <inheritdoc />
    public bool TryClaim(uint index) =>
        Run(repository => repository.TryClaimAsync(index));

    /// <inheritdoc />
    public void Raise(uint index) =>
        Run(async repository =>
        {
            await repository.RaiseAsync(index);
            return true;
        });

    private T Run<T>(Func<Domain.Channels.Interfaces.IChannelKeyIndexDbRepository, Task<T>> action) =>
        Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await action(unitOfWork.ChannelKeyIndexDbRepository);
        }).GetAwaiter().GetResult();
}