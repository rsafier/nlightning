namespace NLightning.Application.Tests.TestCollections;

using Xunit;

/// <summary>
/// The timing-sensitive classes that stayed racy under a loaded full run even after the de-timing pass (NL-512's
/// signature flush, NL-565's FIFO interleave): they run serialized against each other, so their load windows do not
/// compound. They still share the assembly's pool with the other classes — a failure here is a lead, not a verdict.
/// </summary>
[CollectionDefinition("timing-serial", DisableParallelization = true)]
public sealed class TimingSerialCollection;