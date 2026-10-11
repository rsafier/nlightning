using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;
using LndGrpc.Macaroons;

/// <summary>
/// Rotates the LND gRPC macaroon root key 0 (ClientCommand 58, NL-1389): <see cref="LndRootKeyStore.RotateDefault"/>
/// replaces it (in the node's database under the write fence, or the file without one), every macaroon baked with the
/// old key stops verifying at once, and the three default macaroons are baked again under the new one.
/// </summary>
/// <remarks>The operator's replacement for deleting <c>macaroons.key</c>, which no longer rotates once the root keys
/// are in the database. A root key delivered with a locked start is refused: it is rotated at the provisioner.</remarks>
public sealed class RotateMacaroonRootKeyClientHandler
    : IClientCommandHandler<RotateMacaroonRootKeyClientRequest, RotateMacaroonRootKeyClientResponse>
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RotateMacaroonRootKeyClientHandler> _logger;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.RotateMacaroonRootKey;

    public RotateMacaroonRootKeyClientHandler(IServiceProvider services,
                                              ILogger<RotateMacaroonRootKeyClientHandler> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<RotateMacaroonRootKeyClientResponse> HandleAsync(RotateMacaroonRootKeyClientRequest request,
                                                                 CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rootKeys = _services.GetService<LndRootKeyStore>()
                    ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                                 "The LND gRPC server is not part of this node");
        byte[] rootKey;
        try
        {
            rootKey = rootKeys.RotateDefault();
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }

        LndMacaroonFiles.BakeDefaultFiles(rootKeys.DataDirectory, rootKey, true, _logger);
        return Task.FromResult(new RotateMacaroonRootKeyClientResponse(rootKeys.DataDirectory, rootKeys.UsesDatabase,
                                                                       true));
    }
}