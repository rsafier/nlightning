using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The signer's <see cref="IChannelSigningInfoSource"/> over the node's database (NL-067): each lookup opens a scope of
/// its own and reads the channel through <see cref="IUnitOfWork.ChannelSigningInfoDbRepository"/>, so a singleton signer
/// never holds a unit of work.
/// </summary>
/// <remarks>
/// The signer's API is synchronous, so the read runs on the thread pool and is waited for (no synchronization context
/// can deadlock it). It only runs when the signer meets a channel it has not registered, i.e. once per channel after a
/// restart.
/// </remarks>
public sealed class ChannelSigningInfoSource : IChannelSigningInfoSource
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChannelSigningInfoSource> _logger;

    public ChannelSigningInfoSource(IServiceScopeFactory scopeFactory, ILogger<ChannelSigningInfoSource>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? NullLogger<ChannelSigningInfoSource>.Instance;
    }

    /// <inheritdoc />
    public bool TryGet(ChannelId channelId, out ChannelSigningInfo signingInfo)
    {
        ChannelSigningInfo? loaded;
        try
        {
            loaded = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                return await unitOfWork.ChannelSigningInfoDbRepository.GetAsync(channelId);
            }).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not load the signing data of channel {ChannelId}", channelId);
            loaded = null;
        }

        signingInfo = loaded ?? default;
        return loaded is not null;
    }
}