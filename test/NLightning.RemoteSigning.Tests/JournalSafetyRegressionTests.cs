using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Builders;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.Bitcoin.Services;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;

namespace NLightning.RemoteSigning.Tests;

public sealed class JournalSafetyRegressionTests
{
    [Fact]
    public void EmptyExistingStateRefusesStartupInsteadOfResettingSafetyHistory()
    {
        using var fixture = new JournalFixture();
        using (new DurableSignerState(fixture.Signer, fixture.Path, "regtest")) { }
        using (var file = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write))
            file.SetLength(0);

        Assert.Throws<InvalidDataException>(() =>
            new DurableSignerState(fixture.Signer, fixture.Path, "regtest"));
        Assert.Empty(File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void TruncatedStateRecordRefusesRestartWithoutDiscardingSafetyHistory()
    {
        using var fixture = new JournalFixture();
        var channel = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        using (var state = new DurableSignerState(fixture.Signer, fixture.Path, "regtest"))
            state.Execute(SignerOperations.MarkDataLoss, SignerWire.Decode(SignerWire.Encode([channel])));

        using (var file = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write))
            file.SetLength(file.Length - 1);
        var truncated = File.ReadAllBytes(fixture.Path);

        Assert.Throws<EndOfStreamException>(() =>
            new DurableSignerState(fixture.Signer, fixture.Path, "regtest"));
        Assert.Equal(truncated, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void JournalCannotBeReusedOnAnotherNetworkWithTheSameNodeKey()
    {
        using var fixture = new JournalFixture();
        using (var state = new DurableSignerState(fixture.Signer, fixture.Path, "regtest"))
            state.Execute(SignerOperations.MarkDataLoss,
                          SignerWire.Decode(SignerWire.Encode([new ChannelId(new byte[32])])));
        var original = File.ReadAllBytes(fixture.Path);

        Assert.Throws<InvalidDataException>(() =>
            new DurableSignerState(fixture.Signer, fixture.Path, "mainnet"));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        // Case differences name the same chain and must still recover the original journal.
        using var restored = new DurableSignerState(fixture.Signer, fixture.Path, "REGTEST");
    }

    private sealed class JournalFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                                    "rs-journal-" + Guid.NewGuid().ToString("N"));
        private readonly SecureKeyManager _keys;

        public string Path => System.IO.Path.Combine(_directory, "state");
        public LocalLightningSigner Signer { get; }

        public JournalFixture()
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_directory,
                                     UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _keys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)1, 32).ToArray(), NetworkConstants.Regtest,
                                              _ => { });
            Signer = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(),
                                              NullLogger<LocalLightningSigner>.Instance,
                                              new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest },
                                              _keys, new UtxoMemoryRepository());
        }

        public void Dispose()
        {
            _keys.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}