using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace NLightning.Testing.Lnd.Tests;

using TestUtils;

public class LndCertificatePinningTests
{
    [Fact]
    public void Given_ThePinnedCertificate_When_Presented_Then_Accepted()
    {
        // Arrange
        using var pinned = TestCertificates.CreateServerCertificate();
        using var presented = X509CertificateLoader.LoadCertificate(pinned.RawData);
        var callback = LndCertificatePinning.CreateCallback(pinned);

        // Act
        var accepted = callback(this, presented, null, SslPolicyErrors.RemoteCertificateChainErrors
                                                       | SslPolicyErrors.RemoteCertificateNameMismatch);

        // Assert
        Assert.True(accepted);
    }

    [Fact]
    public void Given_AnotherCertificateWithTheSameSubject_When_Presented_Then_Rejected()
    {
        // Arrange
        using var pinned = TestCertificates.CreateServerCertificate();
        using var other = TestCertificates.CreateServerCertificate();
        var callback = LndCertificatePinning.CreateCallback(pinned);

        // Act
        var accepted = callback(this, other, null, SslPolicyErrors.None);

        // Assert
        Assert.Equal(pinned.Subject, other.Subject);
        Assert.False(accepted);
    }

    [Fact]
    public void Given_NoCertificate_When_Presented_Then_Rejected()
    {
        // Arrange
        using var pinned = TestCertificates.CreateServerCertificate();

        // Act
        var pinnedAccepts = LndCertificatePinning.CreateCallback(pinned)(this, null, null, SslPolicyErrors.None);
        var customAccepts = LndCertificatePinning.CreateCallback((_, _, _) => true)(this, null, null,
                                                                                     SslPolicyErrors.None);

        // Assert
        Assert.False(pinnedAccepts);
        Assert.False(customAccepts);
    }

    [Fact]
    public void Given_SettingsWithACustomCallback_When_CreatingTheCallback_Then_TheCustomOneDecides()
    {
        // Arrange
        using var pinned = TestCertificates.CreateServerCertificate();
        using var other = TestCertificates.CreateServerCertificate();
        X509Certificate2? seen = null;
        var settings = LndSettings.FromBytes("https://h:1", TestCertificates.ToPem(pinned), null);
        settings.ServerCertificateValidation = (certificate, _, errors) =>
        {
            seen = certificate;
            return errors == SslPolicyErrors.RemoteCertificateChainErrors;
        };
        var callback = LndCertificatePinning.CreateCallback(settings);

        // Act
        var accepted = callback(this, other, null, SslPolicyErrors.RemoteCertificateChainErrors);

        // Assert
        Assert.True(accepted);
        Assert.Equal(other.Thumbprint, seen?.Thumbprint);
    }

    [Fact]
    public void Given_SettingsWithACertificate_When_CreatingTheCallback_Then_ItPinsThatCertificate()
    {
        // Arrange
        using var pinned = TestCertificates.CreateServerCertificate();
        using var other = TestCertificates.CreateServerCertificate();
        var callback = LndCertificatePinning.CreateCallback(
            LndSettings.FromBytes("https://h:1", TestCertificates.ToPem(pinned), null));

        // Act
        var acceptsPinned = callback(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors);
        var acceptsOther = callback(this, other, null, SslPolicyErrors.None);

        // Assert
        Assert.True(acceptsPinned);
        Assert.False(acceptsOther);
    }
}