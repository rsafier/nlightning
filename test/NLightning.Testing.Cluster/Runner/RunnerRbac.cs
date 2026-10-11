using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Runner;

using Run;

/// <summary>
/// The in-cluster test runner's identity, scoped to one run's namespace (ported from LNUnit PR #10's
/// <c>k8s/rbac.yaml</c> and narrowed): a ServiceAccount, a Role and a RoleBinding, all in the run's namespace, so a
/// runner can deploy, exec into, restart and remove nodes there and nowhere else. It cannot create or list namespaces:
/// the host creates the run's namespace and the runner adopts it (<see cref="TestRunOptions.AdoptNamespace"/>).
/// Nothing cluster-scoped is created; the objects go with the namespace.
/// </summary>
public static class RunnerRbac
{
    /// <summary>The ServiceAccount, Role and RoleBinding name.</summary>
    public const string Name = "nltg-test-runner";

    /// <summary>
    /// The Role's rules. <c>namespaces</c> <c>get</c> covers only the run's own namespace object (a Role grants a
    /// namespace's own object), which <see cref="AdoptedNamespace"/> reads to check its labels.
    /// </summary>
    public static IReadOnlyList<V1PolicyRule> Rules { get; } =
    [
        new() { ApiGroups = [""], Resources = ["namespaces"], Verbs = ["get"] },
        new()
        {
            ApiGroups = ["apps"], Resources = ["statefulsets"],
            Verbs = ["create", "delete", "get", "list", "watch", "patch"]
        },
        // Stopped maintenance windows scale only workloads in this run's namespace.
        new() { ApiGroups = ["apps"], Resources = ["statefulsets/scale"], Verbs = ["get", "patch"] },
        new()
        {
            ApiGroups = [""],
            Resources =
            [
                "pods", "pods/log", "pods/status", "services", "persistentvolumeclaims", "configmaps", "events"
            ],
            Verbs = ["create", "delete", "get", "list", "watch", "patch"]
        },
        new() { ApiGroups = [""], Resources = ["resourcequotas"], Verbs = ["get", "list"] },
        new() { ApiGroups = [""], Resources = ["pods/exec"], Verbs = ["create", "get"] },
        new()
        {
            ApiGroups = ["networking.k8s.io"], Resources = ["networkpolicies"],
            Verbs = ["create", "delete", "get", "list"]
        }
    ];

    /// <summary>The three objects in <paramref name="run"/>'s namespace.</summary>
    public static RunnerRbacManifests Build(RunIdentity run)
    {
        ArgumentNullException.ThrowIfNull(run);

        V1ObjectMeta Meta() => new()
        {
            Name = Name,
            NamespaceProperty = run.Namespace,
            Labels = new Dictionary<string, string>(run.Labels)
        };

        return new RunnerRbacManifests(
            new V1ServiceAccount
            {
                ApiVersion = "v1",
                Kind = "ServiceAccount",
                Metadata = Meta(),
                AutomountServiceAccountToken = true
            },
            new V1Role
            {
                ApiVersion = "rbac.authorization.k8s.io/v1",
                Kind = "Role",
                Metadata = Meta(),
                Rules = [.. Rules]
            },
            new V1RoleBinding
            {
                ApiVersion = "rbac.authorization.k8s.io/v1",
                Kind = "RoleBinding",
                Metadata = Meta(),
                RoleRef = new V1RoleRef { ApiGroup = "rbac.authorization.k8s.io", Kind = "Role", Name = Name },
                Subjects =
                [
                    new Rbacv1Subject { Kind = "ServiceAccount", Name = Name, NamespaceProperty = run.Namespace }
                ]
            });
    }

    /// <summary>The user name the runner's requests carry (for access reviews).</summary>
    public static string UserName(string ns) => $"system:serviceaccount:{ns}:{Name}";

    /// <summary>Creates the objects; ones that already exist are kept.</summary>
    public static async Task ApplyAsync(IKubernetes client, RunnerRbacManifests manifests,
                                        CancellationToken cancellationToken)
    {
        var ns = manifests.ServiceAccount.Metadata.NamespaceProperty;
        await IgnoreConflict(client.CoreV1.CreateNamespacedServiceAccountAsync(
                                 manifests.ServiceAccount, ns, cancellationToken: cancellationToken))
           .ConfigureAwait(false);
        await IgnoreConflict(client.RbacAuthorizationV1.CreateNamespacedRoleAsync(
                                 manifests.Role, ns, cancellationToken: cancellationToken))
           .ConfigureAwait(false);
        await IgnoreConflict(client.RbacAuthorizationV1.CreateNamespacedRoleBindingAsync(
                                 manifests.RoleBinding, ns, cancellationToken: cancellationToken))
           .ConfigureAwait(false);
    }

    private static async Task IgnoreConflict<T>(Task<T> create)
    {
        try
        {
            await create.ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // Already there (a second Job in the same run)
        }
    }
}

/// <summary>The runner's ServiceAccount, Role and RoleBinding.</summary>
public sealed record RunnerRbacManifests(V1ServiceAccount ServiceAccount, V1Role Role, V1RoleBinding RoleBinding);