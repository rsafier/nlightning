using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NLightning.Testing.Lnd.Tests;

/// <summary>The committed protos are LND's files at the manifest's tag, changed only by the csharp_namespace line.</summary>
public partial class ProtoManifestTests
{
    private static readonly string s_protoRoot = Path.Combine(AppContext.BaseDirectory, "Protos");

    public static TheoryData<string> ManifestFiles => [.. s_manifest.Value.Files.Keys];

    private static readonly Lazy<ProtoManifest> s_manifest = new(() => ProtoManifest.Read(s_protoRoot));

    [Fact]
    public void Given_TheManifest_When_Read_Then_ItNamesTheLndTagCommitAndNamespacePrefix()
    {
        // Act
        var manifest = s_manifest.Value;

        // Assert
        Assert.Equal("github.com/lightningnetwork/lnd", manifest.Repository);
        Assert.Matches(TagRegex(), manifest.Tag);
        Assert.Matches(CommitRegex(), manifest.Commit);
        Assert.Equal("NLightning.Testing.Lnd", manifest.NamespacePrefix);
    }

    [Fact]
    public void Given_TheProtoFolder_When_ComparedWithTheManifest_Then_TheyListTheSameFiles()
    {
        // Arrange
        var onDisk = Directory.EnumerateFiles(s_protoRoot, "*.proto", SearchOption.AllDirectories)
                              .Select(x => Path.GetRelativePath(s_protoRoot, x).Replace('\\', '/'))
                              .Order(StringComparer.Ordinal);

        // Act
        var listed = s_manifest.Value.Files.Keys.Order(StringComparer.Ordinal);

        // Assert
        Assert.Equal(listed, onDisk);
    }

    [Fact]
    public void Given_TheManifest_When_Read_Then_EveryServiceLnUnitFetchedFromLndIsThere()
    {
        // Arrange
        string[] lnUnitSet =
        [
            "lightning.proto", "walletunlocker.proto", "stateservice.proto", "chainrpc/chainnotifier.proto",
            "invoicesrpc/invoices.proto", "routerrpc/router.proto", "watchtowerrpc/watchtower.proto",
            "wtclientrpc/wtclient.proto", "signrpc/signer.proto", "walletrpc/walletkit.proto",
            "autopilotrpc/autopilot.proto", "verrpc/verrpc.proto", "neutrinorpc/neutrino.proto",
            "peersrpc/peers.proto", "devrpc/dev.proto"
        ];

        // Act
        var missing = lnUnitSet.Where(x => !s_manifest.Value.Files.ContainsKey(x));

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void Given_TheManifest_When_Read_Then_LndsMitNoticeIsCommittedNextToTheProtos()
    {
        // Arrange
        var license = s_manifest.Value.License;
        var bytes = File.ReadAllBytes(Path.Combine(s_protoRoot, license.File));

        // Act
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var text = Encoding.UTF8.GetString(bytes);

        // Assert
        Assert.Equal("LICENSE-LND.txt", license.File);
        Assert.Equal("LICENSE", license.UpstreamPath);
        Assert.Equal(license.Sha256, hash);
        Assert.Contains("Lightning Labs and The Lightning Network Developers", text, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ManifestFiles))]
    public void Given_ACommittedProto_When_Hashed_Then_ItMatchesTheManifest(string file)
    {
        // Arrange
        var bytes = File.ReadAllBytes(Path.Combine(s_protoRoot, file));

        // Act
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // Assert
        Assert.Equal(s_manifest.Value.Files[file].Committed, hash);
    }

    [Theory]
    [MemberData(nameof(ManifestFiles))]
    public void Given_ACommittedProto_When_ItsNamespaceLineIsRemoved_Then_ItIsTheUpstreamFile(string file)
    {
        // Arrange
        var text = File.ReadAllText(Path.Combine(s_protoRoot, file), Encoding.UTF8);
        var package = PackageRegex().Match(text);
        Assert.True(package.Success, $"{file} has no package line");
        var expectedNamespace = $"{s_manifest.Value.NamespacePrefix}.{char.ToUpperInvariant(package.Groups[1].Value[0])}"
                              + package.Groups[1].Value[1..];
        var line = $"option csharp_namespace = \"{expectedNamespace}\";\n";

        // Act
        var upstream = text.Replace(package.Value + "\n" + line, package.Value + "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(upstream)));

        // Assert
        Assert.Single(CsharpNamespaceRegex().Matches(text));
        Assert.Contains(package.Value + "\n" + line, text, StringComparison.Ordinal);
        Assert.Equal(s_manifest.Value.Files[file].Upstream, hash);
    }

    [GeneratedRegex(@"^v\d+\.\d+\.\d+-beta(\.rc\d+)?$")]
    private static partial Regex TagRegex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitRegex();

    [GeneratedRegex(@"^package ([A-Za-z0-9_]+);$", RegexOptions.Multiline)]
    private static partial Regex PackageRegex();

    [GeneratedRegex("csharp_namespace")]
    private static partial Regex CsharpNamespaceRegex();

    private sealed record ProtoManifest(
        string Repository,
        string Tag,
        string Commit,
        string NamespacePrefix,
        (string File, string UpstreamPath, string Sha256) License,
        IReadOnlyDictionary<string, (string Upstream, string Committed)> Files)
    {
        public static ProtoManifest Read(string protoRoot)
        {
            string? repository = null, tag = null, commit = null, prefix = null;
            (string, string, string)? license = null;
            var files = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(Path.Combine(protoRoot, "manifest.txt")))
            {
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                var parts = line.Split(' ');
                switch (parts[0])
                {
                    case "lnd_repo": repository = parts[1]; break;
                    case "lnd_tag": tag = parts[1]; break;
                    case "lnd_commit": commit = parts[1]; break;
                    case "csharp_namespace_prefix": prefix = parts[1]; break;
                    case "license": license = (parts[1], parts[2], parts[3]); break;
                    case "file": files.Add(parts[1], (parts[2], parts[3])); break;
                    default: throw new InvalidDataException($"Unknown manifest line: {line}");
                }
            }

            return new ProtoManifest(repository ?? throw new InvalidDataException("lnd_repo missing"),
                                     tag ?? throw new InvalidDataException("lnd_tag missing"),
                                     commit ?? throw new InvalidDataException("lnd_commit missing"),
                                     prefix ?? throw new InvalidDataException("csharp_namespace_prefix missing"),
                                     license ?? throw new InvalidDataException("license missing"),
                                     files);
        }
    }
}