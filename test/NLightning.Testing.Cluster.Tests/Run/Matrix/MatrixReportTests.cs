using System.IO.Compression;
using System.Xml.Linq;

namespace NLightning.Testing.Cluster.Tests.Run.Matrix;

using Cluster.Run;
using Cluster.Run.Matrix;

public sealed class MatrixReportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nltg-matrix").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Given_AResultFile_When_Read_Then_CountsFailedClassesAndTheFirstErrorComeBack()
    {
        // Arrange
        var path = WriteResults(Path.Combine(_root, "r.xml"), ("A", "One", false), ("B", "Two", true),
                                ("B", "Three", true), ("C", "Four", true));

        // Act
        var result = XunitResults.Read(path);

        // Assert
        Assert.True(result.Found);
        Assert.Equal((4, 1, 3, 0), (result.Total, result.Passed, result.Failed, result.Errors));
        Assert.Equal(["Ns.B", "Ns.C"], result.FailedClasses);
        Assert.Equal("Two: Two broke", result.FirstError);
        Assert.False(result.IsGreen);
    }

    [Fact]
    public void Given_NoOrABrokenResultFile_When_Read_Then_ItIsMissing()
    {
        // Arrange
        var broken = Path.Combine(_root, "broken.xml");
        File.WriteAllText(broken, "<assemblies><assembly total=");

        // Act & Assert
        Assert.False(XunitResults.Read(Path.Combine(_root, "none.xml")).Found);
        Assert.False(XunitResults.Read(broken).Found);
        Assert.False(XunitResults.Parse(XDocument.Parse("<assemblies/>")).Found);
    }

    [Fact]
    public void Given_AnErrorOutsideTests_When_Read_Then_ItIsTheFirstErrorAndNotGreen()
    {
        // Arrange
        var document = XDocument.Parse(
            """
            <assemblies><assembly total="2" passed="2" failed="0" skipped="0" errors="1">
              <errors><error type="fixture-cleanup" name="ClnFixture"><failure><message>pod stuck
            more</message></failure></error></errors>
            </assembly></assemblies>
            """);

        // Act
        var result = XunitResults.Parse(document);

        // Assert
        Assert.False(result.IsGreen);
        Assert.Equal("fixture-cleanup ClnFixture: pod stuck", result.FirstError);
        Assert.Empty(result.FailedClasses);
    }

    [Fact]
    public void Given_EveryTestSkipped_When_ReadAndJudged_Then_TheSuiteIsNotGreen()
    {
        // Arrange: an LND suite whose fixture found no cluster and skipped every test (NL-860)
        var document = XDocument.Parse(
            """
            <assemblies><assembly total="12" passed="0" failed="0" skipped="10" not-run="2" errors="0">
            </assembly></assemblies>
            """);
        var dir = Attempt("skipped", "0 3 1", tests: []);
        document.Save(Path.Combine(dir, SuiteAttempt.ResultsFile));

        // Act
        var result = XunitResults.Parse(document);
        var attempt = SuiteAttempt.Read(dir);

        // Assert
        Assert.Equal((12, 0, 10), (result.Total, result.Passed, result.Skipped));
        Assert.False(result.IsGreen);
        Assert.False(attempt.IsGreen);
        Assert.False(SuiteAttempt.IsGreenIn(dir));
        Assert.Equal(SuiteOutcome.Failed, MatrixReport.Judge(null, attempt, [], 3));
    }

    [Theory]
    [InlineData("Collection fixture type 'NLightning.Integration.Tests.Fixtures.PostgresFixture' threw in InitializeAsync", true)]
    [InlineData("Class fixture type 'Ns.F' threw in its constructor\n---- System.TimeoutException : no slot", true)]
    [InlineData("Assembly fixture type 'Ns.F' had one or more unresolved constructor arguments: x", true)]
    [InlineData("The following constructor parameters did not have matching fixture data: Ns.F fixture", true)]
    [InlineData("Assert.Equal() Failure: fixture type mismatch", false)]
    [InlineData(null, false)]
    public void Given_AFailureMessage_When_Checked_Then_OnlyXunitsFixtureFailuresCount(string? message, bool fixture)
    {
        // Act & Assert
        Assert.Equal(fixture, XunitResults.IsFixtureFailure(message));
    }

    [Fact]
    public void Given_AClassWhoseFixtureThrew_When_AskedWhatToRerun_Then_NothingIsAndTheSummarySaysWhy()
    {
        // Arrange: xunit v3 fails every test of the class with the fixture's message, errors stay 0
        var dir = Path.Combine(_root, "fixture-batch");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, MatrixReport.PlanFile),
                           MatrixPlanner.Plan(["ldk"], 6, false).Select(p => p.ToLine()));
        Attempt("fixture-batch/ldk", "1 10 1", tests: [("A", "One", false)]);
        new XDocument(new XElement("assemblies", new XElement(
                          "assembly", new XAttribute("total", 2), new XAttribute("passed", 0),
                          new XAttribute("failed", 2), new XAttribute("skipped", 0), new XAttribute("errors", 0),
                          new XElement("collection",
                                       new[] { "One", "Two" }.Select(m => new XElement(
                                                                       "test", new XAttribute("name", $"Ns.A.{m}"),
                                                                       new XAttribute("type", "Ns.A"),
                                                                       new XAttribute("method", m),
                                                                       new XAttribute("result", "Fail"),
                                                                       new XElement("failure", new XElement(
                                                                           "message",
                                                                           "Class fixture type 'Ns.F' threw in "
                                                                         + "InitializeAsync\nboom"))))))))
            .Save(Path.Combine(dir, "ldk", SuiteAttempt.ResultsFile));

        // Act
        var attempt = SuiteAttempt.Read(Path.Combine(dir, "ldk"));
        var reports = MatrixReport.Read(dir, 3);

        // Assert: one class, under the rerun limit, yet no rerun: a fixture that threw is not a test flake
        Assert.Equal(2, attempt.Results.FixtureFailures);
        Assert.Empty(MatrixReport.ClassesToRerun(attempt, 3));
        Assert.Equal(SuiteOutcome.Failed, reports.Single().Outcome);
        Assert.Contains("ldk: 2 test(s) failed in a fixture (no rerun)", MatrixReport.Format(reports, _root, null, null));
    }

    [Fact]
    public void Given_SkippedSuites_When_TheExitCodeIsComputed_Then_NothingRunOrANamedSkipIsNotGreen()
    {
        // Arrange
        var batch = Path.Combine(_root, "skips");
        Directory.CreateDirectory(batch);
        File.WriteAllLines(Path.Combine(batch, MatrixReport.PlanFile),
                           MatrixPlanner.Plan(["ldk", "tor"], 6, false).Select(p => p.ToLine()));
        Attempt("skips/ldk", "0 5 1", tests: [("A", "One", false)]);
        var reports = MatrixReport.Read(batch, 3);
        var torOnly = reports.Where(r => r.Name == "tor").ToList();

        // Act & Assert: the default matrix lists its skips (0); a named skip or nothing run is 3; failures stay 1
        Assert.Equal(0, MatrixReport.ExitCode(reports));
        Assert.Equal(MatrixReport.NothingRanExitCode, MatrixReport.ExitCode(reports, suitesNamed: true));
        Assert.Equal(MatrixReport.NothingRanExitCode, MatrixReport.ExitCode(torOnly));
        Assert.Equal(MatrixReport.NothingRanExitCode, MatrixReport.ExitCode([]));
    }

    [Fact]
    public async Task Given_AttemptsUnderABatch_When_GreenAttemptsAreListed_Then_OnlyTheGreenOnesComeBack()
    {
        // Arrange: green, exit 0 without tests, exit 0 with a failure, a timeout, a crash, and a green rerun
        var green = Attempt("g/ldk", "0 5 1", tests: [("A", "One", false)]);
        Attempt("g/cln", "0 5 1", tests: []);
        Attempt("g/eclair", "0 5 1", tests: [("A", "One", true)]);
        var hung = Attempt("g/postgres", "0 5 1", tests: [("A", "One", false)]);
        File.WriteAllText(Path.Combine(hung, SuiteAttempt.TimedOutFile), "");
        Attempt("g/faults", "3 5 1", tests: null);
        var rerun = Attempt("g/eclair/rerun-1", "0 5 1", tests: [("A", "One", false)], rerunClass: "Ns.A");
        var output = new StringWriter();

        // Act
        var code = await MatrixCli.RunAsync(["green-attempts", Path.Combine(_root, "g")], output, TextWriter.Null);

        // Assert
        Assert.Equal(ClusterCli.Ok, code);
        Assert.Equal([rerun, green], output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.False(SuiteAttempt.IsGreenIn(Path.Combine(_root, "g", "missing")));
    }

    [Fact]
    public void Given_AnOldResultWithoutType_When_Read_Then_TheClassComesFromTheName()
    {
        // Arrange
        var document = XDocument.Parse(
            """
            <assemblies><assembly total="2" passed="0" failed="2">
              <test name="Ns.X.M1" method="M1" result="Fail"/>
              <test name="Ns.Y.M2(a: 1)" result="Fail"/>
            </assembly></assemblies>
            """);

        // Act & Assert
        Assert.Equal(["Ns.X", "Ns.Y"], XunitResults.Parse(document).FailedClasses);
    }

    [Fact]
    public void Given_AnAttemptFolder_When_Read_Then_ExitNamespacesFixturesAndDumpsComeBack()
    {
        // Arrange
        var dir = Attempt("cln", exit: "0 42 1700000000", log:
                          """
                          [nltg-cluster] run b-cln: namespace nltg-spike-b-cln created
                          [fixture] CLN fixture (Cluster) ready in 6.3 s
                          [nltg-cluster] run b-cln-2: namespace nltg-spike-b-cln-2 created
                          [fixture] Postgres fixture postgres (Cluster) ready in 12 s
                          """, tests: [("A", "One", false)]);
        Directory.CreateDirectory(Path.Combine(dir, "diag", "Test", "nltg-spike-b-cln"));
        File.WriteAllText(Path.Combine(dir, "diag", "Test", "nltg-spike-b-cln", "summary.txt"), "x");

        // Act
        var attempt = SuiteAttempt.Read(dir);

        // Assert
        Assert.Equal((0, 42, 1700000000L), (attempt.ExitCode, attempt.WallSeconds, attempt.StartEpoch));
        Assert.Equal("b-cln", attempt.RunId);
        Assert.False(attempt.TimedOut);
        Assert.Equal(["nltg-spike-b-cln", "nltg-spike-b-cln-2"], attempt.Namespaces);
        Assert.Equal([new FixtureReady("CLN fixture (Cluster)", 6.3), new FixtureReady("Postgres fixture postgres (Cluster)", 12)],
                     attempt.FixturesReady);
        Assert.Single(attempt.DiagDumps);
        Assert.True(attempt.IsGreen);
    }

    [Fact]
    public void Given_AGzippedLog_When_TheAttemptIsRead_Then_ItsLinesAreScannedToo()
    {
        // Arrange
        var dir = Attempt("gz", exit: "0 1 1", tests: [("A", "One", false)]);
        using (var gz = new GZipStream(File.Create(Path.Combine(dir, SuiteAttempt.LogFile + ".gz")),
                                       CompressionMode.Compress))
        using (var writer = new StreamWriter(gz))
            writer.Write("x\n[nltg-cluster] run b-gz: namespace nltg-spike-b-gz created\n[fixture] F ready in 2.5 s\n");

        // Act
        var attempt = SuiteAttempt.Read(dir);

        // Assert
        Assert.Equal(["nltg-spike-b-gz"], attempt.Namespaces);
        Assert.Equal([new FixtureReady("F", 2.5)], attempt.FixturesReady);
        Assert.Empty(SuiteAttempt.LogLines(Path.Combine(_root, "none.log")));
    }

    [Fact]
    public void Given_FewFailedClasses_When_AskedWhatToRerun_Then_EachIsRerunAlone()
    {
        // Arrange
        var attempt = SuiteAttempt.Read(Attempt("a", "1 10 1", tests: [("A", "One", true), ("B", "Two", true)]));

        // Act & Assert
        Assert.Equal(["Ns.A", "Ns.B"], MatrixReport.ClassesToRerun(attempt, 3));
        Assert.Empty(MatrixReport.ClassesToRerun(attempt, 1));
        Assert.Empty(MatrixReport.ClassesToRerun(attempt, 0));
    }

    [Fact]
    public void Given_AHangACrashOrAnUnfinishedRun_When_AskedWhatToRerun_Then_NothingIs()
    {
        // Arrange
        var hung = Attempt("hung", "143 600 1", tests: [("A", "One", true)]);
        File.WriteAllText(Path.Combine(hung, SuiteAttempt.TimedOutFile), "");
        var crashed = Attempt("crashed", "3 5 1", tests: null);
        var unfinished = Attempt("unfinished", null, tests: [("A", "One", true)]);

        // Act & Assert
        Assert.Empty(MatrixReport.ClassesToRerun(SuiteAttempt.Read(hung), 3));
        Assert.Empty(MatrixReport.ClassesToRerun(SuiteAttempt.Read(crashed), 3));
        Assert.Empty(MatrixReport.ClassesToRerun(SuiteAttempt.Read(unfinished), 3));
    }

    [Fact]
    public void Given_Attempts_When_Judged_Then_TheFlakeRuleDecides()
    {
        // Arrange
        var green = SuiteAttempt.Read(Attempt("g", "0 1 1", tests: [("A", "One", false)]));
        var red = SuiteAttempt.Read(Attempt("r", "1 1 1", tests: [("A", "One", true), ("B", "Two", false)]));
        var rerunGreen = SuiteAttempt.Read(Attempt("r/rerun-1", "0 1 1", tests: [("A", "One", false)], rerunClass: "Ns.A"));
        var rerunRed = SuiteAttempt.Read(Attempt("r2/rerun-1", "1 1 1", tests: [("A", "One", true)], rerunClass: "Ns.A"));
        var empty = SuiteAttempt.Read(Attempt("e", "0 1 1", tests: []));
        var hung = Attempt("h", "143 9 1", tests: null);
        File.WriteAllText(Path.Combine(hung, SuiteAttempt.TimedOutFile), "");

        // Act & Assert
        Assert.Equal(SuiteOutcome.Skipped, MatrixReport.Judge("Docker only", null, [], 3));
        Assert.Equal(SuiteOutcome.NotRun, MatrixReport.Judge(null, null, [], 3));
        Assert.Equal(SuiteOutcome.Green, MatrixReport.Judge(null, green, [], 3));
        Assert.Equal(SuiteOutcome.RerunGreen, MatrixReport.Judge(null, red, [rerunGreen], 3));
        Assert.Equal(SuiteOutcome.Failed, MatrixReport.Judge(null, red, [rerunRed], 3));
        Assert.Equal(SuiteOutcome.Failed, MatrixReport.Judge(null, red, [], 3));
        Assert.Equal(SuiteOutcome.Failed, MatrixReport.Judge(null, red, [rerunGreen], 0));
        Assert.Equal(SuiteOutcome.Failed, MatrixReport.Judge(null, empty, [], 3));
        Assert.Equal(SuiteOutcome.TimedOut, MatrixReport.Judge(null, SuiteAttempt.Read(hung), [], 3));
    }

    [Fact]
    public void Given_ABatchFolder_When_Summarized_Then_TheTableNotesAndExitCodeTellTheOutcome()
    {
        // Arrange
        var batch = Path.Combine(_root, "batch");
        Directory.CreateDirectory(batch);
        var plan = MatrixPlanner.Plan(["cln", "ldk", "postgres", "tor"], 2, false);
        File.WriteAllLines(Path.Combine(batch, MatrixReport.PlanFile), plan.Select(p => p.ToLine()));
        Attempt("batch/cln", "1 100 1000", tests: [("A", "One", true), ("B", "Two", false)],
                log: "[fixture] CLN fixture (Cluster) ready in 6.3 s\nnamespace nltg-spike-b-cln created");
        Attempt("batch/cln/rerun-1", "0 20 1100", tests: [("A", "One", false)], rerunClass: "Ns.A",
                log: "namespace nltg-spike-b-cln-r1 created");
        Attempt("batch/ldk", "0 50 1000", tests: [("A", "One", false)]);
        Attempt("batch/postgres", "1 30 1010", tests: [("A", "One", true)]);
        Attempt("batch/postgres/rerun-1", "1 30 1050", tests: [("A", "One", true)], rerunClass: "Ns.A");

        // Act
        var reports = MatrixReport.Read(batch, 3);
        var text = MatrixReport.Format(reports, _root, 1000, 1200);

        // Assert
        Assert.Equal([SuiteOutcome.RerunGreen, SuiteOutcome.Green, SuiteOutcome.Failed, SuiteOutcome.Skipped],
                     reports.Select(r => r.Outcome));
        Assert.Equal(1, MatrixReport.ExitCode(reports));
        Assert.Equal(0, MatrixReport.ExitCode(reports.Take(2).ToList()));
        var lines = text.Split('\n');
        Assert.Matches(@"^cln +rerun-green +2 +1 +1 +0 +0 +1/1 green +\+0s +120s +6\.3s +2/1 +0 +One: One broke$", lines[1]);
        Assert.Matches(@"^ldk +green +1 +1 +0 +0 +0 +- +\+0s +50s +- +0/1 +0$", lines[2]);
        Assert.Matches(@"^postgres +FAILED .* +0/1 green .* 0/2 ", lines[3]);
        Assert.Matches(@"^tor +skipped +- +- +- +- +- +- +- +- +- +- +-$", lines[4]);
        Assert.Contains("cln: Ns.A rerun alone: green (flake) (batch/cln/rerun-1)", text);
        Assert.Contains("postgres: Ns.A rerun alone: FAILED again", text);
        Assert.Contains("postgres: log batch/postgres/output.log", text);
        Assert.Contains("postgres: no diagnostics collected", text);
        Assert.Contains("tor: skipped: Docker only", text);
        Assert.EndsWith("3 suite(s) run: 1 green, 1 rerun-green, 1 failed; 1 skipped; matrix wall 200s\n", text);
    }

    [Fact]
    public void Given_ThePeakFile_When_Read_Then_TheFooterReportsIt()
    {
        // Arrange
        var batch = Path.Combine(_root, "peak");
        Directory.CreateDirectory(batch);
        File.WriteAllLines(Path.Combine(batch, MatrixReport.PlanFile),
                           MatrixPlanner.Plan(["tor"], 2, false).Select(p => p.ToLine()));
        File.WriteAllText(Path.Combine(batch, MatrixReport.PeakFile), "3 2 2\n");

        // Act
        var peak = MatrixReport.ReadPeak(batch);
        var text = MatrixReport.Format(MatrixReport.Read(batch, 3), _root, null, null, peak);

        // Assert
        Assert.Equal(new NamespacePeak(3, 2, 2), peak);
        Assert.EndsWith("peak run namespaces of the batch (sampled): 3 (2 active, the rest terminating); budget 2\n", text);
        Assert.Null(MatrixReport.ReadPeak(_root));
        File.WriteAllText(Path.Combine(batch, MatrixReport.PeakFile), "3 x");
        Assert.Null(MatrixReport.ReadPeak(batch));
    }

    [Fact]
    public void Given_ABatchWithoutPlan_When_Read_Then_ItIsRefused()
    {
        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => MatrixReport.Read(_root, 3));
    }

    [Fact]
    public async Task Given_MatrixCommands_When_Run_Then_PlanRerunAndSummaryWorkWithoutACluster()
    {
        // Arrange
        var batch = Path.Combine(_root, "cli");
        Directory.CreateDirectory(batch);
        var output = new StringWriter();
        var error = new StringWriter();

        // Act: the plan (no --repo: the LND suites count as not wired)
        var planCode = await MatrixCli.RunAsync(["plan", "--suites", "ldk,cln", "--max-namespaces", "2"], output, error);
        File.WriteAllText(Path.Combine(batch, MatrixReport.PlanFile), output.ToString());
        Attempt("cli/cln", "1 10 1", tests: [("A", "One", true)]);
        Attempt("cli/ldk", "0 10 1", tests: [("A", "One", false)]);
        var rerunOut = new StringWriter();
        var rerunCode = await MatrixCli.RunAsync(["rerun-classes", Path.Combine(batch, "cln"), "--max", "2"], rerunOut,
                                                 error);
        var summaryOut = new StringWriter();
        var summaryCode = await MatrixCli.RunAsync(["summary", batch, "--repo", _root, "--started", "1"], summaryOut,
                                                   error);

        // Assert
        Assert.Equal(0, planCode);
        Assert.Equal(["cln", "ldk"], output.ToString().Trim().Split('\n').Select(l => l.Split('|')[1]));
        Assert.Equal(0, rerunCode);
        Assert.Equal("Ns.A", rerunOut.ToString().Trim());
        Assert.Equal(1, summaryCode);
        Assert.Matches("(?m)^cln +FAILED", summaryOut.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Given_TheCapCommand_When_Run_Then_ItPrintsTheMachinesNamespaceCap()
    {
        // Arrange
        var output = new StringWriter();

        // Act
        var code = await MatrixCli.RunAsync(["cap"], output, TextWriter.Null);

        // Assert: run-cluster.sh reads its --max-namespaces and -j limit from here (NL-844)
        Assert.Equal(ClusterCli.Ok, code);
        Assert.Equal(RunAdmission.DefaultMaxRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     output.ToString().Trim());
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("plan", "--suites", "bogus")]
    [InlineData("plan", "--max-namespaces", "13")]
    [InlineData("cap", "--suites", "cln")]
    [InlineData("plan", "--max-namespaces", "x")]
    [InlineData("plan", "--max")]
    [InlineData("summary")]
    [InlineData("summary", "--repo", "x")]
    [InlineData("rerun-classes", "dir", "--max")]
    [InlineData("list", "--suites", "cln")]
    public async Task Given_ABadMatrixCommandLine_When_Run_Then_ItExitsWithUsage(params string[] args)
    {
        // Arrange
        var error = new StringWriter();

        // Act
        var code = await MatrixCli.RunAsync(args, TextWriter.Null, error);

        // Assert
        Assert.Equal(ClusterCli.Usage, code);
        Assert.StartsWith("nltg-cluster matrix:", error.ToString());
    }

    [Fact]
    public async Task Given_TheClusterCli_When_AskedForTheMatrix_Then_ItNeverBuildsAKubeClient()
    {
        // Arrange
        var output = new StringWriter();

        // Act
        var code = await ClusterCli.RunAsync(["matrix", "list"], output, TextWriter.Null,
                                             _ => throw new InvalidOperationException("no cluster for matrix"),
                                             TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ClusterCli.Ok, code);
        Assert.StartsWith("SUITE", output.ToString());
    }

    private string Attempt(string relative, string? exit, (string Class, string Method, bool Fail)[]? tests,
                           string? log = null, string? rerunClass = null)
    {
        var dir = Path.Combine(_root, relative);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SuiteAttempt.RunIdFile), "b-" + Path.GetFileName(relative));
        if (exit is not null)
            File.WriteAllText(Path.Combine(dir, SuiteAttempt.ExitFile), exit + "\n");
        if (log is not null)
            File.WriteAllText(Path.Combine(dir, SuiteAttempt.LogFile), log);
        if (rerunClass is not null)
            File.WriteAllText(Path.Combine(dir, SuiteAttempt.ClassFile), rerunClass + "\n");
        if (tests is not null)
            WriteResults(Path.Combine(dir, SuiteAttempt.ResultsFile), tests);

        return dir;
    }

    private static string WriteResults(string path, params (string Class, string Method, bool Fail)[] tests)
    {
        var failed = tests.Count(t => t.Fail);
        var elements = tests.Select(t => new XElement(
                                        "test", new XAttribute("name", $"Ns.{t.Class}.{t.Method}"),
                                        new XAttribute("type", $"Ns.{t.Class}"), new XAttribute("method", t.Method),
                                        new XAttribute("result", t.Fail ? "Fail" : "Pass"),
                                        t.Fail
                                            ? new XElement("failure",
                                                           new XElement("message", $"{t.Method} broke\nat line 2"))
                                            : null));
        new XDocument(new XElement("assemblies",
                                   new XElement("assembly", new XAttribute("total", tests.Length),
                                                new XAttribute("passed", tests.Length - failed),
                                                new XAttribute("failed", failed), new XAttribute("skipped", 0),
                                                new XAttribute("errors", 0),
                                                new XElement("collection", elements))))
            .Save(path);
        return path;
    }
}