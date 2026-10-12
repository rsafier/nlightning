using System.Diagnostics;

namespace NLightning.Testing.Lnd.Tests.Docker;

/// <summary>The <c>docker</c> command line, for the Explicit live tests that start their own containers.</summary>
internal static class DockerCli
{
    /// <summary>Runs <c>docker</c> with these arguments; throws with its output when it exits non-zero.</summary>
    public static async Task<string> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var result = await TryRunAsync(cancellationToken, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"docker {string.Join(' ', args)} exited {result.ExitCode}: "
                                              + $"{result.Stderr.Trim()} {result.Stdout.Trim()}");
        return result.Stdout.Trim();
    }

    /// <summary>Runs <c>docker</c> with these arguments and returns its exit code and output.</summary>
    public static async Task<DockerResult> TryRunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
                         ?? throw new InvalidOperationException("Could not start the docker command line.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new DockerResult(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>A finished <c>docker</c> command.</summary>
internal sealed record DockerResult(int ExitCode, string Stdout, string Stderr);