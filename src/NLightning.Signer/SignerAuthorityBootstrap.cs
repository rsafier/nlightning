using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.Signer;

// Checkpoints describe the installation snapshot and require a complete, well-formed configuration.
// Startup verifies actual safety history against current independent authority, never this static snapshot.
internal sealed record SignerAuthorityConfiguration(NativeSignerBinding Binding, NativeSignerExecution Execution)
{
    public void Validate(NativeSignerBinding expectedBinding)
    {
        if (Binding != expectedBinding)
            throw new UnauthorizedAccessException("Installed authority configuration belongs to another enrollment.");
        if (Execution is null || string.IsNullOrWhiteSpace(Execution.WriterId) || Execution.Epoch <= 0
         || !IsCheckpoint(Execution.Checkpoint, allowInitial: true)
         || !IsCheckpoint(Execution.SignerCheckpoint, allowInitial: false))
            throw new InvalidDataException("Installed authority execution is incomplete or invalid.");
    }

    private static bool IsCheckpoint(string? value, bool allowInitial) =>
        allowInitial && value == NativeSignerAuthority.InitialCheckpoint
        || value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal static class SignerAuthorityBootstrap
{
    [UnsupportedOSPlatform("windows")]
    public static SignerAuthorityConfiguration Load(string path, NativeSignerBinding expectedBinding)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Authority configuration requires an absolute administrator-installed path.");
        SignerFiles.RequirePrivate(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024)
            throw new InvalidDataException("Authority configuration is too large.");
        var configuration = JsonSerializer.Deserialize<SignerAuthorityConfiguration>(stream, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        }) ?? throw new InvalidDataException("Authority configuration is missing.");
        configuration.Validate(expectedBinding);
        return configuration;
    }

    // The connection factory, evidence adapter and validator are installed by trusted signer composition.
    // This path never provisions enrollment, spending approvals or a new writer generation.
    public static NativeAuthorizedSignerExecutor Create(SignerAuthorityConfiguration configuration,
        NativeSignerBinding expectedBinding, NativeSignerAuthority authority, DurableSignerState journal,
        NativeSignerSafetyCheckpointSet checkpoints, IAuthenticatedNativeChainEvidence evidence,
        Func<IAuthenticatedNativeChainEvidence, INativeSignerRequestValidator> createValidator)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(createValidator);
        configuration.Validate(expectedBinding);
        checkpoints.ValidateEnrollment(expectedBinding);
        // Installed configuration identifies the writer; changing checkpoints come from independent authority.
        var execution = authority.LoadCurrentExecution(expectedBinding, configuration.Execution.WriterId,
            configuration.Execution.Epoch, checkpoints.GetCheckpoint);
        return authority.RequireCurrentWriter(expectedBinding, execution, checkpoints.GetCheckpoint, () =>
        {
            evidence.RequireFresh(expectedBinding);
            var validator = createValidator(evidence)
                         ?? throw new InvalidOperationException("An installed purpose-specific validator is required.");
            return new NativeAuthorizedSignerExecutor(authority, expectedBinding, journal,
                execution, validator, checkpoints);
        });
    }
}