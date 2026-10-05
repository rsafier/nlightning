using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.GossipProbe;

using Application.Gossip.Graph.Interfaces;
using Domain.Gossip.Graph;

/// <summary>
/// The sampled cross-check of <c>Gossip:AssumeChannelValid</c>: for a random sample of the stored channels, reads the
/// funding output named by the short channel id from an Esplora API and compares it with what BOLT 7 requires (a
/// P2WSH of the 2-of-2 of the announcement's bitcoin keys, unspent). Also compares the capacity estimate
/// (<see cref="GraphChannel.EstimatedCapacityMsat"/>, the larger <c>htlc_maximum_msat</c>) with the real amount.
/// </summary>
public static class EsploraVerifier
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var runDirectory = Path.Combine(options.Directory, "runs",
                                        $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-verify");
        Directory.CreateDirectory(runDirectory);
        using var loggerProvider = new ProbeLoggerProvider(Path.Combine(runDirectory, "probe.log"), options.LogLevel);
        await using var node = new ProbeNode(options, loggerProvider, new PeerTraffic(),
                                             await EsploraClient.GetTipHeightAsync(options.EsploraUrl));
        node.Build(1);
        await node.MigrateAsync(cancellationToken);
        var store = node.Services.GetRequiredService<IGraphStore>();
        await store.LoadAsync(cancellationToken);
        var channels = store.GetSnapshot().Channels.ToList();
        var sample = channels.OrderBy(_ => Random.Shared.Next()).Take(options.VerifySample).ToList();
        Console.WriteLine($"Verifying {sample.Count} of {channels.Count} stored channels against {options.EsploraUrl}");

        using var esplora = new EsploraClient(options.EsploraUrl, options.VerifyRequestsPerSecond);
        var blockHashes = new Dictionary<uint, string>();
        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        var ratios = new List<double>();
        await using var csv = new StreamWriter(Path.Combine(runDirectory, "verify.csv"));
        await csv.WriteLineAsync("scid,verification,policies,newest_policy_age_days,result,amount_sat,"
                               + "estimated_capacity_sat,estimate_ratio,txid");
        var byPolicies = new Dictionary<string, int>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var checkedCount = 0;
        foreach (var channel in sample)
        {
            string result;
            long? amount = null;
            string? txid = null;
            try
            {
                (result, amount, txid) = await CheckAsync(esplora, blockHashes, channel, cancellationToken);
            }
            catch (EsploraClient.RateLimitedException)
            {
                Console.WriteLine("HTTP 429: stopping the verification");
                results["stopped_rate_limited"] = 1;
                break;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                result = "error";
                Console.WriteLine($"{channel.ShortChannelId}: {e.Message}");
            }

            checkedCount++;
            results[result] = results.GetValueOrDefault(result) + 1;
            var policyCount = (channel.Policy1 is null ? 0 : 1) + (channel.Policy2 is null ? 0 : 1);
            var newest = Math.Max(channel.Policy1?.Timestamp ?? 0, channel.Policy2?.Timestamp ?? 0);
            double? ageDays = newest == 0 ? null : (now - newest) / 86_400.0;
            var bucket = policyCount == 0 ? "no_policy" : ageDays > 14 ? "stale_policies" : "fresh_policy";
            byPolicies[$"{bucket}:{result}"] = byPolicies.GetValueOrDefault($"{bucket}:{result}") + 1;
            var estimate = channel.EstimatedCapacityMsat / 1000;
            double? ratio = amount is > 0 && estimate is { } e2 ? (double)e2 / amount.Value : null;
            if (ratio is { } r && result == "valid_unspent")
                ratios.Add(r);
            await csv.WriteLineAsync(string.Join(',', channel.ShortChannelId.ToString(), channel.Verification,
                                                 policyCount.ToString(CultureInfo.InvariantCulture),
                                                 ageDays?.ToString("F1", CultureInfo.InvariantCulture) ?? "", result,
                                                 amount?.ToString(CultureInfo.InvariantCulture) ?? "",
                                                 estimate?.ToString(CultureInfo.InvariantCulture) ?? "",
                                                 ratio?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                                                 txid ?? ""));
            if (checkedCount % 25 == 0)
                Console.WriteLine($"{checkedCount}/{sample.Count}: {string.Join(", ", results.Select(p => $"{p.Key} {p.Value}"))}");
        }

        ratios.Sort();
        var summary = new Dictionary<string, object?>
        {
            ["stored_channels"] = channels.Count,
            ["sampled"] = sample.Count,
            ["checked"] = checkedCount,
            ["requests"] = esplora.Requests,
            ["results"] = results,
            ["results_by_policy_freshness"] = new SortedDictionary<string, int>(byPolicies, StringComparer.Ordinal),
            ["estimate_ratio_count"] = ratios.Count,
            ["estimate_ratio_p10"] = Percentile(ratios, 0.10),
            ["estimate_ratio_median"] = Percentile(ratios, 0.50),
            ["estimate_ratio_p90"] = Percentile(ratios, 0.90),
            ["estimate_ratio_above_1"] = ratios.Count(r => r > 1.0000001)
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "verify-summary.json"), json, cancellationToken);
        Console.WriteLine(json);
        return 0;
    }

    private static async Task<(string Result, long? Amount, string? TxId)> CheckAsync(
        EsploraClient esplora, Dictionary<uint, string> blockHashes, GraphChannel channel,
        CancellationToken cancellationToken)
    {
        var scid = channel.ShortChannelId;
        if (!blockHashes.TryGetValue(scid.BlockHeight, out var blockHash))
        {
            var hash = await esplora.TryGetStringAsync($"/block-height/{scid.BlockHeight}", cancellationToken);
            if (hash is null)
                return ("no_block", null, null);
            blockHashes[scid.BlockHeight] = blockHash = hash;
        }

        var txid = await esplora.TryGetStringAsync($"/block/{blockHash}/txid/{scid.TransactionIndex}",
                                                   cancellationToken);
        if (txid is null)
            return ("no_transaction_at_index", null, null);

        var tx = await esplora.TryGetJsonAsync($"/tx/{txid}", cancellationToken);
        if (tx is not { } transaction)
            return ("no_transaction", null, txid);

        var outputs = transaction.GetProperty("vout");
        if (scid.OutputIndex >= outputs.GetArrayLength())
            return ("no_output_at_index", null, txid);

        var output = outputs[scid.OutputIndex];
        var amount = output.GetProperty("value").GetInt64();
        var scriptHex = output.GetProperty("scriptpubkey").GetString() ?? "";
        if (!string.Equals(scriptHex, ExpectedScriptPubKey(channel), StringComparison.OrdinalIgnoreCase))
            return ("script_mismatch", amount, txid);

        var outspend = await esplora.TryGetJsonAsync($"/tx/{txid}/outspend/{scid.OutputIndex}", cancellationToken);
        var spent = outspend is { } o && o.TryGetProperty("spent", out var s) && s.GetBoolean();
        return (spent ? "valid_spent" : "valid_unspent", amount, txid);
    }

    /// <summary>P2WSH(2 &lt;key1&gt; &lt;key2&gt; 2 OP_CHECKMULTISIG) with the keys in BOLT 3 order (lexicographic).</summary>
    private static string ExpectedScriptPubKey(GraphChannel channel)
    {
        // A BOLT 7 channel always has both keys (only a channel_announcement_2 may leave them out, NL-878)
        var keys = new[]
                   {
                       new PubKey((byte[])channel.BitcoinKey1!.Value), new PubKey((byte[])channel.BitcoinKey2!.Value)
                   }
                  .OrderBy(k => k.ToHex(), StringComparer.Ordinal)
                  .ToArray();
        var redeem = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, keys);
        return redeem.WitHash.ScriptPubKey.ToHex();
    }

    private static double? Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? null : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
}