using System.Collections.Concurrent;
using System.Net;
using System.Text;
using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

/// <summary>
/// A Kubernetes API without a cluster for the run lifecycle: namespaces are created, read and deleted in memory, and
/// every request is recorded (<c>METHOD path?query</c>). Collection deletes answer success. A deleted namespace stays
/// readable as terminating for <see cref="ReadsWhileTerminating"/> reads, then is gone.
/// </summary>
internal sealed class FakeKubeApi : DelegatingHandler
{
    private const string NamespacesPath = "/api/v1/namespaces";

    private readonly ConcurrentDictionary<string, V1Namespace> _namespaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _terminatingReads = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>How many reads a deleted namespace still answers (terminating) before it is gone.</summary>
    public int ReadsWhileTerminating { get; set; } = 2;

    /// <summary>A client whose every call this API answers.</summary>
    public Kubernetes CreateClient() =>
        new(new KubernetesClientConfiguration { Host = "http://fake-kube.invalid" }, this);

    /// <summary>Adds <paramref name="run"/>'s namespace as if another process (the host) had created it.</summary>
    public void AddNamespace(RunIdentity run) => _namespaces[run.Namespace] = RunNamespace.Build(run);

    public bool Exists(string name) => _namespaces.ContainsKey(name);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        var response = await AnswerAsync(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue($"{request.Method} {path}{request.RequestUri.Query}");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // POST /api/v1/namespaces
        if (request.Method == HttpMethod.Post && path == NamespacesPath)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var ns = KubernetesJson.Deserialize<V1Namespace>(body);
            ns.Metadata.CreationTimestamp = DateTime.UtcNow;
            return _namespaces.TryAdd(ns.Metadata.Name, ns) ? Json(HttpStatusCode.Created, ns) : Status(409);
        }

        // /api/v1/namespaces/{name}
        if (segments.Length == 4 && path.StartsWith(NamespacesPath + "/", StringComparison.Ordinal))
        {
            var name = segments[3];
            if (!_namespaces.TryGetValue(name, out var ns))
                return Status(404);

            if (request.Method == HttpMethod.Get)
            {
                if (ns.Metadata.DeletionTimestamp is not null
                 && _terminatingReads.AddOrUpdate(name, 1, (_, n) => n + 1) > ReadsWhileTerminating)
                {
                    _namespaces.TryRemove(name, out _);
                    return Status(404);
                }

                return Json(HttpStatusCode.OK, ns);
            }

            if (request.Method == HttpMethod.Delete)
            {
                ns.Metadata.DeletionTimestamp ??= DateTime.UtcNow;
                ns.Status = new V1NamespaceStatus { Phase = "Terminating" };
                return Json(HttpStatusCode.OK, ns);
            }
        }

        // /api/v1/namespaces/{ns}/pods: deleted as a collection, listed (none left)
        if (segments.Length == 5 && segments[4] == "pods")
            return Json(HttpStatusCode.OK, new V1PodList { Items = [] });

        // DELETE /apis/apps/v1/namespaces/{ns}/statefulsets (a collection)
        if (request.Method == HttpMethod.Delete && segments.Length == 6 && segments[5] == "statefulsets")
            return Json(HttpStatusCode.OK, new V1StatefulSetList { Items = [] });

        return Status(404);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) =>
        new(code) { Content = new StringContent(KubernetesJson.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(int code) =>
        new((HttpStatusCode)code)
        {
            Content = new StringContent(
                $"{{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"code\":{code},"
              + $"\"status\":\"{(code < 400 ? "Success" : "Failure")}\"}}", Encoding.UTF8, "application/json")
        };
}