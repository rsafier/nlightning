namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Money;

/// <summary>
/// What a wallet spend would cost now (<c>IWalletSpendService.EstimateWithdrawFeeAsync</c>, NL-997): the inputs the
/// selector would take (largest confirmed outputs first) and a change output, at the fee rate.
/// </summary>
/// <param name="Fee">The fee of the estimated transaction.</param>
/// <param name="FeeRatePerKw">The fee rate it was estimated at, in sat per 1000 weight units.</param>
/// <param name="Weight">The estimated weight.</param>
/// <param name="InputCount">How many wallet outputs it would spend.</param>
public sealed record WalletWithdrawEstimate(LightningMoney Fee, LightningMoney FeeRatePerKw, int Weight,
                                            int InputCount);