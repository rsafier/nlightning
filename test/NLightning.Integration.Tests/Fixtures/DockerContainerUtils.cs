using Docker.DotNet;
using Docker.DotNet.Models;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// Container helpers shared by the Docker fixtures.
/// </summary>
internal static class DockerContainerUtils
{
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
}