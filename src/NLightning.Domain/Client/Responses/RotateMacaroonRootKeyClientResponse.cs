namespace NLightning.Domain.Client.Responses;

/// <summary>The rotated LND gRPC macaroon root key 0 (<c>ClientCommand.RotateMacaroonRootKey</c>, NL-1389).</summary>
/// <param name="DataDirectory">The LND gRPC data directory, where the default macaroons are.</param>
/// <param name="InDatabase">True when the root key is kept in the node's database, false when it is a file.</param>
/// <param name="RebakedDefaults">True when <c>admin</c>, <c>readonly</c> and <c>invoice.macaroon</c> were baked
/// again under the new key.</param>
public sealed record RotateMacaroonRootKeyClientResponse(string DataDirectory, bool InDatabase, bool RebakedDefaults);