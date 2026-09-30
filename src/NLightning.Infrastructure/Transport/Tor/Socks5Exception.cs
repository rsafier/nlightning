using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Exceptions;

/// <summary>
/// A SOCKS5 proxy refused a request or broke the protocol. It is a <see cref="ConnectionException"/>: the dial failed.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class Socks5Exception : ConnectionException
{
    /// <summary>
    /// The <c>REP</c> byte of the proxy's <c>CONNECT</c> reply, when it sent one.
    /// </summary>
    public byte? ReplyCode { get; }

    public Socks5Exception(string message) : base(message) { }
    public Socks5Exception(string message, Exception innerException) : base(message, innerException) { }

    public Socks5Exception(byte replyCode, string message) : base(message)
    {
        ReplyCode = replyCode;
    }

    /// <summary>
    /// The meaning of a <c>REP</c> byte: RFC 1928's codes and Tor's extended onion service errors (proposal 304, sent
    /// when the <c>SocksPort</c> has the <c>ExtendedErrors</c> flag).
    /// </summary>
    public static string Describe(byte replyCode) => replyCode switch
    {
        0x01 => "general SOCKS server failure",
        0x02 => "connection not allowed by ruleset (exit policy)",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused",
        0x06 => "TTL expired (Tor: the circuit or stream timed out)",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        0xF0 => "onion service descriptor not found (the service is offline or unknown)",
        0xF1 => "onion service descriptor is invalid",
        0xF2 => "onion service introduction failed",
        0xF3 => "onion service rendezvous failed",
        0xF4 => "onion service requires client authorization",
        0xF5 => "onion service client authorization is wrong",
        0xF6 => "invalid onion address",
        0xF7 => "onion service introduction timed out",
        _ => $"reply code 0x{replyCode:x2}"
    };
}