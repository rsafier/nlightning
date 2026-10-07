using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Signer;

/// <summary>Persists allocation indices and public identity, never injected secret material.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class InjectedKeyIndexJournal : IDisposable
{
    private static readonly byte[] s_magic = "NLIDX001"u8.ToArray();
    private readonly FileStream _stream;
    private readonly byte[] _identity;
    private bool _failed;

    public uint LastIndex { get; private set; }
    public bool StateInitialized { get; private set; }

    public InjectedKeyIndexJournal(string path, string network, byte[] nodePublicKey)
    {
        var existing = Path.Exists(path);
        if (existing || new FileInfo(path).LinkTarget is not null)
            SignerFiles.RequirePrivate(path);
        _identity = SHA256.HashData(Encoding.UTF8.GetBytes(network + "|" + Convert.ToHexString(nodePublicKey)));
        var fileOptions = new FileStreamOptions
        {
            Mode = existing ? FileMode.Open : FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!existing)
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _stream = new FileStream(path, fileOptions);
        try
        {
            if (_stream.Length == 0)
            {
                if (existing)
                    throw new IOException("Injected key index journal is empty or truncated.");
                _stream.Write(s_magic);
                _stream.Write(_identity);
                _stream.Flush(true);
                SignerFiles.SyncParentDirectory(path);
            }
            else
            {
                Span<byte> header = stackalloc byte[40];
                _stream.ReadExactly(header);
                if (!header[..8].SequenceEqual(s_magic) || !header[8..].SequenceEqual(_identity))
                    throw new IOException("Injected seed identity or network does not match the allocation journal.");
                Span<byte> record = stackalloc byte[36];
                while (_stream.Position < _stream.Length)
                {
                    _stream.ReadExactly(record);
                    var index = BinaryPrimitives.ReadUInt32BigEndian(record);
                    if (!record[4..].SequenceEqual(SHA256.HashData(record[..4])))
                        throw new IOException("Injected key index journal is invalid.");
                    if (index == 0)
                    {
                        if (StateInitialized || LastIndex > 0)
                            throw new IOException("Injected key index journal has an invalid state marker.");
                        StateInitialized = true;
                    }
                    else
                    {
                        if (!StateInitialized || index <= LastIndex)
                            throw new IOException("Injected key index journal is not monotonic.");
                        LastIndex = index;
                    }
                }
            }
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public void Persist(uint index)
    {
        if (_failed)
            throw new IOException("Injected key index journal failed; restart after repairing durable state.");
        if (index <= LastIndex)
            return;
        if (!StateInitialized)
            throw new IOException("Injected signer state must be durable before channel key allocation.");
        Append(index);
        LastIndex = index;
    }

    public void MarkStateInitialized()
    {
        if (StateInitialized)
            return;
        Append(0);
        StateInitialized = true;
    }

    private void Append(uint index)
    {
        if (_failed)
            throw new IOException("Injected key index journal failed; restart after repairing durable state.");
        Span<byte> record = stackalloc byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(record, index);
        SHA256.HashData(record[..4], record[4..]);
        try
        {
            _stream.Write(record);
            _stream.Flush(true);
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();
}