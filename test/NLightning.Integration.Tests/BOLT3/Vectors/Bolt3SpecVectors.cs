using System.Globalization;
using System.Text.Json;

namespace NLightning.Integration.Tests.BOLT3.Vectors;

/// <summary>
/// One second-stage HTLC transaction vector, in commitment output order.
/// </summary>
/// <param name="OutputIndex">The commitment output it spends.</param>
/// <param name="HtlcId">The Appendix C HTLC number (null for Appendix F, which does not name them).</param>
/// <param name="IsSuccess">True for HTLC-success, false for HTLC-timeout (null for Appendix F).</param>
/// <param name="RemoteSigHex">DER <c>remote_htlc_signature</c> without the sighash flag.</param>
/// <param name="LocalSigHex">DER <c>local_htlc_signature</c> without the sighash flag (null for Appendix F).</param>
/// <param name="TxHex">The fully signed transaction.</param>
public sealed record Bolt3HtlcTxVector(int OutputIndex, int? HtlcId, bool? IsSuccess, string RemoteSigHex,
                                       string? LocalSigHex, string TxHex);

/// <summary>
/// One BOLT 3 Appendix C or F commitment vector.
/// </summary>
/// <param name="Name">The spec's <c>name</c>.</param>
/// <param name="ToLocalMsat">Net local balance (every HTLC already taken out; fee and anchors not).</param>
/// <param name="ToRemoteMsat">Net remote balance.</param>
/// <param name="FeeRatePerKw">The commitment feerate.</param>
/// <param name="DustLimitSatoshis">The holder's (local) dust limit.</param>
/// <param name="HtlcIds">The Appendix C HTLCs (0-6) committed to.</param>
/// <param name="CommitTxHex">The fully signed commitment transaction.</param>
/// <param name="RemoteSigHex">DER remote commitment signature without the sighash flag.</param>
/// <param name="LocalSigHex">DER local commitment signature (null for Appendix F; read it from the witness).</param>
/// <param name="HtlcTxs">The HTLC transactions, in commitment output order.</param>
public sealed record Bolt3CommitmentVector(string Name, ulong ToLocalMsat, ulong ToRemoteMsat, ulong FeeRatePerKw,
                                           ulong DustLimitSatoshis, IReadOnlyList<int> HtlcIds, string CommitTxHex,
                                           string RemoteSigHex, string? LocalSigHex,
                                           IReadOnlyList<Bolt3HtlcTxVector> HtlcTxs)
{
    public override string ToString() => Name;
}

/// <summary>
/// Loads the BOLT 3 Appendix C and F commitment/HTLC vectors from the verbatim spec copies in
/// <c>BOLT3/Vectors/appendix-c.txt</c> and <c>appendix-f.json</c> (lightning/bolts 03-transactions.md @
/// 1aadb719b4007c4cea0ba6e36b08c4fb53788dee).
/// </summary>
public static class Bolt3SpecVectors
{
    private const ulong AppendixCDustLimitSatoshis = 546;
    private const string SameAmountAndPreimageName = "3 htlc outputs, 2 offered having the same amount and preimage";

    private static readonly int[] s_testHtlcIds = [0, 1, 2, 3, 4];
    private static readonly int[] s_sameAmountHtlcIds = [1, 5, 6];

    private static readonly Lazy<IReadOnlyList<Bolt3CommitmentVector>> s_appendixC = new(LoadAppendixC);
    private static readonly Lazy<IReadOnlyList<Bolt3CommitmentVector>> s_appendixF = new(LoadAppendixF);

    public static IReadOnlyList<Bolt3CommitmentVector> AppendixC => s_appendixC.Value;
    public static IReadOnlyList<Bolt3CommitmentVector> AppendixF => s_appendixF.Value;

    public static TheoryData<string> AppendixCNames => ToTheoryData(AppendixC);
    public static TheoryData<string> AppendixFNames => ToTheoryData(AppendixF);

    public static Bolt3CommitmentVector GetAppendixC(string name) => AppendixC.Single(v => v.Name == name);
    public static Bolt3CommitmentVector GetAppendixF(string name) => AppendixF.Single(v => v.Name == name);

    private static TheoryData<string> ToTheoryData(IEnumerable<Bolt3CommitmentVector> vectors)
    {
        var data = new TheoryData<string>();
        foreach (var vector in vectors)
            data.Add(vector.Name);
        return data;
    }

    private static IReadOnlyList<int> HtlcIdsFor(string name, bool usesHtlcs)
    {
        if (!usesHtlcs)
            return [];

        return name.Contains(SameAmountAndPreimageName, StringComparison.Ordinal) ? s_sameAmountHtlcIds : s_testHtlcIds;
    }

    private static string VectorPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "BOLT3", "Vectors", fileName);

    private static IReadOnlyList<Bolt3CommitmentVector> LoadAppendixC()
    {
        var vectors = new List<Bolt3CommitmentVector>();
        CommitmentBuilder? current = null;
        HtlcBuilder? currentHtlc = null;

        foreach (var rawLine in File.ReadLines(VectorPath("appendix-c.txt")))
        {
            var line = rawLine.Trim();
            if (TryValue(line, "name:", out var name))
            {
                Flush();
                current = new CommitmentBuilder { Name = name };
                continue;
            }

            if (current is null)
                continue;

            if (TryValue(line, "to_local_msat:", out var value))
                current.ToLocalMsat = ulong.Parse(value, CultureInfo.InvariantCulture);
            else if (TryValue(line, "to_remote_msat:", out value))
                current.ToRemoteMsat = ulong.Parse(value, CultureInfo.InvariantCulture);
            else if (TryValue(line, "local_feerate_per_kw:", out value))
                current.FeeRatePerKw = ulong.Parse(value, CultureInfo.InvariantCulture);
            else if (TryValue(line, "remote_signature =", out value))
                current.RemoteSigHex = value;
            else if (TryValue(line, "# local_signature =", out value))
                current.LocalSigHex = value;
            else if (TryValue(line, "output commit_tx:", out value))
                current.CommitTxHex = value;
            else if (TryValue(line, "# signature for output #", out value))
            {
                // "<n> (htlc-success for htlc #<k>)"
                currentHtlc = new HtlcBuilder
                {
                    OutputIndex = int.Parse(value[..value.IndexOf(' ')], CultureInfo.InvariantCulture),
                    IsSuccess = value.Contains("htlc-success", StringComparison.Ordinal),
                    HtlcId = int.Parse(value[(value.LastIndexOf('#') + 1)..].TrimEnd(')'),
                                       CultureInfo.InvariantCulture)
                };
            }
            else if (TryValue(line, "remote_htlc_signature =", out value))
                currentHtlc!.RemoteSigHex = value;
            else if (TryValue(line, "# local_htlc_signature =", out value))
                currentHtlc!.LocalSigHex = value;
            else if (line.StartsWith("htlc_success_tx", StringComparison.Ordinal)
                  || line.StartsWith("htlc_timeout_tx", StringComparison.Ordinal))
            {
                var htlc = currentHtlc!;
                htlc.TxHex = line[(line.IndexOf("):", StringComparison.Ordinal) + 2)..].Trim();
                current.HtlcTxs.Add(new Bolt3HtlcTxVector(htlc.OutputIndex, htlc.HtlcId, htlc.IsSuccess,
                                                          htlc.RemoteSigHex!, htlc.LocalSigHex, htlc.TxHex));
                currentHtlc = null;
            }
        }

        Flush();
        return vectors;

        void Flush()
        {
            if (current is null)
                return;

            var usesHtlcs = !current.Name!.Contains("no HTLCs", StringComparison.Ordinal);
            vectors.Add(new Bolt3CommitmentVector(current.Name, current.ToLocalMsat, current.ToRemoteMsat,
                                                  current.FeeRatePerKw, AppendixCDustLimitSatoshis,
                                                  HtlcIdsFor(current.Name, usesHtlcs), current.CommitTxHex!,
                                                  current.RemoteSigHex!, current.LocalSigHex, current.HtlcTxs));
            current = null;
        }
    }

    private static IReadOnlyList<Bolt3CommitmentVector> LoadAppendixF()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorPath("appendix-f.json")));
        var vectors = new List<Bolt3CommitmentVector>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = element.GetProperty("Name").GetString()!;
            var htlcTxs = element.GetProperty("HtlcDescs").EnumerateArray()
                                 .Select(h => (Remote: h.GetProperty("RemoteSigHex").GetString()!,
                                               Tx: h.GetProperty("ResolutionTxHex").GetString()!))
                                 .Select(h => new Bolt3HtlcTxVector(OutputIndexOf(h.Tx), null, null, h.Remote, null,
                                                                    h.Tx))
                                 .ToList();
            vectors.Add(new Bolt3CommitmentVector(name, element.GetProperty("LocalBalance").GetUInt64(),
                                                  element.GetProperty("RemoteBalance").GetUInt64(),
                                                  element.GetProperty("FeePerKw").GetUInt64(),
                                                  element.GetProperty("DustLimitSatoshis").GetUInt64(),
                                                  HtlcIdsFor(name, element.GetProperty("UseTestHtlcs").GetBoolean()),
                                                  element.GetProperty("ExpectedCommitmentTxHex").GetString()!,
                                                  element.GetProperty("RemoteSigHex").GetString()!, null, htlcTxs));
        }

        return vectors;
    }

    private static int OutputIndexOf(string txHex)
    {
        var tx = NBitcoin.Transaction.Parse(txHex, NBitcoin.Network.Main);
        return (int)tx.Inputs[0].PrevOut.N;
    }

    private static bool TryValue(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = line[prefix.Length..].Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private sealed class CommitmentBuilder
    {
        public string? Name { get; init; }
        public ulong ToLocalMsat { get; set; }
        public ulong ToRemoteMsat { get; set; }
        public ulong FeeRatePerKw { get; set; }
        public string? RemoteSigHex { get; set; }
        public string? LocalSigHex { get; set; }
        public string? CommitTxHex { get; set; }
        public List<Bolt3HtlcTxVector> HtlcTxs { get; } = [];
    }

    private sealed class HtlcBuilder
    {
        public int OutputIndex { get; init; }
        public int HtlcId { get; init; }
        public bool IsSuccess { get; init; }
        public string? RemoteSigHex { get; set; }
        public string? LocalSigHex { get; set; }
        public string? TxHex { get; set; }
    }
}