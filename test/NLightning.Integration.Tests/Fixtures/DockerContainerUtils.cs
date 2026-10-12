using Docker.DotNet;
using Docker.DotNet.Models;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// Container and image helpers of the two fixtures left on Docker: the Tor interop fixture (<c>Tor/TorInteropFixture</c>,
/// the one Docker suite) and <see cref="SqlServerFixture"/> (SQL Server tests are not run, plan "SQL Server"). The CLN,
/// Eclair, LDK and Postgres fixtures run on the cluster only since NL-866, and <c>Fixtures/DockerAbsenceTests</c> keeps
/// the Docker API out of every other file. They replaced <c>LNUnit.Setup</c>'s Docker extensions (NL-819).
/// </summary>
internal static class DockerContainerUtils
{
    /// <summary>
    /// Pulls <paramref name="repository"/> at <paramref name="tagOrDigest"/> (a tag, or a <c>sha256:</c> digest)
    /// unless it is already present, and checks that it then is.
    /// </summary>
    /// <remarks>
    /// Unlike LNUnit's <c>PullImageAndWaitForCompleted</c>, which asked the registry on every start, a present image is
    /// used as is: the fixtures pin their tags, so a start works offline and never moves a local tag.
    /// </remarks>
    public static async Task EnsureImageAsync(DockerClient docker, string repository, string tagOrDigest)
    {
        var reference = tagOrDigest.StartsWith("sha256:", StringComparison.Ordinal)
                            ? $"{repository}@{tagOrDigest}"
                            : $"{repository}:{tagOrDigest}";
        if (await ImageExistsAsync(docker, reference))
            return;

        // Docker.DotNet returns once the pull's progress stream has ended
        await docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repository, Tag = tagOrDigest },
                                             null, new Progress<JSONMessage>());
        if (!await ImageExistsAsync(docker, reference))
            throw new InvalidOperationException($"Could not pull {reference}");
    }

    /// <summary>
    /// Whether the image <paramref name="reference"/> (<c>repository:tag</c> or <c>repository@sha256:...</c>) is
    /// present locally.
    /// </summary>
    public static async Task<bool> ImageExistsAsync(DockerClient docker, string reference)
    {
        try
        {
            await docker.Images.InspectImageAsync(reference);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Force-removes a container (and its volumes) by name, ignoring a missing one.
    /// </summary>
    public static async Task RemoveContainerAsync(DockerClient client, string name)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(
                name, new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Creates and starts a container whose <paramref name="containerPort"/> is published on a free
    /// <c>127.0.0.1</c> port, so the tests reach it the same way on OrbStack, Docker Desktop and Linux (the bridge IP
    /// is only routable from the host on OrbStack and Linux).
    /// </summary>
    /// <returns>The host port.</returns>
    public static async Task<int> StartWithLoopbackPortAsync(DockerClient client, string image, string name,
                                                             int containerPort,
                                                             IList<string> env)
    {
        var portKey = $"{containerPort}/tcp";
        var parameters = new CreateContainerParameters
        {
            Image = image,
            Name = name,
            Hostname = name,
            Env = env,
            ExposedPorts = new Dictionary<string, EmptyStruct> { [portKey] = default },
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge",
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    // An empty host port lets Docker pick a free one
                    [portKey] = [new PortBinding { HostIP = "127.0.0.1", HostPort = string.Empty }]
                }
            }
        };

        var container = await client.Containers.CreateContainerAsync(parameters)
                     ?? throw new InvalidOperationException($"Failed to create the {name} container");
        await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters());

        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var inspect = await client.Containers.InspectContainerAsync(container.ID);
            if (inspect.NetworkSettings?.Ports is { } ports
             && ports.TryGetValue(portKey, out var bindings)
             && bindings is { Count: > 0 }
             && int.TryParse(bindings[0].HostPort, out var hostPort)
             && hostPort > 0)
                return hostPort;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Docker did not publish port {portKey} of {name}");

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Retries <paramref name="probe"/> until it succeeds or <paramref name="timeout"/> passes. A published port
    /// accepts TCP connections before the server behind it is up, so the probe has to talk to the server.
    /// </summary>
    public static async Task WaitUntilReadyAsync(string name, Func<CancellationToken, Task> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await probe(attemptCts.Token);
                return;
            }
            catch (Exception e)
            {
                lastError = e;
                await Task.Delay(500);
            }
        }

        throw new TimeoutException($"{name} was not ready after {timeout}", lastError);
    }

    /// <summary>
    /// <c>test/Docker/&lt;name&gt;</c>, found by walking up from the test assembly.
    /// </summary>
    public static string FindDockerDirectory(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "test", "Docker", name);
            if (File.Exists(Path.Combine(candidate, "Dockerfile")))
                return candidate;

            candidate = Path.Combine(dir.FullName, "Docker", name);
            if (File.Exists(Path.Combine(candidate, "Dockerfile")))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"test/Docker/{name}/Dockerfile not found above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// <c>docker build -t <paramref name="tag"/> <paramref name="directory"/></c> through the Docker CLI (BuildKit), with
    /// <paramref name="timeout"/>.
    /// </summary>
    public static async Task BuildImageAsync(DockerClient client, string directory, string tag, TimeSpan timeout)
    {
        var info = new System.Diagnostics.ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        info.ArgumentList.Add("build");
        info.ArgumentList.Add("-t");
        info.ArgumentList.Add(tag);
        info.ArgumentList.Add(".");
        using var process = System.Diagnostics.Process.Start(info)
                         ?? throw new InvalidOperationException("Could not start docker build");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException($"docker build {tag} took more than {timeout}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker build {tag} failed:\n{await stdout}\n{await stderr}");

        if (!await ImageExistsAsync(client, tag))
            throw new InvalidOperationException($"docker build {tag} did not produce the image");
    }
}