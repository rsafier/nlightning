using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers;

using Constants;
using Encoding;
using Validators;

/// <summary>
/// A BOLT 12 <c>invoice_error</c> (onion message payload 68): <c>erroneous_field</c> (1, tu64),
/// <c>suggested_value</c> (3, bytes) and <c>error</c> (5, UTF-8).
/// </summary>
/// <remarks>
/// The constructor enforces the writer rules (an error text; a suggested value only with an erroneous field). The
/// spec's reader section is "FIXME!", so parsing is lenient about them and strict only about the format (BOLT 1 stream,
/// no unknown even type, value formats).
/// </remarks>
public sealed class InvoiceError
{
    /// <summary>
    /// <c>erroneous_field</c>: the offending TLV type of the invoice or invoice_request.
    /// </summary>
    public ulong? ErroneousField { get; }

    /// <summary>
    /// <c>suggested_value</c>: a valid value for <see cref="ErroneousField"/>.
    /// </summary>
    public ReadOnlyMemory<byte>? SuggestedValue { get; }

    /// <summary>
    /// <c>error</c>: an explanatory string.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// The TLV stream, exactly as received or built.
    /// </summary>
    public Bolt12TlvStream Stream { get; }

    /// <summary>
    /// A writer's invoice_error.
    /// </summary>
    /// <param name="error">The explanatory string (BOLT 12: MUST be set).</param>
    /// <param name="erroneousField">The offending field, or null.</param>
    /// <param name="suggestedValue">A valid value for that field, or null; only with
    /// <paramref name="erroneousField"/>.</param>
    /// <exception cref="ArgumentException">No error text, or a suggested value without an erroneous field.</exception>
    public InvoiceError(string error, ulong? erroneousField = null, ReadOnlyMemory<byte>? suggestedValue = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        if (suggestedValue is not null && erroneousField is null)
            throw new ArgumentException("A suggested_value needs an erroneous_field.", nameof(suggestedValue));

        Error = error;
        ErroneousField = erroneousField;
        SuggestedValue = suggestedValue;

        var builder = new Bolt12TlvStreamBuilder().SetUtf8(Bolt12TlvTypes.Error, error);
        if (erroneousField is { } field)
            builder.SetTu64(Bolt12TlvTypes.ErroneousField, field);
        if (suggestedValue is { } value)
            builder.Set(Bolt12TlvTypes.SuggestedValue, value.Span);

        Stream = builder.Build();
    }

    private InvoiceError(Bolt12TlvStream stream, ulong? erroneousField, ReadOnlyMemory<byte>? suggestedValue,
                         string? error)
    {
        Stream = stream;
        ErroneousField = erroneousField;
        SuggestedValue = suggestedValue;
        Error = error;
    }

    /// <summary>
    /// The TLV bytes (the onion message payload).
    /// </summary>
    public byte[] Encode() => Stream.Encode();

    public override string ToString() =>
        ErroneousField is { } field ? $"{Error} (field {field})" : Error ?? "(no error text)";

    /// <summary>
    /// Reads an invoice_error from its TLV bytes.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out InvoiceError? invoiceError,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        invoiceError = null;
        if (!Bolt12TlvStream.TryParse(bytes, out var stream, out var reason))
        {
            violation = new Bolt12Violation(Bolt12RequirementIds.TlvStream, reason);
            return false;
        }

        try
        {
            Bolt12TlvRanges.CheckTypes(stream, _ => true, Bolt12TlvRanges.InvoiceErrorTypes, "invoice_error");

            ulong? field = null;
            ReadOnlyMemory<byte>? suggested = null;
            string? error = null;
            foreach (var record in stream.Records)
            {
                switch (record.Type)
                {
                    case Bolt12TlvTypes.ErroneousField:
                        field = Bolt12FieldCodec.ReadTu64(record);
                        break;
                    case Bolt12TlvTypes.SuggestedValue:
                        suggested = record.Value.ToArray();
                        break;
                    case Bolt12TlvTypes.Error:
                        error = Bolt12FieldCodec.ReadUtf8(record);
                        break;
                }
            }

            invoiceError = new InvoiceError(stream, field, suggested, error);
            violation = null;
            return true;
        }
        catch (Bolt12FormatException e)
        {
            violation = e.Violation;
            return false;
        }
    }

    /// <summary>
    /// Reads an invoice_error from its TLV bytes.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a well-formed invoice_error.</exception>
    public static InvoiceError Parse(ReadOnlyMemory<byte> bytes) =>
        TryParse(bytes, out var invoiceError, out var violation)
            ? invoiceError
            : throw new FormatException(violation.ToString());
}