// Part of the port of LNUnit.LND (https://github.com/nbd-wtf/LNUnit), Copyright (c) 2024-2025 nbd, MIT License; the
// full text is in LICENSE-LNUnit.txt next to this file. LNUnit.LND accepted any server certificate; this pins it.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace NLightning.Testing.Lnd;

/// <summary>
/// Server certificate checks for LND's self-signed <c>tls.cert</c>. Pinning accepts exactly the pinned certificate,
/// whatever the chain and host name errors (LND's certificate is its own root and often names neither the container
/// address nor the forwarded port), and rejects every other certificate.
/// </summary>
public static class LndCertificatePinning
{
    /// <summary>True when <paramref name="presented"/> is byte for byte the <paramref name="pinned"/> certificate.</summary>
    public static bool Matches(X509Certificate2 pinned, X509Certificate? presented)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        return presented is not null && pinned.RawDataMemory.Span.SequenceEqual(presented.GetRawCertData());
    }

    /// <summary>A TLS callback that accepts only the pinned certificate.</summary>
    public static RemoteCertificateValidationCallback CreateCallback(X509Certificate2 pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        return (_, certificate, _, _) => Matches(pinned, certificate);
    }

    /// <summary>A TLS callback that hands the decision to <paramref name="validate"/>; no certificate is always a rejection.</summary>
    public static RemoteCertificateValidationCallback CreateCallback(
        Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        return (_, certificate, chain, errors) => certificate switch
        {
            null => false,
            X509Certificate2 certificate2 => validate(certificate2, chain, errors),
            _ => validate(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()), chain, errors)
        };
    }

    /// <summary>The callback <paramref name="settings"/> asks for: its custom validation if set, else its pinned certificate.</summary>
    public static RemoteCertificateValidationCallback CreateCallback(LndSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ServerCertificateValidation is { } validate
            ? CreateCallback(validate)
            : CreateCallback(settings.LoadTlsCertificate());
    }
}