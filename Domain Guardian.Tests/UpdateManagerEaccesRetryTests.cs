using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using AdHealthMonitor;
using Xunit;

namespace Domain_Guardian.Tests;

/// <summary>
/// Unit tests for <see cref="UpdateManager.OpenDownloadTargetStreamAsync"/>,
/// the retry-on-EACCES helper that defends the GitHub asset download path
/// against transient Defender Controlled Folder Access, Smart App Control,
/// and third-party antivirus file locks.
/// </summary>
public class UpdateManagerEaccesRetryTests : IDisposable
{
    private readonly string testRoot;

    public UpdateManagerEaccesRetryTests()
    {
        testRoot = Path.Combine(
            Path.GetTempPath(),
            "UpdateManagerEaccesRetryTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static UpdateManager.GitHubAsset MakeAsset(
        string name = "AD Guardian Installer.exe",
        string downloadUrl = "https://example.invalid/installer.exe") =>
        new() { name = name, browser_download_url = downloadUrl };

    private static Func<int, Task> NoOpDelay() => _ => Task.CompletedTask;

    private static Func<string, FileStream> SuccessFactory(List<string> calls) =>
        path =>
        {
            calls.Add(path);
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        };

    // ── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_FirstAttemptOpensStream_NoRetry()
    {
        UpdateManager.GitHubAsset asset = MakeAsset();
        List<string> factoryCalls = new();
        List<int> delayCalls = new();

        FileStream result = await UpdateManager.OpenDownloadTargetStreamAsync(
            asset,
            streamFactory: SuccessFactory(factoryCalls),
            delay: ms => { delayCalls.Add(ms); return Task.CompletedTask; },
            overrideTempRoot: testRoot);

        using (result)
        {
            Assert.True(result.CanWrite);
            Assert.Single(factoryCalls);
            Assert.Equal(Path.Combine(testRoot, "ADGuardianUpdate", asset.name), factoryCalls[0]);
            // No retries were needed → no delay was awaited.
            Assert.Empty(delayCalls);
            // The expected target path is at tempRoot\ADGuardianUpdate\<asset.name>
            Assert.Equal(
                Path.Combine(testRoot, "ADGuardianUpdate", asset.name),
                result.Name);
        }

        // Make sure the producer-side dir exists.
        Assert.True(Directory.Exists(Path.Combine(testRoot, "ADGuardianUpdate")));
    }

    // ── Retry succeeds ────────────────────────────────────────────────────

    [Fact]
    public async Task RetrySucceeds_AfterUnauthorizedAccessException_OnSecondAttempt()
    {
        UpdateManager.GitHubAsset asset = MakeAsset();
        int callCount = 0;
        FileStream? capturedStream = null;

        static FileStream Flaky(string path, ref int n, ref FileStream? captured)
        {
            n++;
            if (n == 1)
            {
                throw new UnauthorizedAccessException(
                    "Simulated Defender Controlled Folder Access block.", new Win32Exception(5));
            }
            captured = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            return captured;
        }

        List<int> delayCalls = new();
        FileStream result = await UpdateManager.OpenDownloadTargetStreamAsync(
            asset,
            streamFactory: path => Flaky(path, ref callCount, ref capturedStream),
            delay: ms => { delayCalls.Add(ms); return Task.CompletedTask; },
            overrideTempRoot: testRoot);

        using (result)
        {
        }
        // Flaky factory: 1st call throws (callCount=1), 2nd call succeeds (callCount=2).
        Assert.Equal(2, callCount);
        Assert.NotNull(capturedStream);
        Assert.Same(capturedStream, result);
        // First retry used InitialBackoffMs * attempt(1) = 250ms.
        Assert.Equal(new[] { 250 }, delayCalls);
    }

    [Fact]
    public async Task RetrySucceeds_AfterIOException_WithSharingViolationHResult()
    {
        UpdateManager.GitHubAsset asset = MakeAsset();
        int callCount = 0;
        FileStream? captured = null;
        // HRESULT 0x80070020 = ERROR_SHARING_VIOLATION (32). The most common
        // symptom during an active Defender scan.
        const int ESharingViolation = unchecked((int)0x80070020);

        Func<string, FileStream> factory = path =>
        {
            callCount++;
            if (callCount < 3)
            {
                throw new IOException("Simulated share violation during scan.")
                {
                    HResult = ESharingViolation,
                };
            }
            captured = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            return captured;
        };

        FileStream result = await UpdateManager.OpenDownloadTargetStreamAsync(
            asset,
            streamFactory: factory,
            delay: NoOpDelay(),
            overrideTempRoot: testRoot);

        using (result)
        {
            Assert.Equal(3, callCount);
        }
        Assert.NotNull(captured);
        Assert.Same(captured, result);
    }

    // ── Exhausted retries ────────────────────────────────────────────────

    [Fact]
    public async Task ExhaustsRetries_ThrowsIOException_WithFallbackUrlInMessage()
    {
        UpdateManager.GitHubAsset asset = MakeAsset(
            name: "AD Guardian Installer.exe",
            downloadUrl: "https://example.invalid/v2.0.27/installer.exe");
        int callCount = 0;

        Func<string, FileStream> alwaysFails = _ =>
        {
            callCount++;
            throw new UnauthorizedAccessException("always blocked by Defender");
        };

        List<int> delayCalls = new();
        IOException thrown = await Assert.ThrowsAsync<IOException>(async () =>
            await UpdateManager.OpenDownloadTargetStreamAsync(
                asset,
                streamFactory: alwaysFails,
                delay: ms => { delayCalls.Add(ms); return Task.CompletedTask; },
                overrideTempRoot: testRoot));

        Assert.Equal(UpdateManager.MaxDownloadAttempts, callCount);
        // Between each of the 5 attempts we delay once for a total of 4 waits.
        Assert.Equal(new[] { 250, 500, 750, 1000 }, delayCalls);
        Assert.Contains($"after {UpdateManager.MaxDownloadAttempts} attempts", thrown.Message);
        Assert.Contains(asset.browser_download_url, thrown.Message);
        Assert.Contains("Defender Controlled Folder Access", thrown.Message);
        Assert.IsType<UnauthorizedAccessException>(thrown.InnerException);
    }

    // ── Non-EACCES exceptions propagate without retry ────────────────────

    [Fact]
    public async Task NonEaccesException_PropagatesImmediately_WithoutRetry()
    {
        UpdateManager.GitHubAsset asset = MakeAsset();
        int callCount = 0;
        int delayCalls = 0;
        // HResult 0x80070040 = ERROR_NETNAME_DELETED. Not retryable.
        const int ENotARetryableCode = unchecked((int)0x80070040);

        Func<string, FileStream> throwsImmediately = _ =>
        {
            callCount++;
            throw new IOException("Not retryable")
            {
                HResult = ENotARetryableCode,
            };
        };

        IOException thrown = await Assert.ThrowsAsync<IOException>(async () =>
            await UpdateManager.OpenDownloadTargetStreamAsync(
                asset,
                streamFactory: throwsImmediately,
                delay: _ => { delayCalls++; return Task.CompletedTask; },
                overrideTempRoot: testRoot));

        Assert.Equal(1, callCount);
        Assert.Equal(0, delayCalls);
        Assert.Same(thrown.GetType(), typeof(IOException));
    }

    [Fact]
    public async Task ArgumentException_IsNotRetried_PropagatesImmediately()
    {
        UpdateManager.GitHubAsset asset = MakeAsset(name: "AD Guardian Installer.exe");
        int callCount = 0;

        Func<string, FileStream> throwsArg = _ =>
        {
            callCount++;
            throw new ArgumentException("Path contains invalid character.", "path");
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await UpdateManager.OpenDownloadTargetStreamAsync(
                asset,
                streamFactory: throwsArg,
                delay: NoOpDelay(),
                overrideTempRoot: testRoot));

        Assert.Equal(1, callCount);
    }

    // ── Classifier coverage ──────────────────────────────────────────────

    [Theory]
    [InlineData(true)] // UnauthorizedAccessException → retryable
    public async Task Classifier_UnauthorizedAccessException_IsRetryable_VerifyBySucceedingOnSecondAttempt(bool _)
    {
        UpdateManager.GitHubAsset asset = MakeAsset();
        int n = 0;
        Func<string, FileStream> factory = path =>
        {
            n++;
            if (n == 1) throw new UnauthorizedAccessException("acl");
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        };

        FileStream result = await UpdateManager.OpenDownloadTargetStreamAsync(
            asset, streamFactory: factory, delay: NoOpDelay(), overrideTempRoot: testRoot);
        using (result) { }
        Assert.Equal(2, n);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), true)] // ERROR_ACCESS_DENIED
    [InlineData(unchecked((int)0x80070020), true)] // ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070021), true)] // ERROR_LOCK_VIOLATION
    [InlineData(unchecked((int)0x80070040), false)] // ERROR_NETNAME_DELETED -> NOT retryable
    [InlineData(unchecked((int)0x80070002), false)] // ERROR_FILE_NOT_FOUND -> NOT retryable
    [InlineData(unchecked((int)0x8007000E), false)] // ERROR_OUTOFMEMORY -> NOT retryable
    public void Classifier_IOExceptionHResult_IsClassifiedExpectedly(int hresult, bool expectedRetryable)
    {
        IOException ex = new("test") { HResult = hresult };
        Assert.Equal(expectedRetryable, UpdateManager.IsEaccesLikeException(ex));
    }

    [Theory]
    [InlineData(5, true)]   // ERROR_ACCESS_DENIED
    [InlineData(32, true)]  // ERROR_SHARING_VIOLATION
    [InlineData(33, true)]  // ERROR_LOCK_VIOLATION
    [InlineData(2, false)]  // ERROR_FILE_NOT_FOUND
    public void Classifier_Win32ExceptionInner_IsClassifiedExpectedly(int nativeErrorCode, bool expectedRetryable)
    {
        IOException ex = new("test", new Win32Exception(nativeErrorCode));
        Assert.Equal(expectedRetryable, UpdateManager.IsEaccesLikeException(ex));
    }

    [Fact]
    public void Classifier_UnauthorizedAccessException_IsRetryable()
    {
        Assert.True(UpdateManager.IsEaccesLikeException(new UnauthorizedAccessException()));
    }

    [Fact]
    public void Classifier_NonIoException_IsNotRetryable()
    {
        Assert.False(UpdateManager.IsEaccesLikeException(new InvalidOperationException()));
        Assert.False(UpdateManager.IsEaccesLikeException(new ArgumentException()));
        Assert.False(UpdateManager.IsEaccesLikeException(new TaskCanceledException()));
    }

    // ── Path composition ──────────────────────────────────────────────────

    [Fact]
    public async Task Factory_ReceivesPathIncludingAssetNameUnderTempRoot()
    {
        UpdateManager.GitHubAsset asset = MakeAsset(name: "AD.Guardian.Setup.exe");
        List<string> factoryCalls = new();

        FileStream result = await UpdateManager.OpenDownloadTargetStreamAsync(
            asset,
            streamFactory: SuccessFactory(factoryCalls),
            delay: NoOpDelay(),
            overrideTempRoot: testRoot);
        using (result) { }

        string expected = Path.Combine(testRoot, "ADGuardianUpdate", "AD.Guardian.Setup.exe");
        Assert.Equal(expected, factoryCalls[0]);
    }

    [Fact]
    public async Task OverrideTempRoot_DefaultsToSystemTempPath_WhenNotSupplied()
    {
        // Specifically exercise the production path (not the override). We have to
        // construct a unique asset.name so we don't collide with concurrent test runs
        // (xUnit may parallelise collections within an assembly). The test cleans up
        // the orphan file in finally -- %TEMP% writes are otherwise permanent.
        UpdateManager.GitHubAsset asset = MakeAsset(name: $"probe-{Guid.NewGuid():N}.exe");
        string writtenPath = Path.Combine(Path.GetTempPath(), "ADGuardianUpdate", asset.name);
        try
        {
            FileStream result = await UpdateManager.OpenDownloadTargetStreamAsync(
                asset,
                streamFactory: null,
                delay: NoOpDelay(),
                overrideTempRoot: null); // -> defaults to Path.GetTempPath()

            string expected = Path.Combine(
                Path.GetTempPath(),
                "ADGuardianUpdate",
                asset.name);

            Assert.Equal(expected, result.Name);
            Assert.Equal(expected, writtenPath);
            using (result) { }
        }
        finally
        {
            try { if (File.Exists(writtenPath)) File.Delete(writtenPath); } catch { /* best-effort */ }
        }
    }

    // ── Backoff sequence ──────────────────────────────────────────────────

    [Fact]
    public async Task BackoffSequence_IsLinear_250PerAttempt()
    {
        UpdateManager.GitHubAsset asset = MakeAsset();
        Func<string, FileStream> alwaysFail = _ =>
            throw new UnauthorizedAccessException("blocked");

        List<int> delayCalls = new();
        await Assert.ThrowsAsync<IOException>(async () =>
            await UpdateManager.OpenDownloadTargetStreamAsync(
                asset,
                streamFactory: alwaysFail,
                delay: ms => { delayCalls.Add(ms); return Task.CompletedTask; },
                overrideTempRoot: testRoot));

        // 5 attempts, 4 delays in between, each one InitialBackoffMs * attempt.
        Assert.Equal(
            new[] { 1 * 250, 2 * 250, 3 * 250, 4 * 250 },
            delayCalls);
    }

    // ── Constants are internal-visible to tests ───────────────────────────

    [Fact]
    public void MaxDownloadAttempts_IsFive()
    {
        // 5 attempts with 4 inter-attempt delays of 250/500/750/1000ms = 2500ms total.
        Assert.Equal(5, UpdateManager.MaxDownloadAttempts);
    }
}
