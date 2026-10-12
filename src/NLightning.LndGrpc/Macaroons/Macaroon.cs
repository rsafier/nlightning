using System.Security.Cryptography;
using System.Text;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>
/// One caveat of a <see cref="Macaroon"/>: a first-party caveat has only an identifier (its condition); a third-party
/// caveat also carries a verification id and usually a location.
/// </summary>
/// <param name="Identifier">The caveat identifier (a first-party caveat's condition, as UTF-8).</param>
/// <param name="VerificationId">The verification id of a third-party caveat, or null.</param>
/// <param name="Location">The location hint of a third-party caveat, or null.</param>
public sealed record MacaroonCaveat(byte[] Identifier, byte[]? VerificationId = null, string? Location = null)
{
    /// <summary>Whether this is a first-party caveat (no verification id).</summary>
    public bool IsFirstParty => VerificationId is null;

    /// <summary>The condition text of a first-party caveat.</summary>
    public string Condition => Encoding.UTF8.GetString(Identifier);
}

/// <summary>
/// A macaroon in libmacaroons' v2 binary format, the one LND reads and writes (<c>gopkg.in/macaroon.v2</c>,
/// <c>MarshalBinary</c>): <c>0x02</c>, a header section (location 1, identifier 2, EOS), one section per caveat
/// (location 1, identifier 2, verification id 4, EOS), an EOS, then the 32-byte signature (field 6). Every field is a
/// uvarint type and a uvarint length.
/// </summary>
/// <remarks>
/// The signature chain is libmacaroons': <c>sig = HMAC-SHA256(HMAC-SHA256("macaroons-key-generator", rootKey), id)</c>,
/// then <c>sig = HMAC-SHA256(sig, caveatId)</c> for every first-party caveat. Third-party caveats are parsed but never
/// verified (<see cref="VerifySignature"/> refuses them): LND issues none. Immutable.
/// </remarks>
public sealed class Macaroon
{
    /// <summary>The v2 binary format's version byte.</summary>
    public const byte BinaryVersion = 2;

    /// <summary>The HMAC length.</summary>
    public const int SignatureLength = 32;

    private const int FieldEos = 0;
    private const int FieldLocation = 1;
    private const int FieldIdentifier = 2;
    private const int FieldVerificationId = 4;
    private const int FieldSignature = 6;

    // libmacaroons' key generator: the root key is never used directly
    private static readonly byte[] s_keyGenerator = "macaroons-key-generator"u8.ToArray();

    private readonly byte[] _signature;

    private Macaroon(string location, byte[] identifier, IReadOnlyList<MacaroonCaveat> caveats, byte[] signature)
    {
        Location = location;
        Identifier = identifier;
        Caveats = caveats;
        _signature = signature;
    }

    /// <summary>The location (LND: <c>lnd</c>).</summary>
    public string Location { get; }

    /// <summary>The identifier (LND: a bakery v3 <see cref="MacaroonId"/>).</summary>
    public byte[] Identifier { get; }

    /// <summary>The caveats in order.</summary>
    public IReadOnlyList<MacaroonCaveat> Caveats { get; }

    /// <summary>A copy of the 32-byte signature.</summary>
    public byte[] Signature => (byte[])_signature.Clone();

    /// <summary>A new macaroon signed with <paramref name="rootKey"/> (no caveats).</summary>
    public static Macaroon Create(ReadOnlySpan<byte> rootKey, byte[] identifier, string location)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(location);
        return new Macaroon(location, (byte[])identifier.Clone(), [], InitialSignature(rootKey, identifier));
    }

    /// <summary>A copy with a first-party caveat appended (e.g. <c>time-before 2030-01-02T03:04:05Z</c>).</summary>
    public Macaroon AddFirstPartyCaveat(string condition)
    {
        ArgumentException.ThrowIfNullOrEmpty(condition);
        var identifier = Encoding.UTF8.GetBytes(condition);
        return new Macaroon(Location, Identifier, [.. Caveats, new MacaroonCaveat(identifier)],
                            HMACSHA256.HashData(_signature, identifier));
    }

    /// <summary>
    /// Whether the signature is the chain of <paramref name="rootKey"/> over the identifier and the caveats. False for
    /// a macaroon with a third-party caveat (no discharge macaroons are taken). Constant time over the signature.
    /// </summary>
    public bool VerifySignature(ReadOnlySpan<byte> rootKey)
    {
        var signature = InitialSignature(rootKey, Identifier);
        foreach (var caveat in Caveats)
        {
            if (!caveat.IsFirstParty)
                return false;

            signature = HMACSHA256.HashData(signature, caveat.Identifier);
        }

        return CryptographicOperations.FixedTimeEquals(signature, _signature);
    }

    /// <summary>The v2 binary encoding (what LND writes to <c>admin.macaroon</c>).</summary>
    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(BinaryVersion);
        if (Location.Length > 0)
            WritePacket(stream, FieldLocation, Encoding.UTF8.GetBytes(Location));
        WritePacket(stream, FieldIdentifier, Identifier);
        stream.WriteByte(FieldEos);
        foreach (var caveat in Caveats)
        {
            if (!string.IsNullOrEmpty(caveat.Location))
                WritePacket(stream, FieldLocation, Encoding.UTF8.GetBytes(caveat.Location));
            WritePacket(stream, FieldIdentifier, caveat.Identifier);
            if (caveat.VerificationId is { Length: > 0 } verificationId)
                WritePacket(stream, FieldVerificationId, verificationId);
            stream.WriteByte(FieldEos);
        }

        stream.WriteByte(FieldEos);
        WritePacket(stream, FieldSignature, _signature);
        return stream.ToArray();
    }

    /// <summary>Parses the v2 binary encoding (as <c>gopkg.in/macaroon.v2</c>'s <c>parseBinaryV2</c>).</summary>
    /// <exception cref="FormatException">Not a well-formed v2 binary macaroon.</exception>
    public static Macaroon Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data[0] != BinaryVersion)
            throw new FormatException("not a v2 binary macaroon");

        var reader = new PacketReader(data[1..]);
        var header = reader.ReadSection();
        var location = string.Empty;
        var index = 0;
        if (header.Count > index && header[index].Type == FieldLocation)
            location = Encoding.UTF8.GetString(header[index++].Data);
        if (header.Count != index + 1 || header[index].Type != FieldIdentifier)
            throw new FormatException("invalid macaroon header");

        var identifier = header[index].Data;
        var caveats = new List<MacaroonCaveat>();
        while (true)
        {
            var section = reader.ReadSection();
            if (section.Count == 0)
                break;

            index = 0;
            string? caveatLocation = null;
            if (section[index].Type == FieldLocation)
                caveatLocation = Encoding.UTF8.GetString(section[index++].Data);
            if (section.Count <= index || section[index].Type != FieldIdentifier)
                throw new FormatException("no identifier in caveat");

            var caveatId = section[index++].Data;
            if (section.Count == index)
            {
                if (caveatLocation is not null)
                    throw new FormatException("location not allowed in first party caveat");

                caveats.Add(new MacaroonCaveat(caveatId));
                continue;
            }

            if (section.Count != index + 1 || section[index].Type != FieldVerificationId)
                throw new FormatException("invalid field found in caveat");

            caveats.Add(new MacaroonCaveat(caveatId, section[index].Data, caveatLocation));
        }

        var signature = reader.ReadPacket();
        if (signature.Type != FieldSignature)
            throw new FormatException("unexpected field found instead of signature");
        if (signature.Data.Length != SignatureLength)
            throw new FormatException("signature has unexpected length");
        if (!reader.IsAtEnd)
            throw new FormatException("trailing data after the macaroon");

        return new Macaroon(location, identifier, caveats, signature.Data);
    }

    private static byte[] InitialSignature(ReadOnlySpan<byte> rootKey, byte[] identifier)
    {
        Span<byte> derivedKey = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(s_keyGenerator, rootKey, derivedKey);
        try
        {
            return HMACSHA256.HashData(derivedKey, identifier);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    private static void WritePacket(Stream stream, int fieldType, byte[] data)
    {
        WriteUvarint(stream, (ulong)fieldType);
        WriteUvarint(stream, (ulong)data.Length);
        stream.Write(data);
    }

    private static void WriteUvarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private readonly record struct Packet(int Type, byte[] Data);

    /// <summary>The packet layer of the v2 format (<c>packet-v2.go</c>): fields strictly increasing per section.</summary>
    private ref struct PacketReader
    {
        private ReadOnlySpan<byte> _data;

        public PacketReader(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        public bool IsAtEnd => _data.IsEmpty;

        public List<Packet> ReadSection()
        {
            var packets = new List<Packet>();
            var previous = -1;
            while (true)
            {
                if (_data.IsEmpty)
                    throw new FormatException("section extends past end of buffer");

                var packet = ReadPacket();
                if (packet.Type == FieldEos)
                    return packets;
                if (packet.Type <= previous)
                    throw new FormatException("fields out of order");

                packets.Add(packet);
                previous = packet.Type;
            }
        }

        public Packet ReadPacket()
        {
            var type = ReadVarint();
            if (type == FieldEos)
                return new Packet(FieldEos, []);

            var length = ReadVarint();
            if (length > _data.Length)
                throw new FormatException("field data extends past end of buffer");

            var data = _data[..length].ToArray();
            _data = _data[length..];
            return new Packet(type, data);
        }

        private int ReadVarint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_data.IsEmpty)
                    throw new FormatException("varint value extends past end of buffer");

                var b = _data[0];
                _data = _data[1..];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return value > 0x7FFFFFFF
                               ? throw new FormatException("varint value out of range")
                               : (int)value;
            }

            throw new FormatException("varint value out of range");
        }
    }
}