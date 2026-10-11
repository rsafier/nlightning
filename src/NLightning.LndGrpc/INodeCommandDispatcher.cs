namespace NLightning.LndGrpc;

/// <summary>
/// The node's own client commands (the handlers behind <c>openchannel</c>, <c>closechannel</c>,
/// <c>setchannelpolicy</c>, <c>withdraw</c>, <c>createholdinvoice</c>, ...), for the LND RPCs that do the same thing
/// (NL-1164): one implementation of each operation, whichever API asks. The daemon implements it over its
/// <c>IClientCommandHandler&lt;TRequest, TResponse&gt;</c> registrations (one scope per call).
/// </summary>
public interface INodeCommandDispatcher
{
    /// <summary>Runs the handler of <typeparamref name="TRequest"/>.</summary>
    /// <exception cref="Domain.Client.Exceptions.ClientException">The handler refused the request.</exception>
    /// <exception cref="InvalidOperationException">No handler is registered for the pair.</exception>
    Task<TResponse> DispatchAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken)
        where TRequest : notnull;
}