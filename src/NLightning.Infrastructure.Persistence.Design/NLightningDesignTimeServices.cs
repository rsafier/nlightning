using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Persistence.Design;

/// <summary>
/// Design-time services of the compiled-model generator (NL-708), found by <c>dotnet ef</c> in its startup assembly.
/// </summary>
public class NLightningDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection serviceCollection)
        => serviceCollection.AddSingleton<ICSharpHelper, ValueObjectCSharpHelper>();
}

/// <summary>
/// The compiled-model generator writes a property's sentinel as its provider value: <c>converter.ConvertToProvider
/// (sentinel) ?? sentinel</c>. A non-nullable byte-backed value object (<c>Hash</c>, <c>ChannelId</c>, ...) has
/// <c>default</c> as its sentinel, which our converters turn into a null <c>byte[]</c>, so the generator falls back to
/// the value object itself and cannot write it as a literal. Its provider value is null, so it is written as
/// <c>null</c>: the runtime property then has no sentinel, as a property whose default converts to null. No key or
/// generated column of ours relies on the sentinel of such a property (every value is set before a save).
/// </summary>
public class ValueObjectCSharpHelper(ITypeMappingSource typeMappingSource) : CSharpHelper(typeMappingSource)
{
    public override string UnknownLiteral(object? value)
    {
        if (value is not null && IsDefaultDomainValueObject(value))
            return "null";

        return base.UnknownLiteral(value);
    }

    private static bool IsDefaultDomainValueObject(object value)
    {
        var type = value.GetType();
        return type.IsValueType
            && type.Namespace?.StartsWith("NLightning.Domain", StringComparison.Ordinal) == true
            && value.Equals(Activator.CreateInstance(type));
    }
}