using System.Diagnostics;
using System.Globalization;
using k8s.Models;

namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// The process that owns a run (plan R3): recorded on the run's namespace as annotations, so the reaper can tell a run
/// whose process is gone from one that is still working. <paramref name="ProcessStartUnixMs"/> guards against a reused
/// pid: a live process with the same pid but another start time is not the owner.
/// </summary>
/// <param name="Host">The owner's host name (<see cref="Environment.MachineName"/>; in a pod, the pod name).</param>
/// <param name="Pid">The owner's process id.</param>
/// <param name="ProcessStartUnixMs">When the owner process started, in Unix milliseconds, or null when unknown.</param>
public sealed record RunOwner(string Host, int Pid, long? ProcessStartUnixMs)
{
    /// <summary>The owner's host name.</summary>
    public const string HostAnnotation = "nltg.owner-host";

    /// <summary>The owner's process id.</summary>
    public const string PidAnnotation = "nltg.owner-pid";

    /// <summary>The owner process's start time, Unix milliseconds.</summary>
    public const string StartAnnotation = "nltg.owner-start";

    /// <summary>The owner of this process's runs.</summary>
    public static RunOwner Current()
    {
        using var process = Process.GetCurrentProcess();
        return new RunOwner(Environment.MachineName, process.Id, TryGetStartUnixMs(process));
    }

    /// <summary>The annotations that record this owner.</summary>
    public Dictionary<string, string> ToAnnotations()
    {
        var annotations = new Dictionary<string, string>
        {
            [HostAnnotation] = Host,
            [PidAnnotation] = Pid.ToString(CultureInfo.InvariantCulture)
        };
        if (ProcessStartUnixMs is { } start)
            annotations[StartAnnotation] = start.ToString(CultureInfo.InvariantCulture);

        return annotations;
    }

    /// <summary>
    /// The owner recorded on <paramref name="ns"/>, or null when it has no (valid) host and pid annotations.
    /// </summary>
    public static RunOwner? FromNamespace(V1Namespace ns)
    {
        ArgumentNullException.ThrowIfNull(ns);
        var annotations = ns.Metadata?.Annotations;
        if (annotations is null
         || !annotations.TryGetValue(HostAnnotation, out var host) || string.IsNullOrWhiteSpace(host)
         || !annotations.TryGetValue(PidAnnotation, out var pidText)
         || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            return null;

        long? start = annotations.TryGetValue(StartAnnotation, out var startText)
                   && long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                          ? ms
                          : null;
        return new RunOwner(host, pid, start);
    }

    /// <summary>The start time of <paramref name="process"/> in Unix milliseconds, or null when it cannot be read.</summary>
    internal static long? TryGetStartUnixMs(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public override string ToString() => $"{Host}:{Pid}";
}