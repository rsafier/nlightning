using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Reestablish;
using FsProbe;

var local = new ReestablishLocalState(5, 4, false, false, LastSentCommitmentMessage.RevokeAndAck, true);
var own = ReestablishPlanner.CreateOwn(local);
var plan = ReestablishPlanner.Plan(local, new PeerReestablish(5, 4, new byte[32]), (_, _) => true);
Console.WriteLine($"own {own.NextCommitmentNumber} plan {plan.Outcome} [{string.Join(",", plan.Steps)}]");
ReestablishFundingField? next = ReestablishPlanner.GetOwnNextFunding(local); // F# `| null` seen as nullable
Console.WriteLine($"next null: {next is null}");
Console.WriteLine($"structural: {Probe.structural()}");
try { Console.WriteLine(Probe.formatSimple(3)); } catch (Exception e) { Console.WriteLine($"sprintf failed: {e.GetType().Name}"); }
Console.WriteLine(Probe.interp(4));
var step = Step.NewC("n", 1);
Console.WriteLine(step switch { { IsA: true } => "A", Step.B b => $"B {b.Item}", Step.C c => $"C {c.name} {c.flags}", _ => "?" });
try { Console.WriteLine(Probe.formatAny(new Rec(1, Microsoft.FSharp.Collections.FSharpList<Step>.Cons(Step.A, Microsoft.FSharp.Collections.FSharpList<Step>.Empty)))); }
catch (Exception e) { Console.WriteLine($"%A failed: {e.GetType().Name}: {e.Message}"); }
static class NullCheck { static void M(ReestablishLocalState l) { ReestablishFundingField nn = ReestablishPlanner.GetOwnNextFunding(l); OwnReestablish o = ReestablishPlanner.CreateOwn(l); System.Console.WriteLine(nn.TxId); System.Console.WriteLine(o); } }
