namespace NLightning.Testing.Lnd.Tests;

using TestUtils;

public class LndSettingsTests
{
    private static readonly byte[] s_macaroon = [0x02, 0x01, 0x03, 0x6c, 0x6e, 0x64, 0xFF];

    [Fact]
    public void Given_TheFilesLndWrites_When_FromFiles_Then_TheirBytesAreLoaded()
    {
        // Arrange
        using var certificate = TestCertificates.CreateServerCertificate();
        var directory = Directory.CreateTempSubdirectory("nltg-lnd-settings-");
        try
        {
            var certPath = Path.Combine(directory.FullName, "tls.cert");
            var macaroonPath = Path.Combine(directory.FullName, "admin.macaroon");
            File.WriteAllBytes(certPath, TestCertificates.ToPem(certificate));
            File.WriteAllBytes(macaroonPath, s_macaroon);

            // Act
            var settings = LndSettings.FromFiles("alice", 10009, certPath, macaroonPath);

            // Assert
            Assert.Equal("https://alice:10009", settings.GrpcEndpoint);
            Assert.Equal(TestCertificates.ToPem(certificate), settings.TlsCert);
            Assert.Equal(s_macaroon, settings.Macaroon);
            Assert.Equal(certificate.Thumbprint, settings.LoadTlsCertificate().Thumbprint);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Given_Bytes_When_FromBytes_Then_TheSettingsKeepTheirOwnCopies()
    {
        // Arrange
        using var certificate = TestCertificates.CreateServerCertificate();
        var cert = TestCertificates.ToDer(certificate);
        var macaroon = (byte[])s_macaroon.Clone();

        // Act
        var settings = LndSettings.FromBytes("https://10.0.0.5:10009", cert, macaroon);
        cert[0] ^= 0xFF;
        macaroon[0] ^= 0xFF;

        // Assert
        Assert.Equal(TestCertificates.ToDer(certificate), settings.TlsCert);
        Assert.Equal(s_macaroon, settings.Macaroon);
    }

    [Fact]
    public void Given_Base64_When_FromBase64_Then_TheBase64ViewsRoundTrip()
    {
        // Arrange
        using var certificate = TestCertificates.CreateServerCertificate();
        var certBase64 = Convert.ToBase64String(TestCertificates.ToPem(certificate));
        var macaroonBase64 = Convert.ToBase64String(s_macaroon);

        // Act
        var settings = LndSettings.FromBase64("https://127.0.0.1:10009", certBase64, macaroonBase64);

        // Assert
        Assert.Equal(certBase64, settings.TlsCertBase64);
        Assert.Equal(macaroonBase64, settings.MacaroonBase64);
        Assert.Equal("0201036c6e64ff", settings.MacaroonHex);
    }

    [Fact]
    public void Given_LnUnitStyleProperties_When_Set_Then_TheBytesFollow()
    {
        // Act
        var settings = new LndSettings
        {
            MacaroonBase64 = Convert.ToBase64String(s_macaroon),
            TlsCertBase64 = null
        };

        // Assert
        Assert.Equal(s_macaroon, settings.Macaroon);
        Assert.Null(settings.TlsCert);
        Assert.Null(settings.TlsCertBase64);
    }

    [Theory]
    [InlineData("https://localhost:10009", "https://localhost:10009")]
    [InlineData("https://localhost:10009/", "https://localhost:10009")]
    [InlineData("alice:10009", "https://alice:10009")]
    [InlineData(" 172.17.0.3:10009 ", "https://172.17.0.3:10009")]
    [InlineData("https://[::1]:10009", "https://[::1]:10009")]
    public void Given_AnEndpoint_When_Set_Then_ItIsNormalizedToHttps(string endpoint, string expected)
    {
        // Act
        var settings = new LndSettings { GrpcEndpoint = endpoint };

        // Assert
        Assert.Equal(expected, settings.GrpcEndpoint);
    }

    [Theory]
    [InlineData("http://localhost:10009")]
    [InlineData("unix:///var/run/lnd.sock")]
    [InlineData("https://")]
    public void Given_ANonHttpsEndpoint_When_Set_Then_ArgumentException(string endpoint)
    {
        // Arrange
        var settings = new LndSettings();

        // Act
        var exception = Record.Exception(() => settings.GrpcEndpoint = endpoint);

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Theory]
    [InlineData("alice", 10009, "https://alice:10009")]
    [InlineData("127.0.0.1", 10010, "https://127.0.0.1:10010")]
    [InlineData("::1", 10009, "https://[::1]:10009")]
    [InlineData("[fd00::5]", 443, "https://[fd00::5]:443")]
    public void Given_HostAndPort_When_ToGrpcEndpoint_Then_HttpsUrl(string host, int port, string expected)
    {
        // Act
        var endpoint = LndSettings.ToGrpcEndpoint(host, port);

        // Assert
        Assert.Equal(expected, endpoint);
    }

    [Fact]
    public void Given_PemAndDerOfOneCertificate_When_Loaded_Then_BothGiveThatCertificate()
    {
        // Arrange
        using var certificate = TestCertificates.CreateServerCertificate();
        var pem = LndSettings.FromBytes("https://h:1", TestCertificates.ToPem(certificate), s_macaroon);
        var der = LndSettings.FromBytes("https://h:1", TestCertificates.ToDer(certificate), s_macaroon);

        // Act
        using var fromPem = pem.LoadTlsCertificate();
        using var fromDer = der.LoadTlsCertificate();

        // Assert
        Assert.Equal(certificate.RawData, fromPem.RawData);
        Assert.Equal(certificate.RawData, fromDer.RawData);
    }

    [Fact]
    public void Given_SettingsWithoutEndpoint_When_Validate_Then_InvalidOperationException()
    {
        // Arrange
        var settings = new LndSettings { TlsCert = [1], Macaroon = s_macaroon };

        // Act
        var exception = Record.Exception(settings.Validate);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Given_SettingsWithoutCertificateOrCallback_When_Validate_Then_InvalidOperationException()
    {
        // Arrange
        var settings = new LndSettings { GrpcEndpoint = "https://h:1", Macaroon = s_macaroon };

        // Act
        var exception = Record.Exception(settings.Validate);

        // Assert
        Assert.Contains("TlsCert", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public void Given_AnEmptyMacaroon_When_Validate_Then_InvalidOperationException()
    {
        // Arrange
        var settings = new LndSettings { GrpcEndpoint = "https://h:1", TlsCert = [1], Macaroon = [] };

        // Act
        var exception = Record.Exception(settings.Validate);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Given_ACallbackAndNoMacaroon_When_Validate_Then_Valid()
    {
        // Arrange
        var settings = new LndSettings
        {
            GrpcEndpoint = "https://h:1",
            ServerCertificateValidation = (_, _, _) => true
        };

        // Act
        var exception = Record.Exception(settings.Validate);

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_Settings_When_Cloned_Then_TheCopySharesNoArrays()
    {
        // Arrange
        var settings = new LndSettings
        {
            GrpcEndpoint = "https://h:1",
            TlsCert = [1, 2, 3],
            Macaroon = [4, 5, 6],
            MaxReceiveMessageSize = 42
        };

        // Act
        var clone = settings.Clone();
        settings.TlsCert[0] = 9;
        settings.Macaroon[0] = 9;

        // Assert
        Assert.Equal("https://h:1", clone.GrpcEndpoint);
        Assert.Equal([1, 2, 3], clone.TlsCert);
        Assert.Equal([4, 5, 6], clone.Macaroon);
        Assert.Equal(42, clone.MaxReceiveMessageSize);
        Assert.Equal(LndSettings.DefaultMaxMessageSize, clone.MaxSendMessageSize);
    }
}