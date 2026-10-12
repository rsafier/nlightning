namespace NLightning.Testing.Cluster.Cli;

using Run;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await ClusterCli.RunAsync(args, Console.Out, Console.Error, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return ClusterCli.Failed;
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"nltg-cluster: {e.GetType().Name}: {e.Message}");
            return ClusterCli.Failed;
        }
    }
}