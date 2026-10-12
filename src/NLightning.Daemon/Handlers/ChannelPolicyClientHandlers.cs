namespace NLightning.Daemon.Handlers;

using Application.Channels.RoutingPolicies;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

/// <summary>
/// Sets or resets a channel's routing policy (ClientCommand 35, <c>setchannelpolicy</c>, wave sp1 lane SP1-G) through
/// <see cref="IChannelPolicyService"/>: validated, saved, applied to forwarding and announced with a new
/// <c>channel_update</c> at once.
/// </summary>
/// <remarks>
/// Refusals are <see cref="ClientException"/>s: an unknown channel (or short channel id) is
/// <see cref="ErrorCodes.InvalidChannel"/>; a request without a value, a reset with values, and a value the service
/// refuses (BOLT 7: <c>htlc_maximum_msat</c> above the capacity or below <c>htlc_minimum_msat</c>; a
/// <c>cltv_expiry_delta</c> below 34) are <see cref="ErrorCodes.InvalidOperation"/>. A node without the service
/// answers "not available". <see cref="ChannelPolicyClientResponse.IsPersisted"/> is false when the policy store keeps
/// overrides in memory only (<see cref="IChannelPolicyProvider.IsPersistent"/>), so the CLI warns that the override is
/// lost on restart.
/// </remarks>
public sealed class SetChannelPolicyClientHandler
    : IClientCommandHandler<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelPolicyProvider? _channelPolicyProvider;
    private readonly IChannelPolicyService? _channelPolicyService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.SetChannelPolicy;

    public SetChannelPolicyClientHandler(IChannelMemoryRepository channelMemoryRepository,
                                         IChannelPolicyService? channelPolicyService,
                                         IChannelPolicyProvider? channelPolicyProvider = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _channelPolicyService = channelPolicyService;
        _channelPolicyProvider = channelPolicyProvider;
    }

    /// <inheritdoc/>
    public async Task<ChannelPolicyClientResponse> HandleAsync(SetChannelPolicyClientRequest request,
                                                               CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = ChannelPolicyHandlerHelpers.RequireService(_channelPolicyService);
        if (request.Reset && request.HasValues)
            throw new ClientException(ErrorCodes.InvalidOperation, "A reset takes no policy value.");
        if (!request.Reset && !request.HasValues)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Nothing to set: give at least one policy value, or --reset.");

        var channel = ChannelPolicyHandlerHelpers.FindChannel(_channelMemoryRepository, request.Channel);
        try
        {
            EffectiveChannelPolicy policy;
            if (request.Reset)
            {
                await service.ResetAsync(channel.ChannelId, ct);
                policy = await service.GetAsync(channel.ChannelId, ct);
            }
            else
            {
                policy = await service.SetAsync(channel.ChannelId, new ChannelPolicyOverride(
                                                    channel.ChannelId, request.FeeBaseMsat,
                                                    request.FeeProportionalMillionths, request.CltvExpiryDelta,
                                                    request.HtlcMinimumMsat, request.HtlcMaximumMsat), ct);
            }

            return new ChannelPolicyClientResponse(policy, ChannelPolicyHandlerHelpers.GetShortChannelId(channel))
            {
                WasReset = request.Reset,
                IsPersisted = _channelPolicyProvider?.IsPersistent ?? true
            };
        }
        catch (KeyNotFoundException e)
        {
            throw new ClientException(ErrorCodes.InvalidChannel, e.Message, e);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid policy: {e.Message}", e);
        }
    }
}

/// <summary>
/// Reads a channel's routing policy in force (ClientCommand 36, <c>getchannelpolicy</c>, wave sp1 lane SP1-G).
/// </summary>
public sealed class GetChannelPolicyClientHandler
    : IClientCommandHandler<GetChannelPolicyClientRequest, ChannelPolicyClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelPolicyService? _channelPolicyService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.GetChannelPolicy;

    public GetChannelPolicyClientHandler(IChannelMemoryRepository channelMemoryRepository,
                                         IChannelPolicyService? channelPolicyService)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _channelPolicyService = channelPolicyService;
    }

    /// <inheritdoc/>
    public async Task<ChannelPolicyClientResponse> HandleAsync(GetChannelPolicyClientRequest request,
                                                               CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = ChannelPolicyHandlerHelpers.RequireService(_channelPolicyService);
        var channel = ChannelPolicyHandlerHelpers.FindChannel(_channelMemoryRepository, request.Channel);
        try
        {
            var policy = await service.GetAsync(channel.ChannelId, ct);
            return new ChannelPolicyClientResponse(policy, ChannelPolicyHandlerHelpers.GetShortChannelId(channel));
        }
        catch (KeyNotFoundException e)
        {
            throw new ClientException(ErrorCodes.InvalidChannel, e.Message, e);
        }
    }
}

internal static class ChannelPolicyHandlerHelpers
{
    internal static IChannelPolicyService RequireService(IChannelPolicyService? service) =>
        service ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                             "Channel routing policies are not available on this node.");

    /// <summary>
    /// The loaded channel <paramref name="reference"/> names: by channel id, or by its real short channel id, the
    /// peer's alias or one of our aliases.
    /// </summary>
    internal static ChannelModel FindChannel(IChannelMemoryRepository repository, ChannelReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.ChannelId is { } channelId)
        {
            return repository.TryGetChannel(channelId, out var channel) && channel is not null
                       ? channel
                       : throw new ClientException(ErrorCodes.InvalidChannel, $"Unknown channel {channelId}.");
        }

        if (reference.ShortChannelId is not { } scid)
            throw new ClientException(ErrorCodes.InvalidChannel, "No channel given.");

        var matches = repository.FindChannels(c => c.ShortChannelId == scid || c.RemoteAlias == scid
                                                || (c.LocalAliases?.Contains(scid) ?? false))
                                .DistinctBy(c => c.ChannelId)
                                .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ClientException(ErrorCodes.InvalidChannel, $"Unknown short channel id {scid}."),
            _ => throw new ClientException(ErrorCodes.InvalidChannel,
                                           $"Short channel id {scid} names {matches.Count} channels; use the channel "
                                         + "id.")
        };
    }

    internal static ShortChannelId? GetShortChannelId(ChannelModel channel) =>
        channel.ShortChannelId.BlockHeight == 0 ? (ShortChannelId?)null : channel.ShortChannelId;
}