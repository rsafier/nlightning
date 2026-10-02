namespace NLightning.Daemon.Models;

internal sealed record PluginEntry
{
    public string AssemblyPath { get; set; } = "";
    public string? TypeName { get; set; }
    public string? ConfigSection { get; set; }
}