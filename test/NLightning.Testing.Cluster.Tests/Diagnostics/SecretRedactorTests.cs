namespace NLightning.Testing.Cluster.Tests.Diagnostics;

using Cluster.Diagnostics;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("bitcoind -rpcuser=nltg -rpcpassword=hunter2 -regtest", "bitcoind -rpcuser=nltg -rpcpassword=*** -regtest")]
    [InlineData("--bitcoind.rpcpass=hunter2", "--bitcoind.rpcpass=***")]
    [InlineData("--bitcoin-rpcpassword=hunter2 --alias=alice", "--bitcoin-rpcpassword=*** --alias=alice")]
    [InlineData("{\"password\": \"hunter2\", \"id\": \"02ab\"}", "{\"password\": \"***\", \"id\": \"02ab\"}")]
    [InlineData("rpcauth=nltg:abc$def", "rpcauth=***")]
    [InlineData("TOKEN: abc.def", "TOKEN: ***")]
    public void Given_ASecretAssignment_When_Redacted_Then_ItsValueIsMasked(string text, string expected)
    {
        // Act
        var redacted = SecretRedactor.Redact(text);

        // Assert
        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("hunter2", redacted);
    }

    [Theory]
    [InlineData("2026-10-02T10:00:00Z UpdateTip: new best=0f9188 height=101")]
    [InlineData("{\"id\": \"02ab\", \"num_peers\": 1}")]
    [InlineData("")]
    public void Given_TextWithoutSecrets_When_Redacted_Then_ItIsUnchanged(string text)
    {
        // Act & Assert
        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void Given_RedactedText_When_RedactedAgain_Then_ItStaysTheSame()
    {
        // Arrange
        var once = SecretRedactor.Redact("-rpcpassword=hunter2");

        // Act & Assert
        Assert.Equal(once, SecretRedactor.Redact(once));
    }

    [Theory]
    [InlineData("RPC_PASSWORD", true)]
    [InlineData("LND_MACAROON", true)]
    [InlineData("API_KEY", true)]
    [InlineData("LIGHTNINGD_NETWORK", false)]
    [InlineData(null, false)]
    public void Given_AnEnvName_When_Checked_Then_SecretNamesAreRecognized(string? name, bool expected)
    {
        // Act & Assert
        Assert.Equal(expected, SecretRedactor.IsSecretName(name));
    }
}