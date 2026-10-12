using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Verrpc;

/// <summary>Reports the emulated LND API level, with the implementation identified in the version string.</summary>
public sealed class VersionerService : Versioner.VersionerBase
{
    public override Task<Verrpc.Version> GetVersion(VersionRequest request, ServerCallContext context)
    {
        var version = new Verrpc.Version
        {
            Version_ = $"{LightningService.LndApiVersion} nlightning",
            AppMajor = 0,
            AppMinor = 21,
            AppPatch = 4,
            AppPreRelease = "beta"
        };
        version.BuildTags.AddRange(["signrpc", "walletrpc", "chainrpc", "invoicesrpc"]);
        return Task.FromResult(version);
    }
}