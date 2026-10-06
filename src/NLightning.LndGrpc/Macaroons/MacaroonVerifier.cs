using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>How a macaroon check ended.</summary>
public enum MacaroonCheck
{
    /// <summary>The macaroon is ours, intact, unexpired and allows the method.</summary>
    Allowed,

    /// <summary>No usable macaroon: missing, malformed, forged, signed by another root key, expired, or a caveat
    /// that does not hold or is not understood.</summary>
    Unauthenticated,

    /// <summary>A valid macaroon whose operations do not include what the method requires.</summary>
    PermissionDenied
}

/// <summary>The outcome of <see cref="MacaroonVerifier.Check"/>, with the reason for a refusal.</summary>
public readonly record struct MacaroonCheckResult(MacaroonCheck Outcome, string? Reason)
{
    public static MacaroonCheckResult Allow { get; } = new(MacaroonCheck.Allowed, null);

    public static MacaroonCheckResult Unauthenticated(string reason) => new(MacaroonCheck.Unauthenticated, reason);

    public static MacaroonCheckResult Denied(string reason) => new(MacaroonCheck.PermissionDenied, reason);
}

/// <summary>
/// Checks a macaroon the way LND's <c>macaroons.Service.CheckMacAuth</c> does with the bakery's checker: v2 binary,
/// a bakery version 3 identifier, a root key we hold for its storage id, the HMAC chain, every operation the method
/// requires among the macaroon's (or LND's <c>uri:&lt;full method&gt;</c>), and every first-party caveat satisfied.
/// </summary>
/// <remarks>
/// Caveats understood (the bakery's <c>std</c> namespace and LND's registered checkers): <c>time-before
/// &lt;RFC 3339&gt;</c>, <c>ipaddr &lt;ip&gt;</c>, <c>iprange &lt;cidr&gt;</c>. Every other condition (<c>lnd-custom</c>,
/// <c>declared</c>, <c>allow</c>, <c>deny</c>, unknown ones) and every third-party caveat is refused, as the bakery
/// refuses a caveat it does not recognize.
/// </remarks>
public sealed class MacaroonVerifier
{
    private readonly Func<byte[], byte[]?> _rootKeyOf;
    private readonly TimeProvider _timeProvider;

    /// <summary>A verifier of macaroons under one root key, LND's default id <c>0</c>.</summary>
    public MacaroonVerifier(byte[] rootKey, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        var key = (byte[])rootKey.Clone();
        _rootKeyOf = storageId => storageId.AsSpan().SequenceEqual(LndPermissions.DefaultRootKeyId) ? key : null;
        _timeProvider = timeProvider;
    }

    /// <summary>A verifier of macaroons under every root key id of <paramref name="rootKeys"/> (NL-1169).</summary>
    public MacaroonVerifier(LndRootKeyStore rootKeys, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(rootKeys);
        _rootKeyOf = storageId => rootKeys.TryGet(storageId);
        _timeProvider = timeProvider;
    }

    /// <summary>Checks <paramref name="macaroonBytes"/> (v2 binary) for <paramref name="fullMethod"/>.</summary>
    /// <param name="macaroonBytes">The macaroon, already hex-decoded.</param>
    /// <param name="required">The operations the method requires.</param>
    /// <param name="fullMethod">The full gRPC method (<c>/lnrpc.Lightning/GetInfo</c>), for <c>uri:</c>.</param>
    /// <param name="peerAddress">The caller's IP address, for <c>ipaddr</c>/<c>iprange</c> (null: those fail).</param>
    public MacaroonCheckResult Check(ReadOnlySpan<byte> macaroonBytes, IReadOnlyList<MacaroonOp> required,
                                     string fullMethod, IPAddress? peerAddress)
    {
        Macaroon macaroon;
        MacaroonId id;
        try
        {
            macaroon = Macaroon.Deserialize(macaroonBytes);
            id = MacaroonId.Decode(macaroon.Identifier);
        }
        catch (FormatException e)
        {
            return MacaroonCheckResult.Unauthenticated($"invalid macaroon: {e.Message}");
        }

        if (_rootKeyOf(id.StorageId) is not { } rootKey)
            return MacaroonCheckResult.Unauthenticated("macaroon not found in storage");
        if (!macaroon.VerifySignature(rootKey))
            return MacaroonCheckResult.Unauthenticated("verification failed: signature mismatch");

        foreach (var caveat in macaroon.Caveats)
        {
            if (CheckCaveat(caveat.Condition, peerAddress) is { } failure)
                return MacaroonCheckResult.Unauthenticated($"caveat \"{caveat.Condition}\" not satisfied: {failure}");
        }

        var ops = id.Ops.ToHashSet();
        if (required.All(ops.Contains) || ops.Contains(new MacaroonOp(LndPermissions.UriEntity, fullMethod)))
            return MacaroonCheckResult.Allow;

        return MacaroonCheckResult.Denied("permission denied");
    }

    /// <summary>Null when <paramref name="condition"/> holds, else why not.</summary>
    private string? CheckCaveat(string condition, IPAddress? peerAddress)
    {
        // The bakery's ParseCaveat: the condition name up to the first space, the argument after it
        var space = condition.IndexOf(' ');
        var name = space < 0 ? condition : condition[..space];
        var argument = space < 0 ? string.Empty : condition[(space + 1)..];
        switch (name)
        {
            case "time-before":
                if (!DateTimeOffset.TryParse(argument, CultureInfo.InvariantCulture,
                                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                             out var expiry))
                    return "invalid time";

                return _timeProvider.GetUtcNow() < expiry ? null : "macaroon has expired";
            case "ipaddr":
                if (!IPAddress.TryParse(argument, out var locked))
                    return "invalid IP address";

                return peerAddress is not null && Normalize(peerAddress).Equals(Normalize(locked))
                           ? null
                           : "macaroon locked to different IP address";
            case "iprange":
                if (!IPNetwork.TryParse(argument, out var range))
                    return "invalid IP range";

                return peerAddress is not null
                    && (range.Contains(peerAddress) || range.Contains(Normalize(peerAddress)))
                           ? null
                           : "macaroon locked to different IP range";
            default:
                return "caveat not recognized";
        }
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>A fresh 32-byte root key from the CSPRNG.</summary>
    public static byte[] NewRootKey() => RandomNumberGenerator.GetBytes(32);
}