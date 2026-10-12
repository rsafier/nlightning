using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NLightning.Infrastructure.Protocol.Dns;

/// <summary>
/// The RFC 1035 DNS messages of a stub resolver over TCP (NL-571): one question, no EDNS, recursion desired, and the
/// parser for a response (the answers of the asked type, plus the additional section's A/AAAA as glue). Names are read
/// with compression pointers. Everything is validated by length; a malformed message does not parse instead of
/// throwing.
/// </summary>
internal static class DnsTcpMessage
{
    private const int HeaderLength = 12;
    private const ushort FlagResponse = 0x8000;
    private const ushort FlagRecursionDesired = 0x0100;
    private const int TypeA = 1;
    private const int TypeAaaa = 28;
    private const int TypeSrv = 33;

    /// <summary>
    /// A query for <paramref name="name"/> of <paramref name="kind"/> (the caller prefixes it with its 2-byte TCP
    /// length).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a plain ASCII DNS name.</exception>
    public static byte[] BuildQuery(ushort id, string name, DnsRecordKind kind)
    {
        var labels = name.Trim().TrimEnd('.').Split('.');
        if (labels.Any(l => l.Length is 0 or > 63))
            throw new ArgumentException($"'{name}' is not a DNS name.", nameof(name));

        var question = new byte[labels.Sum(l => l.Length + 1) + 1]; // and the root's zero byte
        var offset = 0;
        foreach (var label in labels)
        {
            if (label.Any(c => c > 0x7F))
                throw new ArgumentException($"'{name}' is not a plain ASCII DNS name.", nameof(name));

            question[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, 0, label.Length, question, offset);
        }

        var message = new byte[HeaderLength + question.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(message, id);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), FlagRecursionDesired);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1); // QDCOUNT
        question.CopyTo(message, HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(^4), TypeOf(kind));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(^2), 1); // IN
        return message;
    }

    /// <summary>
    /// Parses a response: the status from its RCODE, the answers of <paramref name="kind"/> (SRV fields, or the
    /// addresses of an A/AAAA query), and the additional section's A/AAAA as glue. False when the message is
    /// malformed or answers another query's id.
    /// </summary>
    public static bool TryParseResponse(byte[] message, ushort expectedId, DnsRecordKind kind,
                                        out DnsLookupResponse response)
    {
        response = DnsLookupResponse.Of(DnsLookupStatus.Other);
        if (message.Length < HeaderLength)
            return false;

        var reader = new Reader(message);
        var id = reader.ReadUInt16();
        var flags = reader.ReadUInt16();
        if (id != expectedId || (flags & FlagResponse) == 0)
        {
            response = DnsLookupResponse.Of(DnsLookupStatus.Other);
            return false;
        }

        var srv = new List<DnsSrv>();
        var addresses = new List<IPAddress>();
        var glue = new List<DnsGlue>();
        var questions = reader.ReadUInt16();
        var answers = reader.ReadUInt16();
        reader.ReadUInt16(); // authority
        var additionals = reader.ReadUInt16();
        for (var i = 0; i < questions; i++)
            if (!reader.ReadName(out _) || !reader.Skip(4))
                return false;

        if (!ReadRecords(ref reader, answers, kind, srv, addresses, null)
         || !ReadRecords(ref reader, additionals, kind, srv, addresses, glue))
            return false;

        response = new DnsLookupResponse(StatusOf(flags), srv, addresses, glue);
        return true;
    }

    private static bool ReadRecords(ref Reader reader, int count, DnsRecordKind kind, List<DnsSrv> srv,
                                    List<IPAddress> addresses, List<DnsGlue>? glue)
    {
        for (var i = 0; i < count; i++)
        {
            if (!reader.ReadName(out var name) || !reader.TryRead(10, out var fixedFields))
                return false;

            var type = BinaryPrimitives.ReadUInt16BigEndian(fixedFields);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(fixedFields[8..]);

            // The additional section only ever contributes glue; the answer section carries the asked type
            if (glue is not null)
            {
                if (!reader.TryRead(dataLength, out var data))
                    return false;
                if (type == TypeA && data.Length == 4 || type == TypeAaaa && data.Length == 16)
                    glue.Add(new DnsGlue(name, new IPAddress(data)));
                continue;
            }

            switch (type, kind)
            {
                case (TypeSrv, DnsRecordKind.Srv) when dataLength >= 6:
                    // The fixed fields are read from the record, the target from the message: RFC 2782 forbids
                    // compressing it, but a name decompresses over the whole message all the same
                    if (!reader.TryRead(6, out var fields) || !reader.ReadName(out var target))
                        return false;

                    srv.Add(new DnsSrv(BinaryPrimitives.ReadUInt16BigEndian(fields),
                                       BinaryPrimitives.ReadUInt16BigEndian(fields[2..]),
                                       BinaryPrimitives.ReadUInt16BigEndian(fields[4..]), target));
                    break;
                case (TypeA, DnsRecordKind.A) when dataLength == 4:
                case (TypeAaaa, DnsRecordKind.Aaaa) when dataLength == 16:
                    if (!reader.TryRead(dataLength, out var address))
                        return false;

                    addresses.Add(new IPAddress(address));
                    break;
                default:
                    if (!reader.Skip(dataLength))
                        return false;

                    break;
            }
        }

        return true;
    }

    private static ushort TypeOf(DnsRecordKind kind) => kind switch
    {
        DnsRecordKind.Srv => TypeSrv,
        DnsRecordKind.A => TypeA,
        DnsRecordKind.Aaaa => TypeAaaa,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static DnsLookupStatus StatusOf(ushort flags) => (flags & 0x000F) switch
    {
        0 => DnsLookupStatus.NoError,
        2 => DnsLookupStatus.ServFail,
        3 => DnsLookupStatus.NxDomain,
        5 => DnsLookupStatus.Refused,
        _ => DnsLookupStatus.Other
    };

    /// <summary>
    /// Reads the big-endian scalars and names of one message; names decompress over the whole message, so an SRV
    /// target is read from the main reader, never from its RDATA alone.
    /// </summary>
    private ref struct Reader(byte[] message)
    {
        private readonly byte[] _message = message;
        private ReadOnlySpan<byte> _span = message;
        private int _offset;

        public ushort ReadUInt16()
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(_span);
            Advance(2);
            return value;
        }

        public bool TryRead(int count, out ReadOnlySpan<byte> data)
        {
            if (_span.Length < count)
            {
                data = default;
                return false;
            }

            data = _span[..count];
            Advance(count);
            return true;
        }

        public bool Skip(int count) => TryRead(count, out _);

        /// <summary>Reads a (possibly compressed) name as text, without the final dot.</summary>
        public bool ReadName(out string name)
        {
            name = string.Empty;
            var pointer = _offset;
            var consumed = 0;
            var jumped = false;
            var guard = 0;
            var builder = new StringBuilder();
            while (pointer >= 0 && pointer < _message.Length && guard++ <= _message.Length)
            {
                var code = _message[pointer];
                if ((code & 0xC0) == 0xC0)
                {
                    if (pointer + 1 >= _message.Length)
                        return false;

                    // A pointer costs two bytes on the wire, wherever it stands
                    if (!jumped)
                        consumed = pointer + 2 - _offset;

                    pointer = (_message[pointer] & 0x3F) << 8 | _message[pointer + 1];
                    jumped = true;
                    continue;
                }

                if ((code & 0xC0) != 0)
                    return false; // Reserved or an unknown label scheme

                if (code == 0)
                {
                    if (!jumped)
                        consumed = pointer + 1 - _offset;

                    name = builder.ToString();
                    return Skip(consumed);
                }

                if (pointer + 1 + code > _message.Length)
                    return false;

                builder.Append(Encoding.ASCII.GetString(_message, pointer + 1, code));
                if (_message[pointer + 1 + code] != 0)
                    builder.Append('.');

                pointer += 1 + code;
            }

            return false;
        }

        private void Advance(int count)
        {
            _span = _span[count..];
            _offset += count;
        }
    }
}