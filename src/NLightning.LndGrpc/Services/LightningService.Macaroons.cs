using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Lnrpc;
using Macaroons;
using MacaroonId = Macaroons.MacaroonId;

public sealed partial class LightningService
{
    /// <summary>LND's <c>validEntities</c> (rpcserver.go v0.21.4).</summary>
    private static readonly HashSet<string> s_validEntities =
    [
        "onchain", "offchain", "address", "message", "peers", "info", "invoices", "signer", "macaroon",
        LndPermissions.UriEntity
    ];

    /// <summary>LND's <c>validActions</c>.</summary>
    private static readonly HashSet<string> s_validActions = ["read", "write", "generate"];

    /// <summary>
    /// <c>BakeMacaroon</c> (NL-1169): a macaroon for the given operations under root key id <c>root_key_id</c> (its key
    /// made on first use, LND's storage id = the decimal id), hex as LND answers. As LND: at least one permission,
    /// entities and actions from LND's lists and <c>uri</c> actions naming a method this server knows, unless
    /// <c>allow_external_permissions</c>.
    /// </summary>
    public override Task<BakeMacaroonResponse> BakeMacaroon(BakeMacaroonRequest request, ServerCallContext context)
    {
        var rootKeys = RootKeys();
        if (request.Permissions.Count == 0)
            throw InvalidArgument("permission list cannot be empty");

        var ops = new List<MacaroonOp>();
        foreach (var permission in request.Permissions)
        {
            if (!request.AllowExternalPermissions)
            {
                if (!s_validEntities.Contains(permission.Entity))
                    throw InvalidArgument("invalid permission entity. allowed entities are "
                                        + $"[{string.Join(" ", s_validEntities)}]");
                if (permission.Entity == LndPermissions.UriEntity)
                {
                    if (LndPermissions.ForMethod(permission.Action) is null)
                        throw InvalidArgument($"invalid permission action. {permission.Action} is not a valid URI");
                }
                else if (!s_validActions.Contains(permission.Action))
                {
                    throw InvalidArgument("invalid permission action. allowed actions are "
                                        + $"[{string.Join(" ", s_validActions)}]");
                }
            }

            ops.Add(new MacaroonOp(permission.Entity, permission.Action));
        }

        var rootKey = rootKeys.GetOrCreate(request.RootKeyId);
        var id = new MacaroonId(System.Security.Cryptography.RandomNumberGenerator.GetBytes(MacaroonId.NonceLength),
                                LndRootKeyStore.StorageIdOf(request.RootKeyId), ops);
        var macaroon = Macaroon.Create(rootKey, id.Encode(), LndPermissions.Location);
        return Task.FromResult(new BakeMacaroonResponse { Macaroon = Convert.ToHexStringLower(macaroon.Serialize()) });
    }

    /// <summary><c>ListMacaroonIDs</c>: the root key ids that have a key.</summary>
    public override Task<ListMacaroonIDsResponse> ListMacaroonIDs(ListMacaroonIDsRequest request,
                                                                  ServerCallContext context)
    {
        var response = new ListMacaroonIDsResponse();
        response.RootKeyIds.Add(RootKeys().ListIds());
        return Task.FromResult(response);
    }

    /// <summary>
    /// <c>DeleteMacaroonID</c>: deletes a root key id's key, which invalidates every macaroon baked with it; id 0 (the
    /// default macaroons') is refused, as LND refuses it.
    /// </summary>
    public override Task<DeleteMacaroonIDResponse> DeleteMacaroonID(DeleteMacaroonIDRequest request,
                                                                    ServerCallContext context)
    {
        try
        {
            return Task.FromResult(new DeleteMacaroonIDResponse { Deleted = RootKeys().Delete(request.RootKeyId) });
        }
        catch (InvalidOperationException e)
        {
            throw InvalidArgument(e.Message);
        }
    }

    private LndRootKeyStore RootKeys() =>
        _rootKeys ?? throw Unimplemented("macaroons are not managed by this server (no root key store)");
}