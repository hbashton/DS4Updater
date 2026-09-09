using System.Runtime.CompilerServices;
using DS4Updater.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class PortableUpdateOrchestrationTests
{
    [TestMethod]
    public void InstalledAndDeeplyStagedIdentitiesRetainExactPeVersionsPastMaxPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "updater-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string image = typeof(PortableUpdateCoordinator).Assembly.Location;
            var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(image);
            using var operations = new PortableUpdateOperations(root, null);
            foreach (string location in new[] { root,
                Path.Combine(root, new string('a', 100), new string('b', 100), new string('c', 50)) })
            {
                Directory.CreateDirectory(location);
                foreach (string name in new[] { "DS4Windows.exe", "DS4Windows.dll", "CustomPad.exe" })
                    File.Copy(image, Path.Combine(location, name)); // Version-bearing fixture only; never executed.
                File.WriteAllText(Path.Combine(location, "DS4Windows.release"), "VIIPERRC4.5.1\n");
                foreach (string executable in new[] { "DS4Windows.exe", "CustomPad.exe" })
                {
                    var identity = operations.ReadIdentity(location, executable);
                    Assert.AreEqual(expected.FileVersion, identity.FileVersion);
                    Assert.AreEqual(expected.ProductVersion, identity.ProductVersion);
                    Assert.AreEqual("VIIPERRC4.5.1", identity.ReleaseTag);
                }
            }
        }
        finally
        {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(root).StartsWith("updater-version-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ExplicitRequestPreservesParentIdentityTagAndCustomExecutableIntoWorker()
    {
        string executable = Path.Combine(Path.GetTempPath(), "portable-parser", "DS4Updater.exe");
        var request = PortableUpdateRequest.Parse(Arguments(launch: "Game.Pad.exe"), executable);
        Assert.AreEqual(Path.GetDirectoryName(executable), request.TargetDirectory);
        Assert.AreEqual(1234, request.ParentPid);
        Assert.AreEqual(638000000000000000L, request.ParentStartUtcTicks);
        Assert.AreEqual("VIIPERRC4.5", request.ReleaseTag);
        Assert.AreEqual("Game.Pad", request.CustomExeBaseName);
        string worker = Path.Combine(request.TargetDirectory, "Updates", "portable-worker-" + Guid.NewGuid().ToString("N"), "DS4Updater.exe");
        Assert.AreEqual(request with { IsWorker = true }, PortableUpdateRequest.Parse(request.WorkerArguments(), worker));
    }

    [DataTestMethod]
    [DataRow("--parentPid", "0")]
    [DataRow("--parentPid", "-4")]
    [DataRow("--parentStartUtcTicks", "0")]
    [DataRow("--parentStartUtcTicks", "9223372036854775807")]
    [DataRow("--releaseTag", "../VIIPERRC4.5")]
    [DataRow("--launchExe", "../DS4Windows.exe")]
    [DataRow("--launchExe", "viiper.exe")]
    [DataRow("--launchExe", "")]
    public void MalformedPortableValuesAreRejected(string option, string value)
    {
        string[] args = Arguments();
        args[Array.IndexOf(args, option) + 1] = value;
        Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(args, Executable));
    }

    [TestMethod]
    public void DuplicateUnknownAndRetargetedLauncherArgumentsNeverFallBackToLegacy()
    {
        foreach (string[] extra in new[] { new[] { "--releaseTag", "VIIPERRC4.4" }, new[] { "-autolaunch" },
                     new[] { "--targetDirectory", Path.GetTempPath() }, new[] { "--portable-safe-v2" } })
        {
            string[] args = Arguments().Concat(extra).ToArray();
            Assert.IsTrue(PortableUpdateRequest.IsPortableInvocation(args));
            Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(args, Executable));
        }
        Assert.IsTrue(PortableUpdateRequest.IsPortableInvocation(new[] { "--portable-worker" }));
        Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(new[] { "--portable-worker" }, Executable));
    }

    [TestMethod]
    public void WorkerMustRunInsideItsExactTargetOwnedGuidDirectory()
    {
        var request = PortableUpdateRequest.Parse(Arguments(), Executable);
        foreach (string path in new[] { Executable,
                     Path.Combine(request.TargetDirectory, "Updates", "portable-worker-not-a-guid", "DS4Updater.exe"),
                     Path.Combine(request.TargetDirectory, "Elsewhere", "portable-worker-" + Guid.NewGuid().ToString("N"), "DS4Updater.exe") })
            Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(request.WorkerArguments(), path));
    }

    [TestMethod]
    public async Task PipelineChecksExactMetadataStageIdentityAndQuiescenceBeforeApplying()
    {
        var ops = new FakeOperations();
        await PortableUpdateCoordinator.ExecuteAsync(Request(), ops, null, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "validate", "installed", "fetch", "download", "prepare", "staged",
            "wait", "validate", "apply", "installed", "dispose" }, ops.Events);
        Assert.IsTrue(ops.Applied);
        Assert.IsNull(ops.AppliedAlias);
    }

    [TestMethod]
    public async Task PipelinePassesOnlyConfirmedCustomBasenameToTransaction()
    {
        var ops = new FakeOperations();
        await PortableUpdateCoordinator.ExecuteAsync(Request() with { LaunchExe = "Game.Pad.exe" }, ops, null, CancellationToken.None);
        Assert.AreEqual("Game.Pad", ops.AppliedAlias);
    }

    [DataTestMethod]
    [DataRow("wrong-tag")]
    [DataRow("draft")]
    [DataRow("rollback")]
    [DataRow("missing-digest")]
    [DataRow("missing-size")]
    [DataRow("wrong-host")]
    public async Task UnverifiedOrNonForwardReleaseCannotDownloadOrApply(string error)
    {
        var ops = new FakeOperations();
        if (error == "wrong-tag") ops.Release = ops.Release with { tag_name = "VIIPERRC4.5.1" };
        if (error == "draft") ops.Release = ops.Release with { draft = true };
        if (error == "rollback") ops.Installed = new("5.0.5.1", "VIIPERRC4.5.1", "VIIPERRC4.5.1");
        if (error == "missing-digest") ops.Release.assets[0] = ops.Release.assets[0] with { digest = null };
        if (error == "missing-size") ops.Release.assets[0] = ops.Release.assets[0] with { size = null };
        if (error == "wrong-host") ops.Release.assets[0] = ops.Release.assets[0] with { browser_download_url = "https://example.com/package.zip" };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => PortableUpdateCoordinator.ExecuteAsync(Request(), ops, null, CancellationToken.None));
        Assert.IsFalse(ops.Events.Contains("download"));
        Assert.IsFalse(ops.Applied);
    }

    [TestMethod]
    public async Task StaleReleaseMarkerCannotAuthorizeDowngradingNewerWindowsBinaries()
    {
        var ops = new FakeOperations { Installed = new("5.0.5.1", "VIIPERRC4.5.1", "VIIPERRC4.4") };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => PortableUpdateCoordinator.ExecuteAsync(Request(), ops, null, CancellationToken.None));
        Assert.IsFalse(ops.Events.Contains("download"));
        Assert.IsFalse(ops.Applied);
    }

    [TestMethod]
    public async Task WrongStagedBinaryIdentityDisposesStageWithoutWaitingOrApplying()
    {
        var ops = new FakeOperations { Staged = new("5.0.4.0", "VIIPERRC4.5", "VIIPERRC4.5") };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => PortableUpdateCoordinator.ExecuteAsync(Request(), ops, null, CancellationToken.None));
        Assert.IsFalse(ops.Applied);
        Assert.IsFalse(ops.Events.Contains("wait"));
        Assert.AreEqual("dispose", ops.Events.Last());
    }

    [TestMethod]
    public async Task ProcessGuardFailureCannotApplyAndAlwaysDisposesStage()
    {
        var ops = new FakeOperations { WaitFailure = new IOException("still running") };
        await Assert.ThrowsExceptionAsync<IOException>(() => PortableUpdateCoordinator.ExecuteAsync(Request(), ops, null, CancellationToken.None));
        Assert.IsFalse(ops.Applied);
        Assert.AreEqual("dispose", ops.Events.Last());
    }

    [TestMethod]
    public async Task CancellationAtQuiescenceDoesNotInstallOrReportSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var ops = new FakeOperations { OnWait = cancellation.Cancel };
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => PortableUpdateCoordinator.ExecuteAsync(Request(), ops, null, cancellation.Token));
        Assert.IsFalse(ops.Applied);
        Assert.AreEqual("dispose", ops.Events.Last());
    }

    [TestMethod]
    public async Task FailedApplyNeverChecksOrLaunchesTheNewApplication()
    {
        var ops = new FakeOperations { ApplyFailure = new IOException("rolled back") };
        await Assert.ThrowsExceptionAsync<IOException>(() => PortableUpdateCoordinator.ExecuteAsync(Request(), ops, null, CancellationToken.None));
        Assert.AreEqual(1, ops.Events.Count(e => e == "installed"));
        Assert.AreEqual("dispose", ops.Events.Last());
    }

    [DataTestMethod]
    [DataRow("http://github.com/file")]
    [DataRow("https://example.com/file")]
    [DataRow("https://github.com.evil.test/file")]
    [DataRow("https://user@github.com/file")]
    [DataRow("https://github.com:444/file")]
    [DataRow("file:///C:/file.zip")]
    public void DownloadsCannotRedirectOutsideApprovedHttpsHosts(string address) =>
        Assert.ThrowsException<InvalidDataException>(() => PortableUpdateOperations.ValidateDownloadUri(new Uri(address)));

    [TestMethod]
    public void PortableStartupBranchesBeforeLegacyConstructorAndGuardsBothExitHandlers()
    {
        string source = File.ReadAllText(SourcePath("App.xaml.cs"));
        Assert.IsTrue(source.IndexOf("PortableUpdateRequest.ShouldUsePortableLifetime", StringComparison.Ordinal) >= 0);
        Assert.IsTrue(source.IndexOf("PortableUpdateRequest.ShouldUsePortableLifetime", StringComparison.Ordinal) <
            source.IndexOf("mwd = new MainWindow", StringComparison.Ordinal));
        Assert.AreEqual(2, source.Split("if (portableSession) return;", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(source, "portableSession = true;");
        StringAssert.Contains(source, "PortableWorkerSession.ValidateWorker(request, executable)");
        string coordinator = File.ReadAllText(SourcePath("PortableUpdateCoordinator.cs"));
        Assert.IsFalse(coordinator.Contains(".Kill(", StringComparison.Ordinal));
        Assert.IsFalse(coordinator.Contains("Process.Start(", StringComparison.Ordinal));
        string ui = File.ReadAllText(SourcePath("PortableUpdateWindow.cs"));
        StringAssert.Contains(ui, "if (!successful || busy) return;");
        StringAssert.Contains(ui, "args.Cancel = true;");
    }

    private static string Executable => Path.Combine(Path.GetTempPath(), "portable-parser", "DS4Updater.exe");
    private static string[] Arguments(string launch = "DS4Windows.exe") => new[] { PortableUpdateRequest.PortableFlag,
        "--parentPid", "1234", "--parentStartUtcTicks", "638000000000000000", "--releaseTag", "VIIPERRC4.5", "--launchExe", launch };
    private static PortableUpdateRequest Request() => PortableUpdateRequest.Parse(Arguments(), Executable) with { IsWorker = true };
    private static string SourcePath(string name, [CallerFilePath] string caller = "") =>
        Path.Combine(Path.GetDirectoryName(caller), "..", "Updater2", name);

    private sealed class FakeOperations : IPortableUpdateOperations, IPortablePreparedPackage
    {
        internal readonly List<string> Events = new();
        internal PortableInstalledIdentity Installed = new("5.0.4.0", "VIIPERRC4.4", "VIIPERRC4.4");
        internal PortableInstalledIdentity Staged = new("5.0.5.0", "VIIPERRC4.5", "VIIPERRC4.5");
        internal GitHubRelease Release = new("VIIPERRC4.5", true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[] { new GitHubReleaseAsset("DS4Windows_VIIPER_x64.zip",
                "https://github.com/hbashton/DS4Windows/releases/download/VIIPERRC4.5/DS4Windows_VIIPER_x64.zip", 100,
                "sha256:" + new string('a', 64)) });
        internal bool Applied;
        internal string AppliedAlias;
        internal Exception WaitFailure, ApplyFailure;
        internal Action OnWait;
        public string StagedRoot => "fake-stage";
        public string ValidateRoot(string target) { Events.Add("validate"); return target; }
        public PortableInstalledIdentity ReadIdentity(string root, string launchExe)
        {
            Events.Add(root == StagedRoot ? "staged" : "installed");
            return root == StagedRoot || Applied ? Staged : Installed;
        }
        public Task<GitHubRelease> FetchReleaseAsync(string tag, CancellationToken cancellation) { Events.Add("fetch"); return Task.FromResult(Release); }
        public Task<string> DownloadAsync(GitHubReleaseAsset asset, CancellationToken cancellation) { Events.Add("download"); return Task.FromResult("fake-archive"); }
        public IPortablePreparedPackage Prepare(string target, string archive, string digest, string tag) { Events.Add("prepare"); return this; }
        public Task WaitForQuiescenceAsync(PortableUpdateRequest request, CancellationToken cancellation)
        {
            Events.Add("wait");
            OnWait?.Invoke();
            return WaitFailure == null ? Task.CompletedTask : Task.FromException(WaitFailure);
        }
        public void Apply(string customExeBaseName)
        {
            Events.Add("apply");
            if (ApplyFailure != null) throw ApplyFailure;
            Applied = true;
            AppliedAlias = customExeBaseName;
        }
        public void Dispose() => Events.Add("dispose");
    }
}
