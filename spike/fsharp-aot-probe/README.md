# F# NativeAOT probe (F# evaluation spike, not product code)

A C# console app that calls the F# `ReestablishPlanner` (`src/NLightning.Domain.Rules`), consumes an F# union from C#
(`PlanConsumer.cs`) and uses a few FSharp.Core features, published with NativeAOT. It needs an SDK whose ILCompiler
links on this machine (NL-750: SDK 11 rc.1 on macOS):

    /usr/local/share/dotnet/dotnet publish spike/fsharp-aot-probe/Probe -c Release -r osx-arm64 -f net10.0 -o /tmp/probe-out -p:TrimmerSingleWarn=false
    /tmp/probe-out/Probe

Result on 2026-10-04: the planner, structural equality, Set/Map, DU pattern matching from C# and `$"..."` strings
work. `sprintf "%d"` throws NotSupportedException (MakeGenericMethod) at run time. FSharp.Core reports 47 trim/AOT
warnings, collapsed into one IL2104/IL3053 pair unless you pass TrimmerSingleWarn=false. The C# switch over the F#
union needs a discard arm (CS8509): the C# compiler does not treat it as closed.
