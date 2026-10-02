using System.Text.RegularExpressions;

namespace NLightning.Testing.Cluster.Diagnostics;

/// <summary>
/// Masks secrets in everything a dump writes: values after a password, secret, token, macaroon or <c>rpcauth</c> key
/// (command lines, env values, config echoes in logs). The dump never reads secret files (macaroons,
/// <c>hsm_secret</c>, TLS keys) in the first place; this is the second line.
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>What a masked value is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>Whether an env var or option named <paramref name="name"/> holds a secret.</summary>
    public static bool IsSecretName(string? name) => name is not null && SecretName().IsMatch(name);

    /// <summary><paramref name="text"/> with every <c>key=value</c> / <c>key: value</c> secret masked.</summary>
    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? string.Empty : SecretAssignment().Replace(text, $"$1$2{Mask}");

    // A key that names a secret, optionally followed by more key characters (rpcpassword, bitcoind.rpcpass,
    // macaroonpath is a path, not a secret, but masking it costs nothing).
    [GeneratedRegex(@"(?i)(?:pass(?:word|wd|phrase)?|secret|token|macaroon|rpcauth|api[-_]?key|private[-_]?key|seed)")]
    private static partial Regex SecretName();

    [GeneratedRegex(
        @"(?i)([A-Za-z0-9_.\-]*(?:pass(?:word|wd|phrase)?|secret|token|macaroon|rpcauth|api[-_]?key|private[-_]?key)[A-Za-z0-9_.\-]*)(""?\s*[=:]\s*""?)(?!\*\*\*)([^\s""',;&]+)")]
    private static partial Regex SecretAssignment();
}