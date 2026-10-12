using System.Globalization;

namespace NLightning.Testing.Cluster.Nodes.Bark;

using Images;
using Kube;
using Run;

/// <summary>
/// How captaind — Second's Bark ASP server, the <c>nltg-captaind</c> image built from the bark repo — is deployed as a
/// node of a run (the Bark/ASP proof, NL-1148 wave B): its <c>captaind.toml</c> starts from the vendored default
/// template (<c>captaind.template.toml</c>, the one <c>server/src/config.rs</c>'s test parses) with the run's addresses
/// spliced in, carried into the pod by <see cref="ConfigVariable"/> and written before <c>captaind</c> runs.
/// </summary>
/// <remarks>
/// <para>
/// The Lightning backend is captaind's <c>[[cln_array]]</c>: <c>uri</c> (the <c>cln.Node</c> gRPC) and
/// <c>hold_invoice.uri</c> (the Boltz <c>hold.Hold</c> gRPC) both point at the NLightning node's LN backend. Both are
/// <c>https://</c> with mutual TLS: captaind's client builds its <c>ClientTlsConfig</c> — the CA, its certificate and
/// its key from the config's certificate paths — whatever the URI's scheme, and an <c>http://</c> URI then dies at
/// channel build ("Error parsing TLS private key"), so the node's backend serves <c>LnBackend:TlsDirectory</c> and the
/// run carries the test CA's material into the pod (<see cref="CaCertificateVariable"/> and friends).
/// </para>
/// <para>
/// captaind also needs its own bitcoind (v31 or newer, <c>txindex</c>: it checks both at startup — the LND fixture's
/// Core 29 is too old) and its own PostgreSQL (it creates its database there itself). Ready once its public gRPC port
/// accepts connections, which only happens after <c>captaind create</c> and <c>start</c> succeeded against both.
/// </para>
/// </remarks>
public sealed record CaptaindNodeOptions
{
    /// <summary>The node's alias (StatefulSet, Service and container name).</summary>
    public string Name { get; init; } = "captaind";

    /// <summary>
    /// The image: <c>nltg-captaind:latest</c>, built on the host from the bark repo (the tag is Never pulled; OrbStack's
    /// cluster shares the Docker image store) — see <see cref="ImageVersions.Captaind"/>.
    /// </summary>
    public ImageRef Image { get; init; } = ImageVersions.Captaind;

    /// <summary>The <c>cln.Node</c> gRPC URI of the Lightning backend (an NLightning LN backend, mTLS).</summary>
    public string LightningUri { get; init; } = "https://127.0.0.1:50052";

    /// <summary>The <c>hold.Hold</c> gRPC URI of the Lightning backend (the same NLightning LN backend).</summary>
    public string HoldInvoiceUri { get; init; } = "https://127.0.0.1:50052";

    /// <summary>The CA certificate (PEM) captaind validates the Lightning backend's server with.</summary>
    public string CaCertificatePem { get; init; } = string.Empty;

    /// <summary>The client certificate (PEM) captaind presents to the Lightning backend.</summary>
    public string ClientCertificatePem { get; init; } = string.Empty;

    /// <summary>The client key (PKCS 8 PEM) of <see cref="ClientCertificatePem"/>.</summary>
    public string ClientKeyPem { get; init; } = string.Empty;

    /// <summary>The bitcoind RPC URL captaind uses (its own node, Core 31+, <c>txindex</c>).</summary>
    public string BitcoindUrl { get; init; } = "http://bitcoind:18443";

    public string BitcoindUser { get; init; } = "nltg";

    public string BitcoindPassword { get; init; } = "nltg";

    /// <summary>The PostgreSQL host (pod alias) and port captaind creates its database on.</summary>
    public string PostgresHost { get; init; } = "postgres";

    public int PostgresPort { get; init; } = 5432;

    public string PostgresUser { get; init; } = "superuser";

    public string PostgresPassword { get; init; } = "superuser";

    /// <summary>The database captaind creates for itself (must not exist yet).</summary>
    public string PostgresDatabase { get; init; } = "captaind";

    /// <summary>
    /// The VTXO pool's targets (<c>[vtxopool] vtxo_targets</c>): what lightning receive grants are paid from. The
    /// template's 1000/10000 sat targets are too small to fund one lightning receive, so a run sizes them for its
    /// receives.
    /// </summary>
    public string VtxoPoolTargets { get; init; } = "vtxo_targets = [ \"1000sat:10\", \"10000sat:10\" ]";

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;

    /// <summary>The data directory in an <c>emptyDir</c>: a captaind of the tests is never restarted.</summary>
    public NodeStorage Storage { get; init; } = NodeStorage.Ephemeral;

    public string DataSize { get; init; } = "256Mi";
}

/// <summary>
/// A captaind pod of a run: its public Ark gRPC (what a Bark client — or the test standing in for one — calls) and its
/// admin gRPC (the operator's <c>WalletAdminService</c>, unauthenticated inside the pod network) on the pod's DNS name.
/// </summary>
public sealed class CaptaindNode
{
    private CaptaindNode(KubeNodeHandle handle, CaptaindNodeOptions options)
    {
        Handle = handle;
        Options = options;
    }

    /// <summary>The public Ark gRPC's port in the pod (captaind's <c>rpc.public_address</c> binds it).</summary>
    public const int PublicPort = 3535;

    /// <summary>The admin gRPC's port in the pod (<c>rpc.admin_address</c> binds it).</summary>
    public const int AdminPort = 3536;

    /// <summary>captaind's data directory (its mnemonic and wallet state).</summary>
    public const string DataPath = "/data";

    /// <summary>The environment variable that carries <c>captaind.toml</c> into the pod.</summary>
    public const string ConfigVariable = "NLTG_CAPTAIND_CONFIG";

    /// <summary>The environment variable that carries the Lightning backend's CA certificate into the pod.</summary>
    public const string CaCertificateVariable = "NLTG_CAPTAIND_CA_PEM";

    /// <summary>The environment variable that carries captaind's Lightning client certificate into the pod.</summary>
    public const string ClientCertificateVariable = "NLTG_CAPTAIND_CLIENT_PEM";

    /// <summary>The environment variable that carries that certificate's PKCS 8 key into the pod.</summary>
    public const string ClientKeyVariable = "NLTG_CAPTAIND_CLIENT_KEY_PEM";

    /// <summary>Where the workload writes the certificate files the config points captaind at.</summary>
    public const string CertificatesPath = "/certs";

    /// <summary>The deployed node.</summary>
    public KubeNodeHandle Handle { get; }

    public CaptaindNodeOptions Options { get; }

    /// <summary>The <c>host:port</c> the test process calls the public Ark gRPC at (the pod's DNS name).</summary>
    public string PublicEndpoint => $"{Handle.PodDnsName}:{PublicPort.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The <c>host:port</c> of the admin gRPC (wallet status, round triggers).</summary>
    public string AdminEndpoint => $"{Handle.PodDnsName}:{AdminPort.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The workload: write the configuration and the Lightning backend's certificate files, then run the image's start
    /// script (which runs <c>captaind create</c> on an empty data directory and <c>exec</c>s <c>captaind start</c>, so
    /// SIGTERM reaches the server). Ready once the public gRPC port accepts connections.
    /// </summary>
    public static NodeWorkload Workload(CaptaindNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LightningUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HoldInvoiceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BitcoindUrl);
        if (options.LightningUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || options.HoldInvoiceUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "captaind's Lightning URIs must be https://: its client builds the TLS configuration (and parses the "
                + "certificate paths) whatever the scheme, so an http:// URI dies at channel build", nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CaCertificatePem);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientCertificatePem);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientKeyPem);

        var workload = new NodeWorkload(options.Name, NodeKind.Other, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(DataPath, options.DataSize, Storage: options.Storage),
            Command =
            [
                "/bin/sh", "-c",
                $"mkdir -p {DataPath}/captaind {CertificatesPath} "
              + $"&& printf '%s' \"${ConfigVariable}\" > {DataPath}/captaind.toml "
              + $"&& printf '%s' \"${CaCertificateVariable}\" > {CertificatesPath}/ca.pem "
              + $"&& printf '%s' \"${ClientCertificateVariable}\" > {CertificatesPath}/client.pem "
              + $"&& printf '%s' \"${ClientKeyVariable}\" > {CertificatesPath}/client.key "
              + $"&& export CAPTAIND_CONFIG_PATH={DataPath}/captaind.toml && exec /usr/local/bin/start.sh"
            ],
            ReadinessProbe = Probes.Tcp(PublicPort, periodSeconds: 1),
            // create (bitcoind and postgres round trips) plus start, against the readiness timeout's budget
            TerminationGracePeriodSeconds = 15
        };
        workload.Env[ConfigVariable] = BuildConfig(options);
        workload.Env[CaCertificateVariable] = options.CaCertificatePem;
        workload.Env[ClientCertificateVariable] = options.ClientCertificatePem;
        workload.Env[ClientKeyVariable] = options.ClientKeyPem;
        workload.Ports.Add(new WorkloadPort("ark", PublicPort));
        workload.Ports.Add(new WorkloadPort("admin", AdminPort));
        return workload;
    }

    /// <summary>
    /// <c>captaind.toml</c>: the vendored default template with this run's addresses (data directory, bitcoind,
    /// PostgreSQL, the public and admin sockets), the test-friendly timings (a fast round cadence, a one-second settler
    /// poll and invoice check, a payment reconciliation backing off from 1 s to 5 s) and the <c>[[cln_array]]</c> pointing at the NLightning LN backend with the certificate
    /// files the workload wrote.
    /// </summary>
    public static string BuildConfig(CaptaindNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var config = Template.Replace("data_dir = \"./bark-server\"", $"data_dir = \"{DataPath}/captaind\"")
                             .Replace("url = \"http://127.0.0.1:18443\"", $"url = \"{options.BitcoindUrl}\"")
                             .Replace("#rpc_user = \"admin\"", $"rpc_user = \"{options.BitcoindUser}\"")
                             .Replace("#rpc_pass = \"super_secure\"", $"rpc_pass = \"{options.BitcoindPassword}\"")
                             .Replace("public_address = \"127.0.0.1:3535\"",
                                      $"public_address = \"0.0.0.0:{Format(PublicPort)}\"")
                             .Replace("admin_address = \"127.0.0.1:3536\"",
                                      $"admin_address = \"0.0.0.0:{Format(AdminPort)}\"")
                             .Replace("host = \"localhost\"", $"host = \"{options.PostgresHost}\"")
                             .Replace("port = 5432", $"port = {Format(options.PostgresPort)}")
                             .Replace("name = \"bark-server-db\"", $"name = \"{options.PostgresDatabase}\"")
                             .Replace("#user = \"postgres\"", $"user = \"{options.PostgresUser}\"")
                             .Replace("#password = \"super_secure\"",
                                      $"password = \"{options.PostgresPassword}\"")
                             // Test cadence: rounds every 5 s, the settler WAL and the invoice check every second
                             .Replace("round_interval = \"10s\"", "round_interval = \"5s\"")
                             .Replace("htlc_settlement_poll_interval = \"60s\"",
                                      "htlc_settlement_poll_interval = \"1s\"")
                             .Replace("invoice_check_interval = \"3s\"", "invoice_check_interval = \"1s\"")
                             .Replace("cln_reconnect_interval = \"10s\"", "cln_reconnect_interval = \"2s\"")
                             // The xpay reconciliation of a payment still in flight after its retry window (first
                             // check after retry_for + 15 s, then backing off) and a waiting CheckLightningPayment's
                             // poll: seconds, not the template's 10 s doubling to 10 min (NL-1148 wave C)
                             .Replace("invoice_check_base_delay = \"10s\"", "invoice_check_base_delay = \"1s\"")
                             .Replace("max_invoice_check_delay = \"10m\"", "max_invoice_check_delay = \"5s\"")
                             .Replace("invoice_poll_interval = \"30s\"", "invoice_poll_interval = \"2s\"")
                             .Replace("vtxo_targets = [ \"1000sat:10\", \"10000sat:10\" ]", options.VtxoPoolTargets);
        foreach (var replaced in new[]
                 {
                     $"data_dir = \"{DataPath}/captaind\"", $"public_address = \"0.0.0.0:{Format(PublicPort)}\"",
                     $"name = \"{options.PostgresDatabase}\"", options.VtxoPoolTargets,
                     "invoice_check_base_delay = \"1s\"", "invoice_poll_interval = \"2s\""
                 })
            if (!config.Contains(replaced))
                throw new InvalidOperationException(
                    $"the captaind template no longer contains '{replaced}': re-vendor captaind.default.toml");

        var lnBackend = $$"""

                          [[cln_array]]
                          uri = "{{options.LightningUri}}"
                          priority = 10
                          # captaind's naming: server_cert_path is the CA it validates our server with, the client
                          # pair is what our LnBackend:TlsDirectory ca.pem trust requires
                          server_cert_path = "{{CertificatesPath}}/ca.pem"
                          client_cert_path = "{{CertificatesPath}}/client.pem"
                          client_key_path = "{{CertificatesPath}}/client.key"

                          [cln_array.hold_invoice]
                          uri = "{{options.HoldInvoiceUri}}"
                          server_cert_path = "{{CertificatesPath}}/ca.pem"
                          client_cert_path = "{{CertificatesPath}}/client.pem"
                          client_key_path = "{{CertificatesPath}}/client.key"
                          """;
        return config.TrimEnd() + lnBackend + "\n";
    }

    /// <summary>Deploys captaind into <paramref name="run"/>'s namespace (bitcoind and PostgreSQL first) and waits for
    /// its public gRPC.</summary>
    public static async Task<CaptaindNode> DeployAsync(TestRun run, CaptaindNodeOptions options, TimeSpan readyTimeout,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var handle = await run.DeployAsync(Workload(options), readyTimeout, cancellationToken).ConfigureAwait(false);
        return new CaptaindNode(handle, options);
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// bark's own <c>server/captaind.default.toml</c> (MIT), the file <c>server/src/config.rs</c>'s test validates
    /// against — every field captaind requires with the upstream defaults, so only the run's addresses and timings are
    /// spliced in. The image is built from the same commit.
    /// </summary>
    public static string Template { get; } = ReadTemplate();

    private static string ReadTemplate()
    {
        var assembly = typeof(CaptaindNode).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
                                   .Single(n => n.EndsWith("captaind.template.toml", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
                     ?? throw new InvalidOperationException("captaind.template.toml is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}