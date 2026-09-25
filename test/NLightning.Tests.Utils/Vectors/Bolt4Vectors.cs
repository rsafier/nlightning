using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NLightning.Tests.Utils.Vectors;

/// <summary>
/// A single hop of the BOLT 4 <c>onion-test.json</c> <c>generate.hops</c> section.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed record Bolt4OnionTestHop(byte[] PubKey, byte[] Payload);

/// <summary>
/// The typed contents of the BOLT 4 <c>onion-test.json</c> vector file.
/// </summary>
/// <param name="SessionKey"><c>generate.session_key</c></param>
/// <param name="AssociatedData"><c>generate.associated_data</c></param>
/// <param name="Hops"><c>generate.hops</c></param>
/// <param name="Onion">The expected 1366-byte onion packet (<c>onion</c>)</param>
/// <param name="DecodePrivateKeys">The hop private keys used to peel the onion (<c>decode</c>)</param>
[ExcludeFromCodeCoverage]
public sealed record Bolt4OnionTestVector(
    byte[] SessionKey,
    byte[] AssociatedData,
    IReadOnlyList<Bolt4OnionTestHop> Hops,
    byte[] Onion,
    IReadOnlyList<byte[]> DecodePrivateKeys);

/// <summary>
/// A single hop of the BOLT 4 <c>onion-error-test.json</c> <c>generate.hops</c> section.
/// </summary>
/// <param name="Version">The legacy <c>version</c> field (always 0)</param>
/// <param name="PubKey"><c>pubkey</c></param>
/// <param name="SharedSecret"><c>hop_shared_secret</c></param>
/// <param name="AmmagKey"><c>ammag_key</c></param>
/// <param name="UmKey"><c>um_key</c>; only present for the erring (final) hop</param>
/// <param name="Payload">
/// The unencrypted failure packet body without the HMAC (<c>payload</c>); only present for the erring hop
/// </param>
[ExcludeFromCodeCoverage]
public sealed record Bolt4OnionErrorTestHop(
    int Version,
    byte[] PubKey,
    byte[] SharedSecret,
    byte[] AmmagKey,
    byte[]? UmKey,
    byte[]? Payload);

/// <summary>
/// The typed contents of the BOLT 4 <c>onion-error-test.json</c> vector file.
/// </summary>
/// <param name="SessionKey"><c>generate.session_key</c></param>
/// <param name="FailureMessage"><c>generate.failure_message</c></param>
/// <param name="Hops"><c>generate.hops</c></param>
/// <param name="ErrorPacket">The fully wrapped error packet the origin node receives (<c>errorpacket</c>)</param>
[ExcludeFromCodeCoverage]
public sealed record Bolt4OnionErrorTestVector(
    byte[] SessionKey,
    byte[] FailureMessage,
    IReadOnlyList<Bolt4OnionErrorTestHop> Hops,
    byte[] ErrorPacket);

/// <summary>
/// Loader and shared constants for the official BOLT 4 JSON test vectors.
/// </summary>
/// <remarks>
/// The JSON files live in <c>test/NLightning.Integration.Tests/BOLT4/Vectors/</c> and are copied to the test output
/// directory, so the relative paths below resolve against <see cref="AppContext.BaseDirectory"/>.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class Bolt4Vectors
{
    public const string OnionTestPath = "BOLT4/Vectors/onion-test.json";
    public const string OnionErrorTestPath = "BOLT4/Vectors/onion-error-test.json";
    public const string RouteBlindingTestPath = "BOLT4/Vectors/route-blinding-test.json";
    public const string BlindedPaymentOnionTestPath = "BOLT4/Vectors/blinded-payment-onion-test.json";
    public const string BlindedOnionMessageOnionTestPath = "BOLT4/Vectors/blinded-onion-message-onion-test.json";

    /// <summary>
    /// The session key used by <c>onion-test.json</c> and <c>onion-error-test.json</c> (0x41 repeated 32 times).
    /// </summary>
    public static readonly byte[] SessionKey = Enumerable.Repeat((byte)0x41, 32).ToArray();

    /// <summary>
    /// The associated data (payment hash) used by <c>onion-test.json</c> (0x42 repeated 32 times).
    /// </summary>
    public static readonly byte[] AssociatedData = Enumerable.Repeat((byte)0x42, 32).ToArray();

    public static Bolt4OnionTestVector LoadOnionTest(string path = OnionTestPath)
    {
        using var document = LoadDocument(path);
        var root = document.RootElement;
        var generate = GetRequired(root, "generate");

        var hops = GetRequired(generate, "hops")
                  .EnumerateArray()
                  .Select(hop => new Bolt4OnionTestHop(GetHex(hop, "pubkey"), GetHex(hop, "payload")))
                  .ToList();

        var decodeKeys = GetRequired(root, "decode")
                        .EnumerateArray()
                        .Select(key => ParseHex(key, "decode[]"))
                        .ToList();

        return new Bolt4OnionTestVector(GetHex(generate, "session_key"), GetHex(generate, "associated_data"), hops,
                                        GetHex(root, "onion"), decodeKeys);
    }

    public static Bolt4OnionErrorTestVector LoadOnionErrorTest(string path = OnionErrorTestPath)
    {
        using var document = LoadDocument(path);
        var root = document.RootElement;
        var generate = GetRequired(root, "generate");

        var hops = GetRequired(generate, "hops")
                  .EnumerateArray()
                  .Select(hop => new Bolt4OnionErrorTestHop(GetRequired(hop, "version").GetInt32(),
                                                            GetHex(hop, "pubkey"),
                                                            GetHex(hop, "hop_shared_secret"),
                                                            GetHex(hop, "ammag_key"),
                                                            GetOptionalHex(hop, "um_key"),
                                                            GetOptionalHex(hop, "payload")))
                  .ToList();

        return new Bolt4OnionErrorTestVector(GetHex(generate, "session_key"), GetHex(generate, "failure_message"),
                                             hops, GetHex(root, "errorpacket"));
    }

    /// <summary>
    /// Loads any BOLT 4 vector file as a raw <see cref="JsonDocument"/>. The caller owns (and must dispose) the result.
    /// </summary>
    public static JsonDocument LoadDocument(string path)
    {
        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        using var stream = File.OpenRead(fullPath);
        return JsonDocument.Parse(stream);
    }

    public static JsonElement GetRequired(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            throw new InvalidDataException($"BOLT 4 vector is missing required property '{propertyName}'");

        return value;
    }

    public static byte[] GetHex(JsonElement element, string propertyName)
    {
        return ParseHex(GetRequired(element, propertyName), propertyName);
    }

    public static byte[]? GetOptionalHex(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) ? ParseHex(value, propertyName) : null;
    }

    private static byte[] ParseHex(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"BOLT 4 vector property '{propertyName}' is not a string");

        return Convert.FromHexString(value.GetString()!);
    }
}