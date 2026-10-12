using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

/// <summary>
/// The simple taproot channel vectors of bolt-simple-taproot.md (<c>Taproot/Vectors/simple-taproot-vectors.json</c>,
/// a verbatim copy), read once.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class SimpleTaprootVectors
{
    private static readonly Lazy<JsonDocument> s_document = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Taproot", "Vectors", "simple-taproot-vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    });

    public static JsonElement Root => s_document.Value.RootElement;
    public static JsonElement Params => Root.GetProperty("params");
    public static JsonElement Keys => Params.GetProperty("keys");
    public static JsonElement Scripts => Root.GetProperty("scripts");

    public static ulong FundingSatoshis => Params.GetProperty("funding_amount_satoshis").GetUInt64();
    public static ulong DustLimitSatoshis => Params.GetProperty("dust_limit_satoshis").GetUInt64();
    public static ushort CsvDelay => checked((ushort)Params.GetProperty("csv_delay").GetUInt32());
    public static ulong CommitHeight => Params.GetProperty("commit_height").GetUInt64();

    public static byte[] Hex(JsonElement element, string name) => Convert.FromHexString(element.GetProperty(name)
                                                                                                  .GetString()!);

    public static string HexString(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!.ToLowerInvariant();

    public static byte[] Key(string name) => Hex(Keys, name);
    public static PubKey PubKey(string name) => new(Key(name));
    public static Key PrivKey(string name) => new(Key(name));

    /// <summary>The funding transaction of the vectors (one output, the taproot funding output, at index 0).</summary>
    public static Transaction FundingTransaction =>
        Transaction.Parse(Scripts.GetProperty("funding").GetProperty("funding_tx_hex").GetString()!, Network.Main);

    public static IReadOnlyList<TransactionCase> Transactions =>
        Root.GetProperty("transactions").EnumerateArray().Select(TransactionCase.FromJson).ToList();

    /// <summary>One of the three commitment transaction vectors.</summary>
    internal sealed record TransactionCase(
        string Name,
        ulong LocalBalanceMsat,
        ulong RemoteBalanceMsat,
        ulong FeePerKw,
        ulong DustLimitSatoshis,
        IReadOnlyList<HtlcCase> Htlcs,
        string ExpectedCommitmentTxHex,
        IReadOnlyList<HtlcDesc> HtlcDescs)
    {
        public static TransactionCase FromJson(JsonElement element)
        {
            var htlcs = element.GetProperty("htlcs").ValueKind == JsonValueKind.Array
                            ? element.GetProperty("htlcs").EnumerateArray()
                                     .Select((h, i) => new HtlcCase(i, h.GetProperty("incoming").GetBoolean(),
                                                                    h.GetProperty("amount_msat").GetUInt64(),
                                                                    h.GetProperty("expiry").GetUInt32(),
                                                                    Convert.FromHexString(
                                                                        h.GetProperty("preimage").GetString()!)))
                                     .ToList()
                            : [];
            var descs = element.TryGetProperty("htlc_descs", out var descArray)
                     && descArray.ValueKind == JsonValueKind.Array
                            ? descArray.EnumerateArray()
                                       .Select(d => new HtlcDesc(
                                                   Convert.FromHexString(d.GetProperty("remote_partial_sig_hex")
                                                                          .GetString()!),
                                                   d.GetProperty("resolution_tx_hex").GetString()!))
                                       .ToList()
                            : [];
            var dust = element.TryGetProperty("dust_limit_satoshis", out var dustElement)
                           ? dustElement.GetUInt64()
                           : SimpleTaprootVectors.DustLimitSatoshis;

            return new TransactionCase(element.GetProperty("name").GetString()!,
                                       element.GetProperty("local_balance_msat").GetUInt64(),
                                       element.GetProperty("remote_balance_msat").GetUInt64(),
                                       element.GetProperty("fee_per_kw").GetUInt64(), dust, htlcs,
                                       element.GetProperty("expected_commitment_tx_hex").GetString()!, descs);
        }

        public override string ToString() => Name;
    }

    /// <summary>An HTLC of a transaction vector; "incoming" is from the local node's point of view.</summary>
    internal sealed record HtlcCase(int Id, bool Incoming, ulong AmountMsat, uint Expiry, byte[] Preimage)
    {
        public byte[] PaymentHash => SHA256.HashData(Preimage);
    }

    /// <summary>A second-level HTLC transaction of a vector, in commitment output order.</summary>
    internal sealed record HtlcDesc(byte[] RemoteSignature, string ResolutionTxHex);
}