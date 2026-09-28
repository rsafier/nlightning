using System.Text.Json;

namespace NLightning.Bolt11.Tests.Models;

using Bolt11.Models;
using Exceptions;

/// <summary>
/// The machine-readable BOLT 11 vectors of bolts#1357 (<c>Vectors/invoice-test.json</c>, see its README): every
/// valid entry decodes and every invalid one fails.
/// </summary>
public class InvoiceSpecVectorTests
{
    private const string VectorFile = "Vectors/invoice-test.json";
    private const string TwoDistinctPFields = "Two distinct p fields in an otherwise valid invoice";
    private const string SamePFieldTwice = "The same p field twice in an otherwise valid invoice";

    private static readonly Lazy<IReadOnlyList<SpecVector>> s_vectors = new(LoadVectors);

    public static TheoryData<string> ValidDescriptions => ToTheoryData(true);

    public static TheoryData<string> InvalidDescriptions => ToTheoryData(false);

    [Fact]
    public void Given_TheVectorFile_When_Loaded_Then_ItHoldsBothKindsAndTheDuplicatePCases()
    {
        // Arrange & Act
        var vectors = s_vectors.Value;

        // Assert
        Assert.Equal(28, vectors.Count);
        Assert.Equal(15, vectors.Count(v => v.Valid));
        Assert.Contains(vectors, v => v.Description == TwoDistinctPFields && !v.Valid);
        Assert.Contains(vectors, v => v.Description == SamePFieldTwice && !v.Valid);
        Assert.Equal(vectors.Count, vectors.Select(v => v.Description).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(ValidDescriptions))]
    public void Given_AValidSpecVector_When_Decoded_Then_ItDecodesWithTheRequiredFields(string description)
    {
        // Arrange
        var vector = GetVector(description);

        // Act
        var invoice = Invoice.Decode(vector.Invoice);

        // Assert
        Assert.NotNull(invoice.PaymentHash);
        Assert.NotNull(invoice.PaymentSecret);
        Assert.NotNull(invoice.PayeePubKey);
        Assert.True(invoice.Description is not null || invoice.DescriptionHash is not null);
    }

    [Theory]
    [MemberData(nameof(InvalidDescriptions))]
    public void Given_AnInvalidSpecVector_When_Decoded_Then_ThrowsInvoiceSerializationException(string description)
    {
        // Arrange
        var vector = GetVector(description);

        // Act & Assert
        Assert.Throws<InvoiceSerializationException>(() => Invoice.Decode(vector.Invoice));
    }

    [Fact]
    public void Given_TwoDistinctPFields_When_Decoded_Then_ThrowsOnTheDuplicatePaymentHash()
    {
        // Arrange
        // bolts#1357: MUST fail the payment if more than one `p` field is present
        var vector = GetVector(TwoDistinctPFields);

        // Act
        var exception = Assert.Throws<InvoiceSerializationException>(() => Invoice.Decode(vector.Invoice));

        // Assert
        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Contains("more than one payment hash", exception.InnerException.Message);
    }

    [Fact]
    public void Given_TheSamePFieldTwice_When_Decoded_Then_ThrowsOnTheDuplicatePaymentHash()
    {
        // Arrange
        // bolts#1357: an identical second `p` fails too
        var vector = GetVector(SamePFieldTwice);

        // Act
        var exception = Assert.Throws<InvoiceSerializationException>(() => Invoice.Decode(vector.Invoice));

        // Assert
        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Contains("more than one payment hash", exception.InnerException.Message);
    }

    private static SpecVector GetVector(string description) =>
        s_vectors.Value.Single(v => v.Description == description);

    private static TheoryData<string> ToTheoryData(bool valid)
    {
        var data = new TheoryData<string>();
        foreach (var vector in s_vectors.Value.Where(v => v.Valid == valid))
            data.Add(vector.Description);

        return data;
    }

    private static IReadOnlyList<SpecVector> LoadVectors()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, VectorFile));
        return JsonSerializer.Deserialize<List<SpecVector>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"{VectorFile} is empty");
    }

    private sealed record SpecVector(string Description, bool Valid, string Invoice);
}