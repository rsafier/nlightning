using System.Diagnostics.CodeAnalysis;

namespace NLightning.Bolt11.Models;

using Domain.Protocol.ValueObjects;
using Domain.Utils;
using Enums;
using Factories;
using Interfaces;

/// <summary>
/// A list of tagged fields
/// </summary>
internal class TaggedFieldList : List<ITaggedField>
{
    private bool _shouldInvokeChangedEvent = true;
    public event EventHandler? Changed;

    /// <summary>
    /// Add a tagged field to the list
    /// </summary>
    /// <param name="taggedField">The tagged field to add</param>
    /// <exception cref="ArgumentException">If the tagged field is not unique</exception>
    internal new void Add(ITaggedField taggedField)
    {
        // Individual field validation
        if (!taggedField.IsValid())
            throw new ArgumentException($"Invalid {taggedField.Type} field: field validation failed");

        // Check for uniqueness (BOLT 11 allows repeated `f` and `r` fields)
        if (!IsRepeatable(taggedField.Type) && this.Any(x => x.Type.Equals(taggedField.Type)))
            throw new ArgumentException(
                $"TaggedFieldDictionary already contains a tagged field of type {taggedField.Type}");

        // Mutual exclusivity validation
        if (taggedField.Type == TaggedFieldTypes.Description
         && this.Any(x => x.Type.Equals(TaggedFieldTypes.DescriptionHash)))
            throw new ArgumentException(
                $"TaggedFieldDictionary already contains a tagged field of type {taggedField.Type}");

        if (taggedField.Type == TaggedFieldTypes.DescriptionHash
         && this.Any(x => x.Type.Equals(TaggedFieldTypes.Description)))
            throw new ArgumentException(
                $"TaggedFieldDictionary already contains a tagged field of type {taggedField.Type}");

        // Add the tagged field
        base.Add(taggedField);
        if (_shouldInvokeChangedEvent)
            OnChanged();
    }

    /// <summary>
    /// Add a range of tagged fields to the list
    /// </summary>
    /// <param name="taggedFields">The tagged fields to add</param>
    internal new void AddRange(IEnumerable<ITaggedField> taggedFields)
    {
        _shouldInvokeChangedEvent = false;

        foreach (var taggedField in taggedFields)
            Add(taggedField);

        _shouldInvokeChangedEvent = true;
        OnChanged();
    }

    /// <summary>
    /// Replace every tagged field of a type with the given fields, raising <see cref="Changed"/> once
    /// </summary>
    /// <param name="taggedFieldType">The type of the tagged fields to replace</param>
    /// <param name="taggedFields">The new fields; all must be of <paramref name="taggedFieldType"/></param>
    /// <exception cref="ArgumentException">
    /// If a field has another type or is invalid, if several fields are given for a type that may not repeat, or
    /// if the result would hold both a description and a description hash
    /// </exception>
    internal void Replace(TaggedFieldTypes taggedFieldType, params IEnumerable<ITaggedField> taggedFields)
    {
        var newFields = taggedFields.ToList();
        foreach (var taggedField in newFields)
        {
            if (taggedField.Type != taggedFieldType)
                throw new ArgumentException(
                    $"Cannot replace {taggedFieldType} fields with a field of type {taggedField.Type}");

            if (!taggedField.IsValid())
                throw new ArgumentException($"Invalid {taggedField.Type} field: field validation failed");
        }

        if (newFields.Count > 1 && !IsRepeatable(taggedFieldType))
            throw new ArgumentException($"Only one tagged field of type {taggedFieldType} is allowed");

        if (newFields.Count > 0
         && ((taggedFieldType == TaggedFieldTypes.Description
           && this.Any(x => x.Type.Equals(TaggedFieldTypes.DescriptionHash)))
          || (taggedFieldType == TaggedFieldTypes.DescriptionHash
           && this.Any(x => x.Type.Equals(TaggedFieldTypes.Description)))))
            throw new ArgumentException(
                $"TaggedFieldDictionary already contains a tagged field that excludes {taggedFieldType}");

        base.RemoveAll(x => x.Type.Equals(taggedFieldType));
        base.AddRange(newFields);
        OnChanged();
    }

    internal new bool Remove(ITaggedField item)
    {
        if (!base.Remove(item))
            return false;

        OnChanged();
        return true;
    }

    internal new void RemoveAt(int index)
    {
        base.RemoveAt(index);
        OnChanged();
    }

    internal new int RemoveAll(Predicate<ITaggedField> match)
    {
        var removed = base.RemoveAll(match);
        if (removed > 0)
            OnChanged();

        return removed;
    }

    internal new void RemoveRange(int index, int count)
    {
        base.RemoveRange(index, count);
        OnChanged();
    }

    /// <summary>
    /// Attempts to retrieve a tagged field of the specified type from the list.
    /// </summary>
    /// <typeparam name="T">The type of the tagged field to retrieve.</typeparam>
    /// <param name="taggedFieldType">The type of the tagged field being searched for.</param>
    /// <param name="taggedField">The output parameter to hold the retrieved tagged field if found; otherwise, the default value for the type.</param>
    /// <returns>True if the tagged field of the specified type is found; otherwise, false.</returns>
    internal bool TryGet<T>(TaggedFieldTypes taggedFieldType, [MaybeNullWhen(false)] out T taggedField)
        where T : ITaggedField
    {
        var value = Get<T>(taggedFieldType);
        if (value != null)
        {
            taggedField = value;
            return true;
        }

        taggedField = default;
        return false;
    }

    /// <summary>
    /// Try to get all tagged fields of a specific type
    /// </summary>
    /// <param name="taggedFieldType">The type of the tagged field</param>
    /// <param name="taggedFieldList">A list containing the tagged fields</param>
    /// <typeparam name="T">The type of the tagged field</typeparam>
    /// <returns>True if the tagged fields were found, false otherwise</returns>
    internal bool TryGetAll<T>(TaggedFieldTypes taggedFieldType, [MaybeNullWhen(false)] out List<T> taggedFieldList)
        where T : ITaggedField
    {
        var value = GetAll<T>(taggedFieldType);
        if (value != null)
        {
            taggedFieldList = value;
            return true;
        }

        taggedFieldList = null;
        return false;
    }

    /// <summary>
    /// Get a new TaggedFieldList from a BitReader
    /// </summary>
    /// <param name="bitReader">The BitReader to read from</param>
    /// <param name="bitcoinNetwork">The network type</param>
    /// <param name="availableBits">
    /// The exact number of tagged-field bits in the invoice data (multiple of 5). When <c>null</c>, fields are read
    /// until fewer than 15 bits remain in the reader.
    /// </param>
    /// <returns>A new TaggedFieldList</returns>
    /// <exception cref="ArgumentException">
    /// If a field is truncated, a known field is malformed (BOLT 11: e.g. wrong <c>p</c>/<c>h</c>/<c>s</c>/<c>n</c>
    /// length), both <c>d</c> and <c>h</c> are present, or there are dangling bits after the last field.
    /// </exception>
    /// <remarks>
    /// Unknown field types and <c>f</c> fields with an unknown version are skipped, as BOLT 11 requires.
    /// When a non-repeatable field appears more than once, the first one is kept (BOLT 11: use the first).
    /// </remarks>
    internal static TaggedFieldList FromBitReader(BitReader bitReader, BitcoinNetwork bitcoinNetwork,
                                                  int? availableBits = null)
    {
        var taggedFields = new TaggedFieldList();
        var remainingBits = availableBits ?? int.MaxValue;
        while (remainingBits >= 15 && bitReader.HasMoreBits(15))
        {
            var type = (TaggedFieldTypes)bitReader.ReadByteFromBits(5);
            var length = bitReader.ReadInt16FromBits(10);
            remainingBits -= 15;

            var fieldBits = length * 5;
            if (fieldBits > remainingBits || !bitReader.HasMoreBits(fieldBits))
                throw new ArgumentException(
                    $"Tagged field {type} declares data_length {length}, which is longer than the remaining data");

            remainingBits -= fieldBits;

            // Copy exactly this field's bits, so a field parser can never desync the outer reader
            var fieldData = new byte[(fieldBits + 7) / 8];
            bitReader.ReadBits(fieldData, fieldBits);
            if (fieldBits % 8 != 0)
                fieldData[^1] &= (byte)(0xFF << (8 - fieldBits % 8));

            // BOLT 11: skip unknown fields
            if (!Enum.IsDefined(type))
                continue;

            var taggedField = TaggedFieldFactory.CreateTaggedFieldFromBitReader(type, new BitReader(fieldData),
                                                                               length, bitcoinNetwork);

            // e.g. an `f` field with an unknown version, which BOLT 11 says to skip
            if (taggedField is null)
                continue;

            // Keep the first (most preferred) field of a type that may not repeat
            if (!IsRepeatable(type) && taggedFields.Any(x => x.Type.Equals(type)))
                continue;

            // Throws if the field is invalid or if `d` and `h` are both present
            taggedFields.Add(taggedField);
        }

        if (availableBits.HasValue && remainingBits > 0)
            throw new ArgumentException($"{remainingBits} dangling bits after the last tagged field");

        return taggedFields;
    }

    /// <summary>
    /// Write the TaggedFieldList to a BitWriter
    /// </summary>
    /// <param name="bitWriter">The BitWriter to write to</param>
    internal void WriteToBitWriter(BitWriter bitWriter)
    {
        foreach (var taggedField in this)
        {
            // Write type
            bitWriter.WriteByteAsBits((byte)taggedField.Type, 5);

            // Write length
            bitWriter.WriteInt16AsBits(taggedField.Length, 10);

            taggedField.WriteToBitWriter(bitWriter);
        }
    }

    /// <summary>
    /// Calculate the size of the TaggedFieldList in bits
    /// </summary>
    /// <returns>The size of the TaggedFieldList in bits</returns>
    internal int CalculateSizeInBits()
    {
        return this.Sum(x => x.Length);
    }

    /// <summary>
    /// Get a tagged field of a specific type
    /// </summary>
    /// <param name="taggedFieldType">The type of the tagged field</param>
    /// <typeparam name="T">The type of the tagged field</typeparam>
    /// <returns>The tagged field</returns>
    private T? Get<T>(TaggedFieldTypes taggedFieldType) where T : ITaggedField
    {
        return (T?)this.FirstOrDefault(x => x.Type.Equals(taggedFieldType));
    }

    /// <summary>
    /// Get all tagged fields of a specific type
    /// </summary>
    /// <param name="taggedFieldType">The type of the tagged field</param>
    /// <typeparam name="T">The type of the tagged field</typeparam>
    /// <returns>A list containing the tagged fields</returns>
    private List<T>? GetAll<T>(TaggedFieldTypes taggedFieldType) where T : ITaggedField
    {
        var taggedFields = this.Where(x => x.Type.Equals(taggedFieldType)).ToList();
        return taggedFields.Count == 0
                   ? null
                   : taggedFields.Cast<T>().ToList();
    }

    /// <summary>
    /// Whether BOLT 11 allows more than one field of this type in an invoice
    /// </summary>
    internal static bool IsRepeatable(TaggedFieldTypes taggedFieldType)
    {
        return taggedFieldType is TaggedFieldTypes.FallbackAddress or TaggedFieldTypes.RoutingInfo;
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}