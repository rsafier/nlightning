using Docker.DotNet;
using Docker.DotNet.Models;

namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// Failure diagnostics for the Docker tests. The in-process nodes already log to the test output (prefixed with
/// their name); this adds the container logs.
/// </summary>
public static class DockerDiagnostics
{
    /// <summary>
    /// Whether the current test has failed. Valid in the test class's <c>DisposeAsync</c> (xUnit sets the result
    /// before cleanup).
    /// </summary>
    public static bool CurrentTestFailed => TestContext.Current.TestState?.Result == TestResult.Failed;

    /// <summary>
    /// Writes the last <paramref name="tail"/> log lines of each container to <see cref="Console"/> (the test
    /// output), when the current test failed.
    /// </summary>
    public static async Task DumpContainerLogsIfFailedAsync(IEnumerable<string> containerNames, int tail = 300)
    {
        if (CurrentTestFailed)
            await DumpContainerLogsAsync(containerNames, tail);
    }

    /// <summary>
    /// Writes the last <paramref name="tail"/> log lines of each container to <see cref="Console"/>.
    /// </summary>
    public static async Task DumpContainerLogsAsync(IEnumerable<string> containerNames, int tail = 300)
    {
        using var client = new DockerClientConfiguration().CreateClient();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var name in containerNames)
        {
            try
            {
                var parameters = new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Timestamps = true,
                    Tail = tail.ToString()
                };
                using var stream = await client.Containers.GetContainerLogsAsync(name, false, parameters,
                                                                                 timeoutCts.Token);
                var (stdout, stderr) = await stream.ReadOutputToEndAsync(timeoutCts.Token);
                Console.WriteLine($"===== docker logs {name} (last {tail} lines) =====");
                Console.WriteLine(stdout);
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.WriteLine(stderr);
            }
            catch (Exception e)
            {
                Console.WriteLine($"===== docker logs {name}: unavailable ({e.Message}) =====");
            }
        }
    }
}