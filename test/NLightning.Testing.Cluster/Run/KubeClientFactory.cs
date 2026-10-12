using k8s;

namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// Where the client's configuration came from.
/// </summary>
public enum KubeConfigSource
{
    /// <summary>The test process runs in a pod (service account token, <c>KUBERNETES_SERVICE_HOST</c>).</summary>
    InCluster,

    /// <summary>The kubeconfig file (<c>KUBECONFIG</c> or <c>~/.kube/config</c>) on a developer machine.</summary>
    KubeConfig
}

/// <summary>
/// Builds the Kubernetes client (ported from LNUnit PR #10's in-cluster plan, phase 1): in a pod the in-cluster
/// configuration, elsewhere the kubeconfig with an optional context. TLS is always verified (PR #10 skipped it).
/// </summary>
public static class KubeClientFactory
{
    /// <summary>The kubeconfig context to use instead of the current one (e.g. <c>orbstack</c>).</summary>
    public const string ContextVariable = "NLTG_KUBE_CONTEXT";

    /// <summary>Set by Kubernetes in every pod.</summary>
    public const string ServiceHostVariable = "KUBERNETES_SERVICE_HOST";

    /// <summary>Which configuration applies, from the environment.</summary>
    public static KubeConfigSource DetectSource(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return string.IsNullOrEmpty(environment(ServiceHostVariable))
                   ? KubeConfigSource.KubeConfig
                   : KubeConfigSource.InCluster;
    }

    /// <summary>
    /// The client configuration: in-cluster in a pod, otherwise the kubeconfig at <paramref name="context"/> (null:
    /// <see cref="ContextVariable"/>, then the kubeconfig's current context).
    /// </summary>
    public static KubernetesClientConfiguration BuildConfiguration(string? context = null)
    {
        if (DetectSource() == KubeConfigSource.InCluster)
            return KubernetesClientConfiguration.InClusterConfig();

        context ??= Environment.GetEnvironmentVariable(ContextVariable);
        return KubernetesClientConfiguration.BuildConfigFromConfigFile(
            currentContext: string.IsNullOrWhiteSpace(context) ? null : context);
    }

    /// <summary>A client for <see cref="BuildConfiguration"/>.</summary>
    public static IKubernetes Create(string? context = null) => new Kubernetes(BuildConfiguration(context));
}