using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NLightning.Domain.Node.Options;

using Gossip.Addresses;

/// <summary>
/// Tor support, configuration section <c>Node:Tor</c> (it is <see cref="NodeOptions.Tor"/>): outbound peer connections
/// through Tor's SOCKS5 port, and our own v3 onion service through Tor's control port. Off by default.
/// </summary>
/// <remarks>
/// <para><see cref="TorMode.Hybrid"/> dials <c>.onion</c> peers through Tor and everything else directly, so a clearnet
/// node can peer with Tor-only nodes. <see cref="TorMode.TorOnly"/> sends every outbound peer connection through Tor
/// (clearnet peers through an exit), never resolves a peer's host name locally, sends the fee-estimation and Esplora
/// HTTP requests through Tor too and skips the BOLT 10 DNS seeds; it creates our onion service unless
/// <see cref="OnionServiceEnabled"/> is false.</para>
/// <para>Works with C Tor 0.4.8 or newer (the control port is needed only for our own onion service). Arti speaks the
/// SOCKS5 side only: with it set <see cref="OnionServiceEnabled"/> false, host the onion service in Arti's own
/// configuration and put its address in <c>Gossip:AnnounceAddresses</c>. The same goes for a <c>HiddenServiceDir</c>
/// in <c>torrc</c>.</para>
/// </remarks>
public class TorOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Node:Tor";

    /// <summary>The default <see cref="SocksProxy"/>: Tor's default <c>SocksPort</c>.</summary>
    public const string DefaultSocksProxy = "127.0.0.1:9050";

    /// <summary>The default <see cref="Control"/>: Tor's usual <c>ControlPort</c>.</summary>
    public const string DefaultControl = "127.0.0.1:9051";

    /// <summary>The default <see cref="OnionServiceKeyFile"/>, relative to the configuration directory.</summary>
    public const string DefaultOnionServiceKeyFile = "tor_onion_v3.key";

    private const string UnixPrefix = "unix:";

    /// <summary>What goes through Tor. Default <see cref="TorMode.Off"/>.</summary>
    public TorMode Mode { get; set; } = TorMode.Off;

    /// <summary>
    /// Tor's SOCKS5 port: <c>host:port</c>, <c>[ipv6]:port</c> or <c>unix:/path/to/socket</c> (a <c>SocksPort unix:</c>
    /// socket). Default <see cref="DefaultSocksProxy"/>.
    /// </summary>
    public string SocksProxy { get; set; } = DefaultSocksProxy;

    /// <summary>
    /// Give every peer connection its own circuit: each SOCKS5 request carries random username/password credentials,
    /// which Tor isolates by (<c>IsolateSOCKSAuth</c>, on by default on every <c>SocksPort</c>). Default true.
    /// </summary>
    public bool StreamIsolation { get; set; } = true;

    /// <summary>
    /// How long a connection through Tor may take, SOCKS5 handshake and circuit (an onion service's rendezvous
    /// included) together. Replaces <c>Node:NetworkTimeout</c> for those connections. Default 60 s.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tor's control port for our onion service: <c>host:port</c>, <c>[ipv6]:port</c> or <c>unix:/path</c> (Debian's
    /// <c>/run/tor/control</c>). Default <see cref="DefaultControl"/>.
    /// </summary>
    public string Control { get; set; } = DefaultControl;

    /// <summary>
    /// The control port password (<c>HashedControlPassword</c> in <c>torrc</c>). Without it the node authenticates with
    /// the cookie Tor names in <c>PROTOCOLINFO</c> (SAFECOOKIE, else COOKIE), or with no credentials when Tor allows it.
    /// </summary>
    public string? ControlPassword { get; set; }

    /// <summary>
    /// The control auth cookie to read instead of the one Tor names (Tor in a container with the cookie mounted
    /// elsewhere).
    /// </summary>
    public string? ControlCookieFile { get; set; }

    /// <summary>
    /// Create our v3 onion service through the control port (<c>ADD_ONION</c>); unset means on in
    /// <see cref="TorMode.TorOnly"/> and off in <see cref="TorMode.Hybrid"/>. Read the effective value from
    /// <see cref="IsOnionServiceEnabled"/>.
    /// </summary>
    public bool? OnionServiceEnabled { get; set; }

    /// <summary>The onion service's virtual port, the port we announce. Default 9735.</summary>
    public ushort OnionServicePort { get; set; } = 9735;

    /// <summary>
    /// Where Tor sends the onion service's connections: <c>host:port</c> or <c>unix:/path</c>. Unset means our first
    /// <c>Node:ListenAddresses</c> entry, with an any-address (<c>0.0.0.0</c>, <c>[::]</c>) replaced by loopback.
    /// </summary>
    public string? OnionServiceTarget { get; set; }

    /// <summary>
    /// The onion service's private key (<c>ED25519-V3:&lt;base64&gt;</c>, as Tor returns it), created on first use with
    /// owner-only permissions; relative paths resolve against the configuration directory. The key is the onion
    /// address: keep it with the node key. Default <see cref="DefaultOnionServiceKeyFile"/>.
    /// </summary>
    public string OnionServiceKeyFile { get; set; } = DefaultOnionServiceKeyFile;

    /// <summary>
    /// Add the onion service to our <c>node_announcement</c> (it goes out once we have an announced channel). Default
    /// true.
    /// </summary>
    public bool AnnounceOnionService { get; set; } = true;

    /// <summary>True unless <see cref="Mode"/> is <see cref="TorMode.Off"/>.</summary>
    public bool IsEnabled => Mode != TorMode.Off;

    /// <summary>True in <see cref="TorMode.TorOnly"/>.</summary>
    public bool IsTorOnly => Mode == TorMode.TorOnly;

    /// <summary>The effective <see cref="OnionServiceEnabled"/>: never when Tor is off.</summary>
    public bool IsOnionServiceEnabled => IsEnabled && (OnionServiceEnabled ?? IsTorOnly);

    /// <summary>
    /// Whether an address of <paramref name="type"/> can be dialed at all: Tor v3 only with Tor, Tor v2 never (Tor
    /// removed it in 0.4.6), everything else always.
    /// </summary>
    public bool CanDial(AddressDescriptorType type) => type switch
    {
        AddressDescriptorType.TorV3 => IsEnabled,
        AddressDescriptorType.TorV2 => false,
        _ => true
    };

    /// <summary>
    /// Whether a connection to an address of <paramref name="type"/> goes through the SOCKS5 proxy: onions whenever Tor
    /// is on, everything in <see cref="TorMode.TorOnly"/>.
    /// </summary>
    public bool UsesProxy(AddressDescriptorType type) =>
        type == AddressDescriptorType.TorV3 ? IsEnabled : IsTorOnly;

    /// <summary>
    /// The onion service's target: <see cref="OnionServiceTarget"/>, or our first listen address with an any-address
    /// host replaced by loopback. Null when neither gives one.
    /// </summary>
    public string? GetOnionServiceTarget(IEnumerable<string>? listenAddresses)
    {
        if (!string.IsNullOrWhiteSpace(OnionServiceTarget))
            return OnionServiceTarget.Trim();

        var first = listenAddresses?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (first is null || !IPEndPoint.TryParse(first.Trim(), out var endPoint) || endPoint.Port == 0)
            return null;

        if (endPoint.Address.Equals(IPAddress.Any))
            endPoint.Address = IPAddress.Loopback;
        else if (endPoint.Address.Equals(IPAddress.IPv6Any))
            endPoint.Address = IPAddress.IPv6Loopback;

        return endPoint.ToString();
    }

    /// <summary>
    /// Every configuration error of these options (empty when valid); <paramref name="listenAddresses"/> give the
    /// default onion service target.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors(IEnumerable<string>? listenAddresses = null)
    {
        const string prefix = "Tor:";
        var errors = new List<string>();
        if (!Enum.IsDefined(Mode))
            errors.Add($"{prefix}{nameof(Mode)} must be Off, Hybrid or TorOnly.");
        if (!IsEnabled)
            return errors;

        if (!TryParseEndPoint(SocksProxy, out _))
            errors.Add($"{prefix}{nameof(SocksProxy)} '{SocksProxy}' is not host:port, [ipv6]:port or unix:/path.");
        if (ConnectTimeout <= TimeSpan.Zero)
            errors.Add($"{prefix}{nameof(ConnectTimeout)} must be positive.");
        if (!IsOnionServiceEnabled)
            return errors;

        if (!TryParseEndPoint(Control, out _))
            errors.Add($"{prefix}{nameof(Control)} '{Control}' is not host:port, [ipv6]:port or unix:/path "
                     + $"(the onion service needs Tor's control port; set {nameof(OnionServiceEnabled)} false to "
                     + "run without it).");
        if (OnionServicePort == 0)
            errors.Add($"{prefix}{nameof(OnionServicePort)} must be from 1 to 65535.");
        if (string.IsNullOrWhiteSpace(OnionServiceKeyFile))
            errors.Add($"{prefix}{nameof(OnionServiceKeyFile)} must be set.");

        var target = GetOnionServiceTarget(listenAddresses);
        if (target is null)
            errors.Add($"{prefix}{nameof(OnionServiceTarget)} is not set and Node:ListenAddresses has no ip:port entry "
                     + "to send the onion service's connections to.");
        else if (!TryParseEndPoint(target, out _))
            errors.Add($"{prefix}{nameof(OnionServiceTarget)} '{target}' is not host:port, [ipv6]:port or "
                     + "unix:/path.");

        return errors;
    }

    /// <summary>
    /// Reads <c>host:port</c> (an IP address or a host name), <c>[ipv6]:port</c> or <c>unix:/path</c>. A host name
    /// becomes a <see cref="DnsEndPoint"/>.
    /// </summary>
    public static bool TryParseEndPoint(string? value, out EndPoint endPoint)
    {
        endPoint = new IPEndPoint(IPAddress.None, 0);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.StartsWith(UnixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = text[UnixPrefix.Length..];
            if (path.Length == 0)
                return false;

            endPoint = new UnixDomainSocketEndPoint(path);
            return true;
        }

        if (IPEndPoint.TryParse(text, out var ipEndPoint))
        {
            if (ipEndPoint.Port == 0)
                return false;

            endPoint = ipEndPoint;
            return true;
        }

        var separator = text.LastIndexOf(':');
        if (separator <= 0 || text.IndexOf(':') != separator
         || !ushort.TryParse(text[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
         || port == 0)
            return false;

        var host = text[..separator];
        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            return false;

        endPoint = new DnsEndPoint(host, port);
        return true;
    }
}

/// <summary>What goes through Tor (<see cref="TorOptions.Mode"/>).</summary>
public enum TorMode
{
    /// <summary>No Tor: <c>.onion</c> peers cannot be dialed.</summary>
    Off,

    /// <summary><c>.onion</c> peers through Tor, everything else directly.</summary>
    Hybrid,

    /// <summary>Every outbound connection through Tor; our onion service on by default.</summary>
    TorOnly
}