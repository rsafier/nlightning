using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Configuration;

/// <summary>Node-side transport settings. The remote daemon exclusively owns the encrypted key file.</summary>
public sealed class SigningOptions
{
    public const string SectionName = "Signing";

    public string Mode { get; set; } = "Local";
    public string SocketPath { get; set; } = string.Empty;
    public string AuthTokenFile { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
    public string? ExpectedNodePublicKey { get; set; }

    public bool IsRemote => string.Equals(Mode, "RemoteNative", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (!IsRemote && !string.Equals(Mode, "Local", StringComparison.OrdinalIgnoreCase))
            errors.Add("Signing:Mode must be Local or RemoteNative.");
        if (!IsRemote)
            return errors;

        if (!Path.IsPathFullyQualified(SocketPath))
            errors.Add("Signing:SocketPath must be an absolute Unix socket path in RemoteNative mode.");
        if (!Path.IsPathFullyQualified(AuthTokenFile))
            errors.Add("Signing:AuthTokenFile must be an absolute path in RemoteNative mode.");
        if (TimeoutSeconds is < 1 or > 300)
            errors.Add("Signing:TimeoutSeconds must be between 1 and 300.");
        if (ExpectedNodePublicKey is { } publicKey
            && (publicKey.Length != 66 || !(publicKey.StartsWith("02", StringComparison.Ordinal)
                                            || publicKey.StartsWith("03", StringComparison.Ordinal))
                                      || !publicKey.All(Uri.IsHexDigit)))
            errors.Add("Signing:ExpectedNodePublicKey must be a compressed public key (66 hex characters).");
        return errors;
    }

    internal string ReadAuthToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(AuthTokenFile);
            const UnixFileMode otherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & otherAccess) != 0)
                throw new ArgumentException("Signing:AuthTokenFile must be accessible only to its owner (chmod 600).");
        }
        var token = File.ReadAllText(AuthTokenFile).TrimEnd('\r', '\n');
        if (token.Length < 32 || token.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Signing:AuthTokenFile must contain at least 32 printable ASCII characters.");
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