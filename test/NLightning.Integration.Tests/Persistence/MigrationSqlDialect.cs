using System.Text;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Enums;

/// <summary>Table/column naming and literals of each provider (Postgres uses snake_case); names are quoted so a
/// keyword column such as <c>Index</c> works everywhere.</summary>
internal sealed class MigrationSqlDialect(DatabaseType databaseType)
{
    public string Bool(bool value) =>
        databaseType == DatabaseType.PostgreSql ? (value ? "TRUE" : "FALSE") : (value ? "1" : "0");

    /// <summary>A <c>decimal</c> property's value: EF stores those as TEXT on SQLite.</summary>
    public string Decimal(ulong value) => databaseType == DatabaseType.Sqlite ? $"'{value}'" : $"{value}";

    public string Insert(string table, params (string Column, string Value)[] values) =>
        $"INSERT INTO {Name(table)} ({string.Join(", ", values.Select(v => Name(v.Column)))}) " +
        $"VALUES ({string.Join(", ", values.Select(v => v.Value))})";

    private string Name(string pascalCase) => databaseType switch
    {
        DatabaseType.PostgreSql => $"\"{SnakeCase(pascalCase)}\"",
        DatabaseType.MicrosoftSql => $"[{pascalCase}]",
        _ => $"\"{pascalCase}\""
    };

    /// <summary>EFCore.NamingConventions: an underscore before an upper-case letter that follows a lower-case
    /// one (so <c>Sha256OfOnion</c> becomes <c>sha256of_onion</c>).</summary>
    private static string SnakeCase(string pascalCase)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c) && i > 0 && char.IsLower(pascalCase[i - 1]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}