using MessagePack;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Services.Ipc;

using Daemon.Ipc.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Node.Constants;
using Domain.Node.Interfaces;
using Transport.Ipc;

/// <summary>
/// Default router that uses a map of handlers keyed by command.
/// </summary>
/// <remarks>
/// While the node drains for a graceful shutdown (NL-591) the commands that start something
/// (<see cref="RefusedWhileDraining"/>) are refused with <see cref="ErrorCodes.InvalidOperation"/>; the read-only
/// commands and closes still work.
/// </remarks>
internal sealed class IpcRequestRouter : IIpcRequestRouter
{
    /// <summary>The commands refused while the node drains for its shutdown (NL-591).</summary>
    internal static IReadOnlySet<ClientCommand> RefusedWhileDraining { get; } = new HashSet<ClientCommand>
    {
        ClientCommand.ConnectPeer,
        ClientCommand.OpenChannel,
        ClientCommand.CreateInvoice,
        ClientCommand.PayInvoice,
        ClientCommand.RestoreChanBackup,
        ClientCommand.Withdraw,
        ClientCommand.CreateOffer,
        ClientCommand.PayOffer,
        ClientCommand.FetchInvoice,
        ClientCommand.Keysend,
        ClientCommand.SpliceIn,
        ClientCommand.SpliceOut,
        ClientCommand.BumpSplice,
        ClientCommand.BumpOpen,
        ClientCommand.PayRoute,
        ClientCommand.PayRouteAttach
    };

    private readonly IReadOnlyDictionary<ClientCommand, IIpcCommandHandler> _handlers;
    private readonly ILogger<IpcRequestRouter> _logger;
    private readonly INodeDrainState? _nodeDrainState;

    public IpcRequestRouter(IEnumerable<IIpcCommandHandler> handlers, ILogger<IpcRequestRouter> logger,
                            INodeDrainState? nodeDrainState = null)
    {
        _handlers = handlers.ToDictionary(h => h.Command);
        _logger = logger;
        _nodeDrainState = nodeDrainState;
    }

    public async Task<IpcEnvelope> RouteAsync(IpcEnvelope request, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(request.Command, out var handler))
        {
            return Error(request, "unknown_command", $"Unknown command: {request.Command}");
        }

        if (_nodeDrainState is { IsDraining: true } && RefusedWhileDraining.Contains(request.Command))
            return Error(request, ErrorCodes.InvalidOperation, NodeDrain.Refusal(request.Command.ToString()));

        try
        {
            return await handler.HandleAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC handler error for {Command}", request.Command);
            return Error(request, "server_error", ex.Message);
        }
    }

    private static IpcEnvelope Error(IpcEnvelope request, string code, string message)
    {
        var payload = MessagePackSerializer.Serialize(new IpcError { Code = code, Message = message });
        return new IpcEnvelope
        {
            Version = request.Version,
            Command = request.Command,
            CorrelationId = request.CorrelationId,
            Kind = IpcEnvelopeKind.Error,
            Payload = payload
        };
    }
}