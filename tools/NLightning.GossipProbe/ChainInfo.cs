using System.Diagnostics;
using NBitcoin.RPC;

namespace NLightning.GossipProbe;

/// <summary>bitcoind's <c>getblockchaininfo</c>, the fields the probe reports (no credentials).</summary>
public sealed record ChainInfo(string Chain, uint Blocks, uint Headers, bool InitialBlockDownload, bool Pruned,
                               double VerificationProgress)
{
    private static readonly Lock s_gate = new();
    private static RPCClient? s_client;

    public static async Task<ChainInfo> ReadAsync(RpcSettings settings)
    {
        RPCClient client;
        lock (s_gate)
            client = s_client ??= settings.CreateClient();

        var response = await client.SendCommandAsync(RPCOperations.getblockchaininfo);
        var r = response.Result;
        return new ChainInfo(r.Value<string>("chain") ?? "", r.Value<uint>("blocks"), r.Value<uint>("headers"),
                             r.Value<bool>("initialblockdownload"), r.Value<bool>("pruned"),
                             r.Value<double>("verificationprogress"));
    }

    /// <summary>The <c>chaininfo</c> command: can this process reach bitcoind (NL-276), and how fast.</summary>
    public static async Task<int> RunCheckAsync(ProbeOptions options)
    {
        var settings = RpcSettings.Load(options.RpcEnvFile);
        var watch = Stopwatch.StartNew();
        try
        {
            var info = await ReadAsync(settings);
            Console.WriteLine($"getblockchaininfo in {watch.ElapsedMilliseconds} ms: {info}");
            var client = settings.CreateClient();
            watch.Restart();
            var hash = await client.GetBlockHashAsync((int)info.Blocks - 100);
            var block = await client.SendCommandAsync(RPCOperations.getblock, hash.ToString(), 1);
            Console.WriteLine($"getblockhash + getblock {info.Blocks - 100} 1 in {watch.ElapsedMilliseconds} ms: "
                            + $"{block.Result["tx"]?.Count()} txids");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"bitcoind is not reachable from this process after {watch.ElapsedMilliseconds} ms: "
                            + $"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }
}