using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Protocol.Onion.Enums;

/// <summary>
/// Represents a failure while processing an onion packet or payload.
/// </summary>
/// <remarks>
/// Carries the BOLT 4 <see cref="FailureCode"/> and optional failure data so the caller can build the right
/// <c>update_fail_htlc</c> / <c>update_fail_malformed_htlc</c> reply.
/// </remarks>
[ExcludeFromCodeCoverage]
public class OnionException : ErrorException
{
    /// <summary>
    /// The failure code to report.
    /// </summary>
    public FailureCode FailureCode { get; }

    /// <summary>
    /// The failure-specific data (e.g. sha256_of_onion, or bigsize type || u16 offset), if any.
    /// </summary>
    public ReadOnlyMemory<byte>? FailureData { get; }

    public OnionException(FailureCode failureCode, string message, ReadOnlyMemory<byte>? data = null)
        : base(message)
    {
        FailureCode = failureCode;
        FailureData = data;
    }

    public OnionException(FailureCode failureCode, string message, Exception innerException,
                          ReadOnlyMemory<byte>? data = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
        FailureData = data;
    }
}