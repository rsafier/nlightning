using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Domain.Crypto.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// Loads the BIP 327 vector files (<c>Vectors/</c>, verbatim) and checks a case's expected error the way the BIP's
/// reference test runner does (<c>get_error_details</c>): an invalid contribution by signer index and contribution, a
/// value error by its exact message.
/// </summary>
internal static class Musig2VectorKit
{
    private static readonly Dictionary<string, JsonElement> s_files = [];
    private static readonly Lock s_lock = new();

    public static JsonElement Load(string fileName)
    {
        lock (s_lock)
        {
            if (s_files.TryGetValue(fileName, out var cached))
                return cached;

            var path = Path.Combine(AppContext.BaseDirectory, "Crypto", "Musig2", "Vectors", fileName);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement.Clone();
            s_files[fileName] = root;
            return root;
        }
    }

    /// <summary>
    /// The indexes of a vector file's case array, one theory row each.
    /// </summary>
    public static TheoryData<int> CaseIndexes(string fileName, string arrayName)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Load(fileName).GetProperty(arrayName).GetArrayLength(); i++)
            data.Add(i);

        return data;
    }

    public static JsonElement Case(string fileName, string arrayName, int index) =>
        Load(fileName).GetProperty(arrayName)[index];

    public static byte[] Hex(JsonElement element) => Convert.FromHexString(element.GetString()!);

    public static byte[]? HexOrNull(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : Hex(element);

    public static byte[][] HexArray(JsonElement element) => element.EnumerateArray().Select(Hex).ToArray();

    public static int[] Ints(JsonElement element) => element.EnumerateArray().Select(e => e.GetInt32()).ToArray();

    public static bool[] Bools(JsonElement element) => element.EnumerateArray().Select(e => e.GetBoolean()).ToArray();

    public static T[] Pick<T>(T[] values, int[] indexes) => indexes.Select(i => values[i]).ToArray();

    /// <summary>
    /// The tweaks of a case that lists them by index into the file's <c>tweaks</c>.
    /// </summary>
    public static (byte[] Tweak, bool IsXOnly)[] Tweaks(byte[][] tweaks, JsonElement testCase) =>
        Ints(testCase.GetProperty("tweak_indices")).Zip(Bools(testCase.GetProperty("is_xonly")))
                                                   .Select(t => (tweaks[t.First], t.Second)).ToArray();

    public static MusigTweak[] ToMusigTweaks(IEnumerable<(byte[] Tweak, bool IsXOnly)> tweaks) =>
        tweaks.Select(t => new MusigTweak(t.Tweak, t.IsXOnly)).ToArray();

    public static CompactPubKey[] ToPubKeys(IEnumerable<byte[]> pubKeys) =>
        pubKeys.Select(k => (CompactPubKey)k).ToArray();

    /// <summary>
    /// Asserts that <paramref name="act"/> fails as the case's <c>error</c> says.
    /// </summary>
    public static void AssertError(JsonElement error, Action act)
    {
        switch (error.GetProperty("type").GetString())
        {
            case "invalid_contribution":
                var contribution = Assert.Throws<MusigInvalidContributionException>(act);
                var signer = error.GetProperty("signer");
                Assert.Equal(signer.ValueKind == JsonValueKind.Null ? null : signer.GetInt32(), contribution.Signer);
                if (error.TryGetProperty("contrib", out var contrib))
                    Assert.Equal(ToContribution(contrib.GetString()!), contribution.Contribution);
                break;
            case "value":
                var value = Assert.Throws<MusigException>(act);
                Assert.Equal(error.GetProperty("message").GetString(), value.Message);
                break;
            default:
                Assert.Fail($"Unknown error type in the vector: {error}");
                break;
        }
    }

    private static MusigContribution ToContribution(string contrib) => contrib switch
    {
        "pubkey" => MusigContribution.PubKey,
        "pubnonce" => MusigContribution.PubNonce,
        "aggnonce" => MusigContribution.AggNonce,
        "psig" => MusigContribution.PartialSignature,
        "aggothernonce" => MusigContribution.AggOtherNonce,
        _ => throw new ArgumentOutOfRangeException(nameof(contrib), contrib, "Unknown contribution in the vector.")
    };
}