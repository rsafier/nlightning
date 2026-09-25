using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Daemon.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Interfaces;
using Services.Ipc.Factories;
using Transport.Ipc;

/// <summary>
/// An IPC command served by a scoped <see cref="IClientCommandHandler{TRequest,TResponse}"/>: deserializes the IPC
/// request, maps it to the client request, runs the client handler in a new scope and maps its response back.
/// </summary>
/// <remarks>
/// Errors become error envelopes: a <see cref="ClientException"/> keeps its <see cref="ClientException.ErrorCode"/>
/// and message; a missing client handler or service (a node built without the payment services) is reported as
/// <see cref="ErrorCodes.InvalidOperation"/> "not available"; anything else is <see cref="ErrorCodes.ServerError"/>.
/// </remarks>
internal abstract class ClientCommandIpcHandler<TIpcRequest, TClientRequest, TClientResponse, TIpcResponse>
    : IIpcCommandHandler
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    public abstract ClientCommand Command { get; }

    protected ClientCommandIpcHandler(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected abstract TClientRequest ToClientRequest(TIpcRequest request);

    protected abstract TIpcResponse ToIpcResponse(TClientResponse response);

    public async Task<IpcEnvelope> HandleAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var request = MessagePackSerializer.Deserialize<TIpcRequest>(envelope.Payload, cancellationToken: ct);

            using var scope = _serviceProvider.CreateScope();
            IClientCommandHandler<TClientRequest, TClientResponse> clientHandler;
            try
            {
                clientHandler = scope.ServiceProvider
                                     .GetRequiredService<IClientCommandHandler<TClientRequest, TClientResponse>>();
            }
            catch (InvalidOperationException e)
            {
                _logger.LogError(e, "No handler for {Command}", Command);
                return IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.InvalidOperation,
                                                           $"{Command} is not available on this node: {e.Message}");
            }

            var clientResponse = await clientHandler.HandleAsync(ToClientRequest(request), ct);
            var payload = MessagePackSerializer.Serialize(ToIpcResponse(clientResponse), cancellationToken: ct);
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
            _logger.LogWarning(ce, "{Command} refused: {Message}", Command, ce.Message);
            return IpcErrorFactory.CreateErrorEnvelope(envelope, ce.ErrorCode, ce.Message);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while handling {Command}", Command);
            return IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.ServerError,
                                                       $"Error while handling {Command}: {e.Message}");
        }
    }
}