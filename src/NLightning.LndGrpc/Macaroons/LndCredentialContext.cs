using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Macaroons;

using Domain.Signing;

/// <summary>
/// The signing context LND credentials are bound to (enrollment file, context-derived root keys), or null for a standard
/// node: local signing (`Signing:Mode` unset or `Local`) with the default context keeps the stored root key and its
/// existing macaroons, so standard nodes upgrade in place (NL-1340). Remote signers and hosted contexts stay bound.
/// </summary>
internal static class LndCredentialContext
{
    public static NodeSigningContext? Resolve(IServiceProvider services)
    {
        var context = services.GetService<NodeSigningContext>();
        if (context is null) return null;
        var mode = services.GetService<IConfiguration>()?["Signing:Mode"];
        var local = string.IsNullOrEmpty(mode) || string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase);
        var defaultContext = context.NodeId == NodeSigningContext.DefaultNodeId
                          && context.OwnerId == NodeSigningContext.DefaultOwnerId
                          && context.SignerId == NodeSigningContext.DefaultSignerId;
        return local && defaultContext ? null : context;
    }
}