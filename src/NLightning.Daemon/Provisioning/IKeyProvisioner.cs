namespace NLightning.Daemon.Provisioning;

using Contracts.Provisioning;

/// <summary>
/// Delivers the node's key material to a locked daemon (NL-1349). The development provider
/// (<see cref="EndpointKeyProvisioner"/>) takes an encrypted key file and its password over a provisioning endpoint
/// (the Unix socket or stdin); later providers (an attested KMS unwrap, an operator import encrypted to an attested
/// ephemeral key) yield their own <see cref="ProvisionedKeyMaterial"/> through the same interface.
/// </summary>
public interface IKeyProvisioner : IAsyncDisposable
{
    /// <summary>Where the provisioner listens, for the log (never holds a secret).</summary>
    string Description { get; }

    /// <summary>Opens the provisioning endpoint.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the next unlock attempt; null when the provisioner can deliver nothing more (its input ended). Status
    /// requests are answered by the provisioner itself.
    /// </summary>
    Task<KeyProvisioningAttempt?> NextAttemptAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One delivery of key material. The locked start opens the material, checks it and answers through
/// <see cref="RespondAsync"/>; disposing the attempt wipes the material.
/// </summary>
public sealed class KeyProvisioningAttempt(ProvisionedKeyMaterial material, KeyProvisioningSecrets? secrets,
                                           Func<KeyProvisioningResponse, CancellationToken, Task> respond)
    : IDisposable
{
    public ProvisionedKeyMaterial Material { get; } = material;

    /// <summary>At-rest secrets delivered with the key (NL-1352), or null.</summary>
    public KeyProvisioningSecrets? Secrets { get; } = secrets;

    public Task RespondAsync(KeyProvisioningResponse response, CancellationToken cancellationToken) =>
        respond(response, cancellationToken);

    public void Dispose() => Material.Dispose();
}

/// <summary>Key material in one of the forms a provisioner delivers.</summary>
public abstract class ProvisionedKeyMaterial : IDisposable
{
    public abstract void Dispose();
}

/// <summary>
/// An encrypted NLightning key file (its exact bytes) and its password: the file's version decides the derivation and
/// so the node id (NL-158, NL-159), which is why the development provider takes the file, not a raw seed.
/// </summary>
public sealed class EncryptedKeyFileMaterial(byte[] keyFile, string password) : ProvisionedKeyMaterial
{
    public byte[] KeyFile { get; } = keyFile;
    public string Password { get; } = password;

    public override void Dispose() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(KeyFile);
}