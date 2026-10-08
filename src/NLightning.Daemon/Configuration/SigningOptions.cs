using Microsoft.Extensions.Configuration;
using NLightning.Domain.Signing;

namespace NLightning.Daemon.Configuration;

/// <summary>Node-side transport settings. The remote daemon exclusively owns the encrypted key file.</summary>
public sealed class SigningOptions
{
    public const string SectionName = "Signing";

    public string NodeId { get; set; } = NodeSigningContext.DefaultNodeId;
    public string OwnerId { get; set; } = NodeSigningContext.DefaultOwnerId;
    public string SignerId { get; set; } = NodeSigningContext.DefaultSignerId;

    public string Mode { get; set; } = "Local";
    public string SocketPath { get; set; } = string.Empty;
    public string AuthTokenFile { get; set; } = string.Empty;
    public string? WriterId { get; set; }
    public long? WriterEpoch { get; set; }
    public string? WriterCredentialFile { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public string? ExpectedNodePublicKey { get; set; }

    public bool IsRemoteNative => string.Equals(Mode, "RemoteNative", StringComparison.OrdinalIgnoreCase);
    public bool IsVls => string.Equals(Mode, "Vls", StringComparison.OrdinalIgnoreCase);
    public bool IsRemote => IsRemoteNative || IsVls;

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        foreach (var identifier in new[] { NodeId, OwnerId, SignerId })
        {
            try { NodeSigningContext.ValidateIdentifier(identifier, "Signing context"); }
            catch (ArgumentException ex) { errors.Add(ex.Message); }
        }
        if (!IsRemote && !string.Equals(Mode, "Local", StringComparison.OrdinalIgnoreCase))
            errors.Add("Signing:Mode must be Local, RemoteNative or Vls.");
        if (WriterId is not null || WriterEpoch is not null || WriterCredentialFile is not null)
        {
            if (!IsRemoteNative) errors.Add("Writer credentials require RemoteNative signing mode.");
            try { NodeSigningContext.ValidateIdentifier(WriterId ?? "", nameof(WriterId)); }
            catch (ArgumentException ex) { errors.Add(ex.Message); }
            if (WriterEpoch is null or <= 0) errors.Add("Signing:WriterEpoch must be positive.");
            if (WriterCredentialFile is null || !Path.IsPathFullyQualified(WriterCredentialFile))
                errors.Add("Signing:WriterCredentialFile must be an absolute path.");
        }
        if (!IsRemote)
            return errors;

        if (!Path.IsPathFullyQualified(SocketPath))
            errors.Add("Signing:SocketPath must be an absolute Unix socket path in remote signing mode.");
        if (!Path.IsPathFullyQualified(AuthTokenFile))
            errors.Add("Signing:AuthTokenFile must be an absolute path in remote signing mode.");
        if (TimeoutSeconds is < 1 or > 300)
            errors.Add("Signing:TimeoutSeconds must be between 1 and 300.");
        if (ExpectedNodePublicKey is { } publicKey
            && (publicKey.Length != 66 || !(publicKey.StartsWith("02", StringComparison.Ordinal)
                                            || publicKey.StartsWith("03", StringComparison.Ordinal))
                                      || !publicKey.All(Uri.IsHexDigit)))
            errors.Add("Signing:ExpectedNodePublicKey must be a compressed public key (66 hex characters).");
        return errors;
    }

    internal string ReadAuthToken() => ReadPrivateCredential(AuthTokenFile);
    internal string? ReadWriterCredential() => WriterCredentialFile is null ? null : ReadPrivateCredential(WriterCredentialFile);

    private static string ReadPrivateCredential(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode otherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & otherAccess) != 0)
                throw new ArgumentException("Signing credential files must be accessible only to their owner (chmod 600).");
        }
        var token = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (token.Length < 32 || token.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Signing credential files must contain at least 32 printable ASCII characters.");
        return token;
    }

    internal static SigningOptions Read(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<SigningOptions>() ?? new SigningOptions();
        var errors = options.GetValidationErrors();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors));
        return options;
    }
}