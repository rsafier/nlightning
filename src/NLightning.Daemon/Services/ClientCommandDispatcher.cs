using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Services;

using Interfaces;
using LndGrpc;

/// <summary>
/// The LND gRPC server's view of the client command handlers (NL-1164): each call resolves the
/// <see cref="IClientCommandHandler{TRequest, TResponse}"/> of its request in a scope of its own, as an IPC request does.
/// </summary>
internal sealed class ClientCommandDispatcher : INodeCommandDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ClientCommandDispatcher(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<TResponse> DispatchAsync<TRequest, TResponse>(TRequest request,
                                                                    CancellationToken cancellationToken)
        where TRequest : notnull
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetService<IClientCommandHandler<TRequest, TResponse>>()
                   ?? throw new InvalidOperationException(
                          $"No handler for {typeof(TRequest).Name} is registered on this node");
        return await handler.HandleAsync(request, cancellationToken);
    }
}