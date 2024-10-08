namespace NLightning.Bolts.Exceptions;

/// <summary>
/// Represents an exception that is thrown when an error occurs during invoice serialization or deserialization.
/// </summary>
public class InvoiceSerializationException : ErrorException
{
    public InvoiceSerializationException(string message) : base(message)
    { }
    public InvoiceSerializationException(string message, Exception innerException) : base(message, innerException)
    { }
}