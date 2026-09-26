namespace NLightning.Domain.Bitcoin.Interfaces;

using Money;

public interface IFeeService
{
    /// <summary>
    /// Gets the current fee rate in satoshis per kiloweight (sat/kW)
    /// </summary>
    Task<LightningMoney> GetFeeRatePerKwAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the fee rate in sat/kw to confirm within <paramref name="confirmationTarget"/> blocks (BOLT 5 plan OG12,
    /// O6-T1, NL-296): the deadline-driven sweeps, claims and penalties ask for the target their deadline leaves.
    /// </summary>
    /// <remarks>
    /// A source without per-target estimates answers the node-wide rate (<see cref="GetFeeRatePerKwAsync(CancellationToken)"/>),
    /// which is also this default implementation. Never 0.
    /// </remarks>
    /// <param name="confirmationTarget">The number of blocks to confirm within (at least 1).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<LightningMoney> GetFeeRatePerKwAsync(uint confirmationTarget, CancellationToken cancellationToken = default) =>
        GetFeeRatePerKwAsync(cancellationToken);

    /// <summary>
    /// Gets the current cached fee rate without checking API
    /// </summary>
    LightningMoney GetCachedFeeRatePerKw();

    /// <summary>
    /// Forces a refresh of the fee rate from the API
    /// </summary>
    Task RefreshFeeRateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a background task to periodically refresh the fee rate
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the background task and cancels any ongoing operations within the service.
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    Task StopAsync();
}