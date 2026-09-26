namespace NLightning.Infrastructure.Bitcoin.Services;

using Domain.Money;
using Domain.Protocol.Payloads;
using Protocol.Interfaces;
using Protocol.Validators;

public class InteractiveTransactionService : IInteractiveTransactionService
{
    private readonly LightningMoney _dustLimitAmount;
    private readonly bool _isInitiator;
    private readonly Dictionary<ulong, TxAddInputPayload> _inputs = [];
    private readonly Dictionary<ulong, TxAddOutputPayload> _outputs = [];

    // Every message handled here comes from the peer, so the sender is the initiator only when we are not.
    private bool IsPeerInitiator => !_isInitiator;

    /// <param name="dustLimitAmount">The dust limit applied to outputs added by the peer.</param>
    /// <param name="isInitiator">Whether the local node is the negotiation initiator.</param>
    public InteractiveTransactionService(LightningMoney dustLimitAmount, bool isInitiator)
    {
        _dustLimitAmount = dustLimitAmount;
        _isInitiator = isInitiator;
    }

    public async Task AddInputAsync(TxAddInputPayload input)
    {
        await TxAddInputValidator.ValidateAsync(IsPeerInitiator, input, _inputs.Count, IsValidPrevTx, IsUniqueInput, IsSerialIdUnique);
        _inputs.Add(input.SerialId, input);
    }

    public void AddOutput(TxAddOutputPayload output)
    {
        TxAddOutputValidator.Validate(IsPeerInitiator, output, _outputs.Count, IsSerialIdUnique, IsStandardScript,
                                      _dustLimitAmount);
        _outputs.Add(output.SerialId, output);
    }

    public void RemoveInput(TxRemoveInputPayload input)
    {
        TxRemoveInputValidator.Validate(IsPeerInitiator, input, IsInputSerialIdPresent);
        _inputs.Remove(input.SerialId);
    }

    public void RemoveOutput(TxRemoveOutputPayload output)
    {
        TxRemoveOutputValidator.Validate(IsPeerInitiator, output, IsOutputSerialIdPresent);
        _outputs.Remove(output.SerialId);
    }

    private Task<bool> IsValidPrevTx(byte[] prevTx)
    {
        // TODO: Implement logic to validate the prevTx by talking to bitcoind
        return Task.FromResult(true);
    }

    private bool IsUniqueInput(byte[] prevTx, uint prevTxVout)
    {
        return !_inputs.Values.Any(i => i.PrevTx.SequenceEqual(prevTx) && i.PrevTxVout == prevTxVout);
    }

    // serial_ids share one namespace across the inputs and outputs of the transaction
    private bool IsSerialIdUnique(ulong serialId)
    {
        return !_inputs.ContainsKey(serialId) && !_outputs.ContainsKey(serialId);
    }

    private bool IsInputSerialIdPresent(ulong serialId)
    {
        return _inputs.ContainsKey(serialId);
    }

    private bool IsOutputSerialIdPresent(ulong serialId)
    {
        return _outputs.ContainsKey(serialId);
    }

    private bool IsStandardScript(byte[] script)
    {
        // TODO: Check using NBitcoin
        return script.Length > 0;
    }
}