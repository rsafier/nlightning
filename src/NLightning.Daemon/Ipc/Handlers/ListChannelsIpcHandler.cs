using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Daemon.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;
using Services.Ipc.Factories;
using Transport.Ipc;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ListChannelsIpcHandler : IIpcCommandHandler
{
    private readonly ILogger<ListChannelsIpcHandler> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ClientCommand Command => ClientCommand.ListChannels;

    public ListChannelsIpcHandler(ILogger<ListChannelsIpcHandler> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<IpcEnvelope> HandleAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var request = MessagePackSerializer.Deserialize<ListChannelsIpcRequest>(envelope.Payload,
                cancellationToken: ct);

            // The client handler reads the database, so it is scoped
            using var scope = _serviceProvider.CreateScope();
            var clientHandler =
                scope.ServiceProvider
                     .GetRequiredService<IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>>();
            var clientResponse = await clientHandler.HandleAsync(request.ToClientRequest(), ct);

            var payload = MessagePackSerializer.Serialize(ListChannelsIpcResponse.FromClientResponse(clientResponse),
                                                          cancellationToken: ct);
            return new IpcEnvelope
            {
                Version = envelope.Version,
                Command = envelope.Command,
                CorrelationId = envelope.CorrelationId,
                Kind = IpcEnvelopeKind.Response,
                Payload = payload
            };
        }
        catch (ClientException ce)
        {
            _logger.LogError(ce, "Error while handling ListChannels");
            return IpcErrorFactory.CreateErrorEnvelope(envelope, ce.ErrorCode, ce.Message);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error listing channels");
            return IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.ServerError,
                                                       $"Error listing channels: {e.Message}");
        }
    }
}