namespace NLightning.Testing.Cluster.Runner;

using Images;

/// <summary>
/// The test-runner image (plan §4 R5, PR #10's phase 3): the .NET SDK image with the built test assemblies under
/// <see cref="AssemblyDirectory"/>. Built on the host by <c>test/NLightning.Testing.Cluster/Runner/image/build.sh</c>;
/// OrbStack's cluster shares the Docker image store, so it is used with <c>imagePullPolicy: Never</c> and no registry.
/// Another cluster needs it pushed and <see cref="ImageVariable"/> pointing at the pushed reference.
/// </summary>
public static class RunnerImage
{
    /// <summary>The repository of the spike's runner image (under <see cref="ImageVersions.SpikeImagePrefix"/>).</summary>
    public const string Repository = ImageVersions.SpikeImagePrefix + "runner";

    /// <summary>The tag <c>build.sh</c> writes by default.</summary>
    public const string DefaultTag = "latest";

    /// <summary>Overrides the image (<c>repository:tag</c>, pulled IfNotPresent).</summary>
    public const string ImageVariable = "NLTG_RUNNER_IMAGE";

    /// <summary>Where the image keeps the test assemblies.</summary>
    public const string AssemblyDirectory = "/runner";

    /// <summary>The spike's locally built image.</summary>
    public static ImageRef Local { get; } = new(Repository, DefaultTag, PullPolicy: ImagePullPolicy.Never);

    /// <summary>
    /// <see cref="ImageVariable"/> as an image (pulled IfNotPresent) when set, otherwise <see cref="Local"/>.
    /// </summary>
    public static ImageRef FromEnvironment(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var configured = environment(ImageVariable)?.Trim();
        if (string.IsNullOrEmpty(configured))
            return Local;

        var at = configured.IndexOf('@', StringComparison.Ordinal);
        var digest = at >= 0 ? configured[(at + 1)..] : null;
        var name = at >= 0 ? configured[..at] : configured;
        var colon = name.LastIndexOf(':');
        return colon > name.LastIndexOf('/')
                   ? new ImageRef(name[..colon], name[(colon + 1)..], digest)
                   : new ImageRef(name, DefaultTag, digest);
    }
}