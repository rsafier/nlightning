using System.Runtime.InteropServices;

namespace NLightning.GossipProbe;

/// <summary>The probe's entry point: <c>run</c> (the mainnet gossip probe) or <c>verify</c> (the Esplora cross-check).</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = ProbeOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(ProbeOptions.Usage);
            return 2;
        }

        Directory.CreateDirectory(options.Directory);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C: stop gracefully (flush the graph, write the summary)
            e.Cancel = true;
            cts.Cancel();
        };

        // SIGTERM too: a probe started in the background of a non-interactive shell ignores SIGINT
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });

        return options.Command switch
        {
            "verify" => await EsploraVerifier.RunAsync(options, cts.Token),
            "chaininfo" => await ChainInfo.RunCheckAsync(options),
            _ => await new ProbeRun(options).RunAsync(cts.Token)
        };
    }
}