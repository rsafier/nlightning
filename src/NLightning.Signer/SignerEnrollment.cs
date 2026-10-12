using System.Runtime.Versioning;
using System.Text;

namespace NLightning.Signer;

using Domain.Signing;

[UnsupportedOSPlatform("windows")]
internal static class SignerEnrollment
{
    public static void Bind(string statePath, NodeSigningContext context)
    {
        context.Validate();
        var path = statePath + ".enrollment";
        var expected = Encoding.UTF8.GetBytes(string.Join("\n", "1", context.NodeId, context.OwnerId,
            context.SignerId, context.Network.ToLowerInvariant(), context.NodePublicKey.ToString()));
        if (File.Exists(path))
        {
            SignerFiles.RequirePrivate(path);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Signer state belongs to another immutable enrollment.");
            return;
        }
        if (File.Exists(statePath))
            throw new InvalidOperationException("Signer state has no enrollment; refuse to assign existing safety history.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                stream.Write(expected);
                stream.Flush(true);
            }
            File.Move(temporary, path, false);
            SignerFiles.SyncParentDirectory(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}