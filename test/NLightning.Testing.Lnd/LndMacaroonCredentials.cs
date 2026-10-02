// Ported from LNUnit.LND (https://github.com/nbd-wtf/LNUnit, LNDNodeConnection.CreateGrpcConnection), Copyright (c)
// 2024-2025 nbd, MIT License; the full text is in LICENSE-LNUnit.txt next to this file. Changed: the macaroon
// interceptor is its own type so it can be tested and reused.

using Grpc.Core;

namespace NLightning.Testing.Lnd;

/// <summary>LND's <c>macaroon</c> call credentials: every call carries the macaroon as lower-case hex metadata.</summary>
public static class LndMacaroonCredentials
{
    /// <summary>The metadata key LND reads the macaroon from.</summary>
    public const string MetadataKey = "macaroon";

    /// <summary>The interceptor that adds the macaroon header to a call's metadata.</summary>
    public static AsyncAuthInterceptor CreateInterceptor(byte[] macaroon)
    {
        ArgumentNullException.ThrowIfNull(macaroon);
        if (macaroon.Length == 0)
            throw new ArgumentException("The macaroon is empty.", nameof(macaroon));

        var hex = Convert.ToHexStringLower(macaroon);
        return (_, metadata) =>
        {
            metadata.Add(MetadataKey, hex);
            return Task.CompletedTask;
        };
    }

    /// <summary>Call credentials that add the macaroon header to every call.</summary>
    public static CallCredentials Create(byte[] macaroon) => CallCredentials.FromInterceptor(CreateInterceptor(macaroon));
}