using Microsoft.Extensions.Logging;

namespace NLightning.Application.Onchain.Fees;

using Domain.Bitcoin.Interfaces;

/// <summary>
/// The fee estimate the BOLT 5 resolvers and the <see cref="SweepScheduler"/> sign with (NL-296): the rate for the
/// confirmation target of the output's deadline (<c>SweepFeePolicy.GetConfirmationTarget</c>), falling back to the
/// node-wide rate when the fee service has none for that target.
/// </summary>
internal static class FeeEstimates
{
    /// <summary>
    /// The estimate in sat per 1000 weight units for <paramref name="confirmationTarget"/> blocks; 0 when the fee service
    /// fails (the policy then applies its floor).
    /// </summary>
    public static async Task<uint> GetForTargetAsync(IFeeService feeService, uint confirmationTarget, ILogger logger,
                                                     CancellationToken cancellationToken)
    {
        try
        {
            var estimate = await feeService.GetFeeRatePerKwAsync(confirmationTarget, cancellationToken);
            if (estimate is null || estimate.Satoshi <= 0)
                estimate = await feeService.GetFeeRatePerKwAsync(cancellationToken);

            return (uint)Math.Clamp(estimate?.Satoshi ?? 0, 0, uint.MaxValue);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("No fee estimate for {Target} blocks ({Reason}); using the floor", confirmationTarget,
                              e.Message);
            return 0;
        }
    }
}