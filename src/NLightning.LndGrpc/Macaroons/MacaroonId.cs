using Google.Protobuf;

namespace NLightning.LndGrpc.Macaroons;

/// <summary>An operation a macaroon allows: LND's <c>bakery.Op</c> (entity, action), e.g. <c>invoices</c>/<c>write</c>.</summary>
/// <param name="Entity">The entity (<c>onchain</c>, <c>offchain</c>, <c>invoices</c>, ..., or <c>uri</c>).</param>
/// <param name="Action">The action (<c>read</c>, <c>write</c>, <c>generate</c>, or a full gRPC method for <c>uri</c>).</param>
public readonly record struct MacaroonOp(string Entity, string Action)
{
    /// <inheritdoc />
    public override string ToString() => $"{Entity}:{Action}";
}

/// <summary>
/// The identifier of a macaroon minted by macaroon-bakery v3 (the one LND uses): the version byte <c>0x03</c>, then the
/// protobuf <c>MacaroonId { bytes nonce = 1; bytes storageId = 2; repeated Op ops = 3 }</c> with
/// <c>Op { string entity = 1; repeated string actions = 2 }</c>, the operations canonical (sorted by entity, then
/// action, without duplicates, grouped by entity: the bakery's <c>CanonicalOps</c> and <c>macaroonIdOps</c>).
/// </summary>
public sealed class MacaroonId
{
    /// <summary>The bakery version byte LND requires (<c>bakery.Version3</c>).</summary>
    public const byte Version3 = 3;

    /// <summary>The nonce length the bakery uses (a UUID's 16 bytes).</summary>
    public const int NonceLength = 16;

    private const uint NonceTag = (1 << 3) | 2;
    private const uint StorageIdTag = (2 << 3) | 2;
    private const uint OpTag = (3 << 3) | 2;
    private const uint EntityTag = (1 << 3) | 2;
    private const uint ActionTag = (2 << 3) | 2;

    public MacaroonId(byte[] nonce, byte[] storageId, IEnumerable<MacaroonOp> ops)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(storageId);
        Nonce = nonce;
        StorageId = storageId;
        Ops = Canonical(ops);
    }

    /// <summary>The nonce that makes every macaroon of one root key unique.</summary>
    public byte[] Nonce { get; }

    /// <summary>The root key id (LND: <c>"0"</c>, its <c>DefaultRootKeyID</c>).</summary>
    public byte[] StorageId { get; }

    /// <summary>The operations, canonical.</summary>
    public IReadOnlyList<MacaroonOp> Ops { get; }

    /// <summary>The operations sorted by entity then action (ordinal, as Go compares strings), duplicates removed.</summary>
    public static IReadOnlyList<MacaroonOp> Canonical(IEnumerable<MacaroonOp> ops) =>
        ops.Distinct()
           .OrderBy(o => o.Entity, StringComparer.Ordinal)
           .ThenBy(o => o.Action, StringComparer.Ordinal)
           .ToList();

    /// <summary>The identifier bytes: <c>0x03 || protobuf</c>, field by field in number order like Go's marshaller.</summary>
    public byte[] Encode()
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(Version3);
        var output = new CodedOutputStream(buffer, leaveOpen: true);
        if (Nonce.Length > 0)
        {
            output.WriteTag(NonceTag);
            output.WriteBytes(ByteString.CopyFrom(Nonce));
        }

        if (StorageId.Length > 0)
        {
            output.WriteTag(StorageIdTag);
            output.WriteBytes(ByteString.CopyFrom(StorageId));
        }

        foreach (var group in Ops.GroupBy(o => o.Entity))
        {
            using var opBuffer = new MemoryStream();
            var opOutput = new CodedOutputStream(opBuffer, leaveOpen: true);
            if (group.Key.Length > 0)
            {
                opOutput.WriteTag(EntityTag);
                opOutput.WriteString(group.Key);
            }

            foreach (var op in group)
            {
                opOutput.WriteTag(ActionTag);
                opOutput.WriteString(op.Action);
            }

            opOutput.Flush();
            output.WriteTag(OpTag);
            output.WriteBytes(ByteString.CopyFrom(opBuffer.ToArray()));
        }

        output.Flush();
        return buffer.ToArray();
    }

    /// <summary>Decodes a bakery v3 identifier (unknown protobuf fields skipped).</summary>
    /// <exception cref="FormatException">Not a version 3 identifier, malformed, or without operations.</exception>
    public static MacaroonId Decode(ReadOnlySpan<byte> identifier)
    {
        if (identifier.IsEmpty || identifier[0] != Version3)
            throw new FormatException("not a bakery version 3 macaroon id");

        try
        {
            var input = new CodedInputStream(identifier[1..].ToArray());
            var nonce = Array.Empty<byte>();
            var storageId = Array.Empty<byte>();
            var ops = new List<MacaroonOp>();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case NonceTag:
                        nonce = input.ReadBytes().ToByteArray();
                        break;
                    case StorageIdTag:
                        storageId = input.ReadBytes().ToByteArray();
                        break;
                    case OpTag:
                        ops.AddRange(DecodeOp(input.ReadBytes().ToByteArray()));
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            if (ops.Count == 0)
                throw new FormatException("no operations found in macaroon");

            return new MacaroonId(nonce, storageId, ops);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new FormatException("cannot unmarshal macaroon id", e);
        }
    }

    private static IEnumerable<MacaroonOp> DecodeOp(byte[] data)
    {
        var input = new CodedInputStream(data);
        var entity = string.Empty;
        var actions = new List<string>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case EntityTag:
                    entity = input.ReadString();
                    break;
                case ActionTag:
                    actions.Add(input.ReadString());
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        if (actions.Count == 0)
            throw new FormatException("no operations found in macaroon");

        return actions.Select(a => new MacaroonOp(entity, a));
    }
}