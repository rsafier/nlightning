namespace NLightning.Testing.Cluster.Images;

using Nodes;

/// <summary>
/// The one version table of the harness (plan R12), taken from the fixtures' former Docker backends (retired by NL-820 and
/// NL-866), so the suites kept their software. Locally built images use <see cref="ImagePullPolicy.Never"/>: OrbStack's cluster runs on the same Docker
/// image store, so no registry is needed; another cluster needs them pushed first (plan §4 "R12 images").
/// </summary>
/// <remarks>
/// New images built for the spike are tagged under <see cref="SpikeImagePrefix"/> only; the existing local tags
/// (<c>custom_lnd</c>, <c>nltg-eclair</c>, <c>nltg-ldk-server</c>) are reused as they are and never rebuilt or
/// retagged by the harness.
/// </remarks>
public static class ImageVersions
{
    /// <summary>The prefix of every image the spike builds itself.</summary>
    public const string SpikeImagePrefix = "nltg-spike-";

    /// <summary>
    /// Bitcoin Core 29.0 (Polar's image): the chain of the LND fixture (LNUnit's <c>AddBitcoinCoreNode</c>) and of
    /// <c>ClnFixture</c>.
    /// </summary>
    public static readonly ImageRef BitcoinCore =
        new("polarlightning/bitcoind", "29.0",
            "sha256:4521294ac25fdbf1492a8cc5ea987463a5f244b82546d29bbe8451db39f36f3a");

    /// <summary>
    /// Bitcoin Core 31.1 (the official image, multi-arch index digest): the chain of the Eclair and LDK fixtures
    /// (Eclair 0.14.3 refuses Core older than 31) and of the Tor fixture's Docker <c>TorChainHost</c>.
    /// </summary>
    public static readonly ImageRef BitcoinCore31 =
        new("bitcoin/bitcoin", "31.1",
            "sha256:da25cedc66b1daefff9f412ee196c901a899c3fa68a33b20849c3e08b5c40d63");

    /// <summary>
    /// LND 0.21.4-beta, built locally from <c>test/Docker/custom_lnd</c>
    /// (<c>docker build -t custom_lnd:0.21.4-beta test/Docker/custom_lnd</c>; nothing builds it automatically since
    /// NL-820). Reused as it is.
    /// </summary>
    public static readonly ImageRef Lnd = new("custom_lnd", "0.21.4-beta", PullPolicy: ImagePullPolicy.Never);

    /// <summary>
    /// Core Lightning as in <c>ClnFixture</c> (interop needs at least v26.06.7).
    /// </summary>
    public static readonly ImageRef Cln =
        new("elementsproject/lightningd", "v26.06.8",
            "sha256:56f1cebe829fbb3c7d5674be8cd1212c7e02527ab32403b695033a29bcd2abce");

    /// <summary>
    /// Eclair 0.14.3, built locally from <c>test/Docker/eclair</c>
    /// (<c>docker build -t nltg-eclair:0.14.3 test/Docker/eclair</c>; nothing builds it automatically since NL-866).
    /// Reused as it is.
    /// </summary>
    public static readonly ImageRef Eclair = new("nltg-eclair", "0.14.3", PullPolicy: ImagePullPolicy.Never);

    /// <summary>
    /// ldk-server at commit dc02b76c, built locally from <c>test/Docker/ldk_server</c>
    /// (<c>docker build -t nltg-ldk-server:dc02b76c test/Docker/ldk_server</c>, 10-20 min cold; nothing builds it
    /// automatically since NL-866). Reused as it is.
    /// </summary>
    public static readonly ImageRef Ldk = new("nltg-ldk-server", "dc02b76c", PullPolicy: ImagePullPolicy.Never);

    /// <summary>
    /// PostgreSQL as in <c>PostgresFixture</c> (the tag its retired Docker backend pulled, pinned to the official
    /// multi-arch index digest).
    /// </summary>
    public static readonly ImageRef Postgres =
        new("postgres", "16.2-alpine", "sha256:951bfda460300925caa3949eaa092ba022e9aec191bbea9056a39e2382260b27");

    /// <summary>
    /// CDK's mint daemon <c>cdk-mintd</c> 0.18.1 (the official image, multi-arch index digest), the CDK release of the
    /// vendored payment processor proto (Cashu plan C2, NL-993). Pull it once (<c>docker pull cashubtc/mintd:0.18.1</c>).
    /// </summary>
    public static readonly ImageRef CdkMintd =
        new("cashubtc/mintd", "0.18.1", "sha256:fbeac6e5bed139c525911c0a04c9556cbf3a645e20ecec142b52e27fde457f9f");

    /// <summary>
    /// CDK's wallet CLI <c>cdk-cli</c> 0.18.1, built locally from <c>test/Docker/cdk-cli</c>
    /// (<c>docker build -t nltg-cdk-cli:0.18.1 test/Docker/cdk-cli</c>, about 10 min; CDK publishes no image of it).
    /// Reused as it is.
    /// </summary>
    public static readonly ImageRef CdkCli = new("nltg-cdk-cli", "0.18.1", PullPolicy: ImagePullPolicy.Never);

    /// <summary>A tiny image for the harness's own smoke tests.</summary>
    public static readonly ImageRef Busybox = new("busybox", "1.37");

    /// <summary>The default image of each node kind that has one.</summary>
    public static IReadOnlyDictionary<NodeKind, ImageRef> ByKind { get; } = new Dictionary<NodeKind, ImageRef>
    {
        [NodeKind.BitcoinCore] = BitcoinCore,
        [NodeKind.Lnd] = Lnd,
        [NodeKind.Cln] = Cln,
        [NodeKind.Eclair] = Eclair,
        [NodeKind.Ldk] = Ldk,
        [NodeKind.Postgres] = Postgres
    };

    /// <summary>Every image of the table, for a preflight check.</summary>
    public static IReadOnlyList<ImageRef> All { get; } =
        [BitcoinCore, BitcoinCore31, Lnd, Cln, Eclair, Ldk, Postgres, Busybox];
}