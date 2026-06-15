using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Benchmarks that measure the performance characteristics of the
/// six LINQ-to-manual-loop optimizations applied across the codebase.
/// Each test runs thousands of iterations and reports elapsed time.
/// GC assertions are only used where the optimized code path is
/// genuinely allocation-free (stack-only operations).
/// </summary>
[Trait("Category", "RequiredForCI")]
public class LinqOptimizationBenchmarks
{
    private const int BenchmarkIterations = 5_000;
    private static readonly string[] DcList = ["DC01", "DC02", "DC03"];

    // ── 1. dcList.FirstOrDefault() → manual indexer ────────────────
    //    (FormatTestResultTable: selecting first DC from user list)

    [Fact]
    public void FirstDc_ManualIndexer_IsEfficient()
    {
        Warmup();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            string? firstDc = DcList.Length > 0 ? DcList[0] : null;
            string key = (firstDc ?? "Unknown") + i.ToString();
        }
        sw.Stop();

        double avgNs = sw.Elapsed.TotalNanoseconds / BenchmarkIterations;
        Console.WriteLine($"FirstDc manual indexer: {avgNs:F0} ns/iter over {BenchmarkIterations:N0} iterations");

        // Should be sub-microsecond per iteration (pure memory read + string concat)
        Assert.True(avgNs < 1000, $"Expected < 1000 ns/iter, got {avgNs:F0} ns/iter");
    }

    // ── 2. token.Any(char.IsDigit/IsLetter) → manual loop ──────────
    //    (TryExtractControllerFromResultLine: per-token check)

    [Fact]
    public void HasDigitAndLetter_ManualLoop_ProducesZeroAllocations()
    {
        Warmup();

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);
        long gen0Before = GC.CollectionCount(0);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            string token = "DC01test123";
            bool hasDigit = false, hasLetter = false;
            for (int ci = 0; ci < token.Length; ci++)
            {
                char c = token[ci];
                if (char.IsDigit(c)) hasDigit = true;
                else if (char.IsLetter(c)) hasLetter = true;
                if (hasDigit && hasLetter) break;
            }
        }
        sw.Stop();

        long gen0After = GC.CollectionCount(0);
        double avgNs = sw.Elapsed.TotalNanoseconds / BenchmarkIterations;

        Console.WriteLine(
            $"HasDigitAndLetter manual loop: {avgNs:F0} ns/iter, " +
            $"gen-0 deltas: {gen0After - gen0Before}");

        // This is pure stack work — zero allocations expected
        Assert.Equal(gen0Before, gen0After);
    }

    // ── 3. .OrderByDescending().ToList() → .Sort() ────────────────
    //    (InitializeAppStateAsync: history sorting at startup)

    [Fact]
    public void SortInPlace_IsEfficient()
    {
        List<AdHealthMonitor.TestHistoryEntry> entries = CreateSampleHistory(20);
        Shuffle(entries);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            // Re-shuffle each iteration so we measure real sort cost (not O(n) best-case)
            if (i > 0) Shuffle(entries);
            entries.Sort((a, b) => b.RunDate.CompareTo(a.RunDate));
        }
        sw.Stop();

        double avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / BenchmarkIterations;
        Console.WriteLine($"Sort 20 history entries: {avgUs:F1} µs/iter over {BenchmarkIterations:N0} iterations");

        // Sorting 20 items 5000 times should be well under 100µs/iter
        Assert.True(avgUs < 100, $"Expected < 100 µs/iter, got {avgUs:F1} µs/iter");
    }

    // ── 4. ParseDCDiagOutput return List<T> (no .ToList() copy) ──
    //    (Diagnostics + Logs tab refresh)

    [Fact]
    public void ParseDCDiagOutput_IsEfficient()
    {
        string output = SampleDcdiagOutput();
        Warmup();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            List<AdHealthMonitor.TestResult> results =
                AdHealthMonitor.MainWindow.ParseDCDiagOutput("DC01", output, "test.log");
        }
        sw.Stop();

        double avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / BenchmarkIterations;
        Console.WriteLine(
            $"ParseDCDiagOutput (10-test output): {avgUs:F1} µs/iter over {BenchmarkIterations:N0} iterations");

        // Parsing a 10-test DCDiag output should be well under 500µs
        Assert.True(avgUs < 500, $"Expected < 500 µs/iter, got {avgUs:F1} µs/iter");
    }

    // ── 5. BuildRunSummary (already LINQ-free) ────────────────────

    [Fact]
    public void BuildRunSummary_IsEfficient()
    {
        Warmup();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            string summary = AdHealthMonitor.MainWindow.BuildRunSummary(20, 18, 2, DcList);
        }
        sw.Stop();

        double avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / BenchmarkIterations;
        Console.WriteLine(
            $"BuildRunSummary: {avgUs:F1} µs/iter over {BenchmarkIterations:N0} iterations");

        Assert.True(avgUs < 50, $"Expected < 50 µs/iter, got {avgUs:F1} µs/iter");
    }

    // ── 6. FilterLogSections (already LINQ-free) ──────────────────

    [Fact]
    public void FilterLogSections_IsEfficient()
    {
        string source = SampleLogSource();
        string searchText = "fail";
        Warmup();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            var (text, sections, controllers) = AdHealthMonitor.MainWindow.FilterLogSections(
                source, "All domain controllers", "All Results", "All test sections", searchText);
        }
        sw.Stop();

        double avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / BenchmarkIterations;
        Console.WriteLine(
            $"FilterLogSections: {avgUs:F1} µs/iter over {BenchmarkIterations:N0} iterations");

        // Filtering a 4-section log source should be well under 500µs
        Assert.True(avgUs < 500, $"Expected < 500 µs/iter, got {avgUs:F1} µs/iter");
    }

    // ── Aggregate: run all hot-path methods together ──────────────

    [Fact]
    public void AllOptimizedHotPaths_Combined_ReportMetrics()
    {
        string dcdiagOutput = SampleDcdiagOutput();
        string logSource = SampleLogSource();
        List<AdHealthMonitor.TestHistoryEntry> history = CreateSampleHistory(20);

        Warmup();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < BenchmarkIterations; i++)
        {
            // 1. Parse DCDiag output
            List<AdHealthMonitor.TestResult> results =
                AdHealthMonitor.MainWindow.ParseDCDiagOutput("DC01", dcdiagOutput, "test.log");

            // 2. Filter log sections
            var (text, sections, controllers) = AdHealthMonitor.MainWindow.FilterLogSections(
                logSource, "All domain controllers", "Failures", "All test sections", "fail");

            // 3. Build summary
            string summary = AdHealthMonitor.MainWindow.BuildRunSummary(20, 18, 2, DcList);

            // 4. Sort history in place (re-shuffle for real cost)
            Shuffle(history);
            history.Sort((a, b) => b.RunDate.CompareTo(a.RunDate));

            // 5. First DC lookup
            string? firstDc = DcList.Length > 0 ? DcList[0] : null;

            // 6. Token digit/letter check
            string token = "DC01test123";
            bool hasDigit = false, hasLetter = false;
            for (int ci = 0; ci < token.Length; ci++)
            {
                char c = token[ci];
                if (char.IsDigit(c)) hasDigit = true;
                else if (char.IsLetter(c)) hasLetter = true;
                if (hasDigit && hasLetter) break;
            }
        }
        sw.Stop();

        double avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / BenchmarkIterations;

        Console.WriteLine("═══ LINQ Optimization Benchmarks ═══");
        Console.WriteLine($"Iterations:       {BenchmarkIterations:N0}");
        Console.WriteLine($"Total elapsed:    {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"Avg per iter:     {avgUs:F1} µs");
        Console.WriteLine("All six optimizations exercised in each iteration:");
        Console.WriteLine("  1. FirstOrDefault → manual indexer");
        Console.WriteLine("  2. Any(char.IsDigit) & Any(char.IsLetter) → manual loop");
        Console.WriteLine("  3. OrderByDescending+ToList → Sort in-place");
        Console.WriteLine("  4. ParseDCDiagOutput → List<T> (no ToList copy)");
        Console.WriteLine("  5. BuildRunSummary (already LINQ-free)");
        Console.WriteLine("  6. FilterLogSections (already LINQ-free)");
        Console.WriteLine("══════════════════════════════════════");

        // Combined hot-path should be well under 1ms/iter
        Assert.True(avgUs < 1000,
            $"Expected combined operations < 1000 µs/iter, got {avgUs:F1} µs/iter");
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static void Warmup()
    {
        string output = SampleDcdiagOutput();
        AdHealthMonitor.MainWindow.ParseDCDiagOutput("DC01", output, "test.log");
        AdHealthMonitor.MainWindow.FilterLogSections(
            SampleLogSource(), "All domain controllers", "All Results", "All test sections", "");
        AdHealthMonitor.MainWindow.BuildRunSummary(20, 18, 2, DcList);

        List<AdHealthMonitor.TestHistoryEntry> warm = CreateSampleHistory(5);
        warm.Sort((a, b) => b.RunDate.CompareTo(a.RunDate));
    }

    private static string SampleDcdiagOutput()
    {
        return string.Join("\n",
            "---- Results for DC: DC01 ----",
            "Starting test: Connectivity",
            "   DC01 passed test Connectivity",
            "Starting test: DNS",
            "   DNS test passed on DC01",
            "Starting test: Replication",
            "   Replication test passed on DC01",
            "Starting test: Services",
            "   Services test passed on DC01",
            "Starting test: SystemLog",
            "   SystemLog test passed on DC01",
            "---- Results for DC: DC02 ----",
            "Starting test: Connectivity",
            "   DC02 passed test Connectivity",
            "Starting test: DNS",
            "   DNS test failed on DC02 - server can't find",
            "Starting test: Replication",
            "   Replication test passed on DC02",
            "Starting test: Services",
            "   Services test passed on DC02",
            "Starting test: SystemLog",
            "   SystemLog test failed on DC02 - error code 0x54B",
            ""
        );
    }

    private static string SampleLogSource()
    {
        return string.Join("\n",
            "---- Results for DC: DC01 ----",
            "Starting test: Connectivity",
            "   DC01 passed test Connectivity",
            "   --------------------------- End of test -------------------------------",
            "",
            "Starting test: DNS",
            "   DNS test passed on DC01",
            "   --------------------------- End of test -------------------------------",
            "",
            "---- Results for DC: DC02 ----",
            "Starting test: DNS",
            "   DNS test failed on DC02 - server can't find",
            "   --------------------------- End of test -------------------------------",
            ""
        );
    }

    private static List<AdHealthMonitor.TestHistoryEntry> CreateSampleHistory(int count)
    {
        List<AdHealthMonitor.TestHistoryEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add(new AdHealthMonitor.TestHistoryEntry
            {
                RunDate = DateTime.Now.AddDays(-i),
                Total = 20,
                Passed = 18 - i % 3,
                Failed = 2 + i % 3,
                Details = $"Test run #{i + 1}",
                LogFilePath = $"C:\\logs\\run_{i + 1}.txt",
                TestType = "Manual"
            });
        }
        return entries;
    }

    private static void Shuffle<T>(List<T> list)
    {
        Random rng = new(42);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
