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
/// and message; a client handler that is not registered is reported as <see cref="ErrorCodes.InvalidOperation"/>
/// "not available" (a handler whose payment service is missing throws that <see cref="ClientException"/> from its
/// factory); anything else, including a failure while building a registered handler, is
/// <see cref="ErrorCodes.ServerError"/>.
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
            // Only a missing registration is "not available". A handler whose factory finds its service missing
            // throws a ClientException itself; any other construction failure is a wiring bug and a server_error.
            var clientHandler = scope.ServiceProvider
                                     .GetService<IClientCommandHandler<TClientRequest, TClientResponse>>();
            if (clientHandler is null)
            {
                _logger.LogError("No handler registered for {Command}", Command);
                return IpcErrorFactory.CreateErrorEnvelope(envelope, ErrorCodes.InvalidOperation,
                                                           $"{Command} is not available on this node.");
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