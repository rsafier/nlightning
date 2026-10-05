// C# 15 union support types for net10.0 (NL-1086, docs/agents/CSHARP15_PILOT.md). The compiler binds these by name;
// net11.0 has them in System.Runtime, so src/Directory.Build.props compiles this file for net10.0 only, into the
// projects that set NltgUnionPolyfill=true. Remove it once net10.0 is no longer a target.
namespace System.Runtime.CompilerServices;

/// <summary>Marks a type as a C# union (polyfill of the .NET 11 type).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
internal sealed class UnionAttribute : Attribute;

/// <summary>The value a C# union holds (polyfill of the .NET 11 type).</summary>
internal interface IUnion
{
    /// <summary>The case value, or null for a default union.</summary>
    object? Value { get; }
}