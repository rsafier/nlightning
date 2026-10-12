using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

/// <summary>
/// The HTLC switch's interceptor callback was asked to resume or fail a held forward whose incoming channel went on chain
/// after it was held (NL-1182): nothing was done. Only a settle is still possible (LND's on-chain interception: resume
/// and fail are refused); the interceptor hub moves the hold on chain when it sees this.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ForwardHeldOnChainException(string message) : InvalidOperationException(message);