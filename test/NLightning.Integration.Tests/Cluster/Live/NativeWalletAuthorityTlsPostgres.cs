using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using k8s;
using k8s.Models;
using Npgsql;

namespace NLightning.Integration.Tests.Cluster.Live;

using Testing.Cluster.Nodes.Postgres;
using Testing.Cluster.Run;

/// <summary>Proof-local TLS provisioning; shared PostgreSQL fixtures keep their existing deployment contract.</summary>
internal sealed class NativeWalletAuthorityTlsPostgres
{
    public string OwnerConnectionString { get; }
    public string RuntimeConnectionString { get; }

    private NativeWalletAuthorityTlsPostgres(string owner, string runtime)
    {
        OwnerConnectionString = owner;
        RuntimeConnectionString = runtime;
    }

    public static async Task<string> CreateTlsSecretAsync(TestRun run, CancellationToken cancellationToken)
    {
        const string name = "native-authority-pg";
        const string secretName = "native-authority-tls";
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=NLightning test authority CA", caKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=" + name, serverKey, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        san.AddDnsName($"{name}.{run.Namespace}.svc.cluster.local");
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature
            | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        await run.Client.CoreV1.CreateNamespacedSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, Labels = run.Identity.Labels.ToDictionary(pair => pair.Key, pair => pair.Value) },
            Type = "Opaque",
            StringData = new Dictionary<string, string>
            { ["server.crt"] = certificate.ExportCertificatePem(), ["server.key"] = serverKey.ExportPkcs8PrivateKeyPem() }
        }, run.Namespace, cancellationToken: cancellationToken);
        return ca.ExportCertificatePem();
    }

    public static async Task<NativeWalletAuthorityTlsPostgres> DeployAsync(TestRun run, string directory,
        CancellationToken cancellationToken)
    {
        const string name = "native-authority-pg";
        const string secretName = "native-authority-tls";
        var caPem = Environment.GetEnvironmentVariable("NLTG_NATIVE_AUTHORITY_CA_PEM")
            ?? throw new InvalidOperationException("Production authority proof requires wrapper-provisioned TLS identity.");
        var caPath = Path.Combine(directory, "postgres-ca.pem");
        await File.WriteAllTextAsync(caPath, caPem, cancellationToken);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The verified TLS authority proof requires Unix private files.");
        File.SetUnixFileMode(caPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var ownerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var options = new PostgresNodeOptions
        { Name = name, User = "authority_owner", Password = ownerPassword, Database = "native_authority" };
        var workload = PostgresNode.Workload(options);
        workload.ScratchVolumes["tls"] = "/tls";
        workload.Args.Add("postgres");
        foreach (var arg in new[] { "-c", "ssl=on", "-c", "ssl_cert_file=/tls/server.crt", "-c", "ssl_key_file=/tls/server.key" })
            workload.Args.Add(arg);
        workload.InitContainers.Add(new V1Container
        {
            Name = "install-test-tls",
            Image = options.Image.Reference,
            ImagePullPolicy = options.Image.PullPolicyValue,
            Command = ["sh", "-c", "cp /tls-secret/server.crt /tls/server.crt && cp /tls-secret/server.key /tls/server.key && chown 70:70 /tls/server.crt /tls/server.key && chmod 600 /tls/server.key"],
            VolumeMounts = [new V1VolumeMount { Name = "tls", MountPath = "/tls" },
                new V1VolumeMount { Name = "tls-secret", MountPath = "/tls-secret", ReadOnlyProperty = true }],
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("16Mi") },
                Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("64Mi") }
            }
        });
        workload.CustomizePod = pod => pod.Volumes.Add(new V1Volume
        {
            Name = "tls-secret",
            Secret = new V1SecretVolumeSource { SecretName = secretName, DefaultMode = 256 }
        });
        var handle = await run.DeployAsync(workload, TimeSpan.FromMinutes(3), cancellationToken);
        string Connection(string user, string password) => new NpgsqlConnectionStringBuilder
        {
            Host = handle.ServiceDnsName,
            Database = options.Database,
            Username = user,
            Password = password,
            SslMode = SslMode.VerifyFull,
            RootCertificate = caPath,
            Timeout = 5,
            CommandTimeout = 10,
            Pooling = false,
            IncludeErrorDetail = false
        }.ConnectionString;
        var owner = Connection(options.User, ownerPassword);
        await using (var connection = new NpgsqlConnection(owner))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // Password is random hexadecimal generated only by this trusted test provisioning plane.
            command.CommandText = $"CREATE ROLE signer_runtime LOGIN PASSWORD '{runtimePassword}'";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return new NativeWalletAuthorityTlsPostgres(owner, Connection("signer_runtime", runtimePassword));
    }

    public async Task GrantRuntimeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "GRANT SELECT, UPDATE ON NativeSignerAuthorities TO signer_runtime; "
            + "GRANT SELECT, INSERT ON NativeSignerReceipts TO signer_runtime; "
            + "GRANT SELECT ON NativeSignerApprovals, NativeWalletApprovals, NativeWalletInputClaims TO signer_runtime";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var runtime = new NpgsqlConnection(RuntimeConnectionString);
        await runtime.OpenAsync(cancellationToken);
        await using var check = runtime.CreateCommand();
        check.CommandText = "SELECT has_table_privilege(current_user, 'nativewalletapprovals', 'INSERT')";
        Assert.False((bool)(await check.ExecuteScalarAsync(cancellationToken))!);
    }
}