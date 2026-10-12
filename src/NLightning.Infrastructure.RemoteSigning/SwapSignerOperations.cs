namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Native swap operations, separate from channel and private key export operations.</summary>
public static class SwapSignerOperations
{
    public const uint Resolve = 64;
    public const uint SharedKey = 65;
    public const uint SignOutput = 66;
    public const uint CombineKeys = 67;
    public const uint Create = 68;
    public const uint RegisterNonces = 69;
    public const uint Sign = 70;
    public const uint Combine = 71;
    public const uint Cleanup = 72;
    public static bool Contains(uint operation) => operation is >= Resolve and <= Cleanup;
    public static int ArgumentCount(uint operation) => operation switch
    {
        Resolve or RegisterNonces or Combine => 2,
        SharedKey or SignOutput or CombineKeys or Create or Sign => 3,
        Cleanup => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}