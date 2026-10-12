using System.Text.Json.Serialization;

namespace NLightning.Infrastructure.Node.Models;

/// <summary>
/// The source-generated JSON contract of <see cref="KeyFileData"/>. NativeAOT builds turn reflection-based
/// System.Text.Json off, so the key file is read and written through this context (NL-338); the default options keep
/// the file format byte-identical to the reflection serializer's.
/// </summary>
[JsonSerializable(typeof(KeyFileData))]
public partial class KeyFileDataJsonContext : JsonSerializerContext;