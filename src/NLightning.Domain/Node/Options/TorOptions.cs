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
    /// the cookie Tor names in <c>PROTOCOLINFO</c> by SAFECOOKIE (never plain COOKIE, which proves nothing about the
    /// other end), or with no credentials only when <see cref="AllowUnauthenticatedControlPort"/> is set (NL-575).
    /// </summary>
    public string? ControlPassword { get; set; }

    /// <summary>
    /// Accept a control port that asks for no authentication (NULL). Such a port cannot prove it is Tor, and our onion
    /// service key goes to it in <c>ADD_ONION</c>, so this is off by default: enable <c>CookieAuthentication</c> in
    /// <c>torrc</c> or use a <c>ControlSocket</c> instead (NL-575). Default false.
    /// </summary>
    public bool AllowUnauthenticatedControlPort { get; set; }

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
    /// Where Tor sends the onion service's connections: <c>host:port</c>. Unset means our first
    /// <c>Node:ListenAddresses</c> entry, with an any-address (<c>0.0.0.0</c>, <c>[::]</c>) replaced by loopback. A
    /// <c>unix:/path</c> target is refused until the node can listen on a Unix socket (NL-585).
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

    /// <summary>
    /// In <see cref="TorMode.TorOnly"/>, allow <c>Node:ListenAddresses</c> entries that are not loopback. Such a
    /// listener is reachable without Tor, so a Tor-only node refuses to start with one unless this is set (NL-577).
    /// Default false.
    /// </summary>
    public bool AllowClearnetListen { get; set; }

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
    /// is on, everything else in <see cref="TorMode.TorOnly"/> except a loopback or private-network IP address
    /// (<paramref name="address"/>, see <see cref="IsLocalNetworkAddress"/>), which Tor refuses to reach and which
    /// never leaves this host or its LAN (NL-588).
    /// </summary>
    public bool UsesProxy(AddressDescriptorType type, IPAddress? address = null) => type switch
    {
        AddressDescriptorType.TorV3 => IsEnabled,
        _ => IsTorOnly && (address is null || !IsLocalNetworkAddress(address))
    };

    /// <summary>
    /// The network timeout of a connection routed through Tor (a peer dialed through the SOCKS5 port, or one that
    /// reached our onion service): the BOLT 8 handshake, the init exchange and each ping wait
    /// max(<paramref name="networkTimeout"/>, <see cref="ConnectTimeout"/> / 2), since a round trip over two circuits
    /// takes seconds (NL-590).
    /// </summary>
    public TimeSpan GetNetworkTimeout(TimeSpan networkTimeout)
    {
        var half = ConnectTimeout / 2;
        return half > networkTimeout ? half : networkTimeout;
    }

    /// <summary>
    /// True for a loopback (127.0.0.0/8, ::1), RFC 1918 private (10/8, 172.16/12, 192.168/16), link-local
    /// (169.254/16, fe80::/10) or unique-local (fc00::/7) address; an IPv4-mapped IPv6 address is judged as IPv4.
    /// Carrier-grade NAT space (100.64/10) is not local: it is the provider's network.
    /// </summary>
    public static bool IsLocalNetworkAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes is [10, ..] or [172, >= 16 and <= 31, ..] or [192, 168, ..]
                                                  or [169, 254, ..],
            AddressFamily.InterNetworkV6 => (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) || (bytes[0] & 0xfe) == 0xfc,
            _ => false
        };
    }

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
        if (IsTorOnly && !AllowClearnetListen)
            foreach (var listen in listenAddresses ?? [])
                if (IPEndPoint.TryParse(listen.Trim(), out var listenEndPoint)
                 && !IPAddress.IsLoopback(listenEndPoint.Address))
                    errors.Add($"{prefix}Tor-only mode listens on {listen}, which is reachable without Tor: listen on "
                             + $"127.0.0.1 (Node:ListenAddresses, the onion service's target) or set "
                             + $"{prefix}{nameof(AllowClearnetListen)} true.");
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
        else if (target.StartsWith(UnixPrefix, StringComparison.OrdinalIgnoreCase))
            errors.Add($"{prefix}{nameof(OnionServiceTarget)} '{target}' is a Unix socket, but the node listens on TCP "
                     + "only: use host:port.");
        else if (!TryParseEndPoint(target, out _))
            errors.Add($"{prefix}{nameof(OnionServiceTarget)} '{target}' is not host:port or [ipv6]:port.");

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