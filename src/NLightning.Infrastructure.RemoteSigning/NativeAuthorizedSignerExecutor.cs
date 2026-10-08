using NLightning.Signing.Contracts;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Signer-installed semantic validator. It must derive financial authority from trusted signer state.</summary>
public interface INativeSignerRequestValidator
{
    void Validate(NativeSignerBinding binding, NativeSignerIntent intent);
    void ValidateReplay(NativeSignerBinding binding, NativeSignerIntent intent) =>
        throw new NotSupportedException("A purpose-specific receipt replay validator is required.");
}

/// <summary>
/// Coordinates independent authority and local safety history. Use before any RPC cache or journal replay.
/// A failure after local journal mutation blocks this executor until explicit recovery constructs a new instance.
/// </summary>
public sealed class NativeAuthorizedSignerExecutor
{
    private readonly NativeSignerAuthority _authority;
    private readonly NativeSignerBinding _binding;
    private readonly DurableSignerState _journal;
    private readonly INativeSignerRequestValidator _validator;
    private readonly NativeSignerSafetyCheckpointSet _checkpoints;
    private readonly Lock _gate = new();
    private NativeSignerExecution _execution;
    private bool _faulted;

    public NativeAuthorizedSignerExecutor(NativeSignerAuthority authority, NativeSignerBinding binding,
                                          DurableSignerState journal, NativeSignerExecution execution,
                                          INativeSignerRequestValidator validator,
                                          NativeSignerSafetyCheckpointSet checkpoints)
    {
        _authority = authority;
        _binding = binding;
        _journal = journal;
        _execution = execution;
        _validator = validator;
        _checkpoints = checkpoints;
        _checkpoints.ValidateEnrollment(binding);
        if (JournalCheckpoint() != execution.SignerCheckpoint)
            throw new InvalidDataException("Signer history does not match independent authority; reconcile the original operation.");
    }

    public void ValidateEnrollment(NativeSignerBinding configuredBinding)
    {
        if (_binding != configuredBinding)
            throw new UnauthorizedAccessException("RPC enrollment does not match signer authority enrollment.");
    }

    public NativeSignerAuthorityResult Execute(SigningRequest request, string authenticatedWriterId,
                                               long authenticatedEpoch, Func<byte[]> execute)
    {
        lock (_gate)
        {
            RequireExecution(request, authenticatedWriterId, authenticatedEpoch);
            var before = JournalCheckpoint();
            var intent = NativeSignerIntent.FromRequest(request);
            try
            {
                var result = _authority.Execute(_binding, _execution, intent, () =>
                {
                    _validator.Validate(_binding, intent);
                    return execute();
                }, JournalCheckpoint, () =>
                {
                    _validator.ValidateReplay(_binding, intent);
                    if (DurableSignerState.SupportsReconciliation(request.Operation)
                     && _journal.Reconcile(request).Outcome != RequestOutcome.Completed)
                        throw new InvalidOperationException("Signer receipt is invalidated, unknown or missing; no cached result may be returned.");
                });
                _execution = _execution with { Checkpoint = result.Checkpoint, SignerCheckpoint = result.SignerCheckpoint };
                return result;
            }
            catch
            {
                FaultIfHistoryChanged(before);
                throw;
            }
        }
    }

    public T AuthorizeReconciliation<T>(SigningRequest request, string authenticatedWriterId,
                                        long authenticatedEpoch, Func<T> readReconciliation)
    {
        lock (_gate)
        {
            RequireExecution(request, authenticatedWriterId, authenticatedEpoch);
            return _authority.RequireCurrentWriter(_binding, _execution, JournalCheckpoint, readReconciliation);
        }
    }

    private void RequireExecution(SigningRequest request, string writerId, long epoch)
    {
        if (_faulted) throw new InvalidOperationException("Signer authority coordination failed; explicit recovery is required.");
        if (_execution.Epoch != epoch || _execution.WriterId != writerId)
            throw new UnauthorizedAccessException("Authenticated writer does not match this signer execution context.");
        if (request.Version != 1 || request.NodeId != _binding.NodeId || request.OwnerId != _binding.OwnerId
         || request.SignerId != _binding.SignerId || request.Network != _binding.Network)
            throw new UnauthorizedAccessException("Request does not match immutable signer enrollment.");
    }

    private void FaultIfHistoryChanged(string before)
    {
        // Local safety mutation may have committed even when the independent transaction did not.
        // Refuse a fresh ID; a trusted recovery path must reconcile the original operation and digest.
        try { _faulted |= JournalCheckpoint() != before; }
        catch { _faulted = true; }
    }

    private string JournalCheckpoint() => _checkpoints.GetCheckpoint();
}