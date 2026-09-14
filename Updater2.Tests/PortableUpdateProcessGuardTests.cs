using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public class PortableUpdateProcessGuardTests
{
    private const string Root = @"C:\Controller Tests\Portable DS4Windows";
    private const long ParentStart = 638900000000000000;

    [TestMethod]
    public async Task EmptySnapshotReturnsVerifiedTargetAndRevalidatesBeforeApply()
    {
        var host = new FakeHost();
        int validations = 0;
        var guard = new PortableUpdateProcessGuard(host, root => { validations++; return root; });
        Assert.AreEqual(Root, await guard.WaitForQuiescenceAsync(Root));
        Assert.AreEqual(2, validations);
        CollectionAssert.AreEquivalent(new[] { "DS4Windows", "viiper", "HidGuardHelper" }, host.RequestedNames.ToArray());
        Assert.AreEqual(0, host.Delays);
    }

    [TestMethod]
    public async Task ExactTargetAppsAreWaitedForWithoutAStopOperation()
    {
        var host = new FakeHost
        {
            Read = count => count == 1 ? new[]
            {
                Live(1, "DS4Windows.exe"), Live(2, "viiper.exe"), Live(3, "HidGuardHelper.exe"),
                Live(4, @"extras\viiper.exe"), Live(5, "My Controller.exe"),
            } : Array.Empty<PortableUpdateProcessObservation>()
        };
        var guard = new PortableUpdateProcessGuard(host, root => root);
        Assert.AreEqual(Root, await guard.WaitForQuiescenceAsync(Root, "My Controller.exe"));
        Assert.AreEqual(1, host.Delays);
        Assert.AreEqual(2, host.Reads);
        Assert.IsTrue(host.RequestedNames.Contains("My Controller"));
        // The injected boundary has no termination or close-window capability.
        Assert.IsFalse(typeof(IPortableUpdateProcessHost).GetMethods().Any(method =>
            method.Name.Contains("Kill") || method.Name.Contains("Stop") || method.Name.Contains("Close")));
    }

    [TestMethod]
    public async Task OtherPortableAndInstalledCopiesAreNotWaitedFor()
    {
        var host = new FakeHost
        {
            Read = _ => new[]
            {
                new PortableUpdateProcessObservation(1, ParentStart, @"C:\Program Files\DS4Windows\DS4Windows.exe"),
                new PortableUpdateProcessObservation(2, ParentStart, @"D:\Other portable\viiper.exe"),
                new PortableUpdateProcessObservation(3, ParentStart, Root + @"-Other\viiper.exe"),
            }
        };
        Assert.AreEqual(Root, await new PortableUpdateProcessGuard(host, root => root).WaitForQuiescenceAsync(Root));
        Assert.AreEqual(0, host.Delays);
    }

    [TestMethod]
    public async Task PersistentLocalBorrowedBrokerGivesCloseAppsMessageAtBoundedDeadline()
    {
        var host = new FakeHost { Read = _ => new[] { Live(2, "viiper.exe") } };
        var error = await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() =>
            new PortableUpdateProcessGuard(host, root => root).WaitForQuiescenceAsync(Root, timeout: TimeSpan.FromMilliseconds(600)));
        StringAssert.Contains(error.Message, "Close DS4Windows, VIIPER and HidGuardHelper");
        StringAssert.Contains(error.Message, "No processes were stopped");
        Assert.AreEqual(600L, host.TimestampMilliseconds);
        Assert.AreEqual(3, host.Delays);
    }

    [TestMethod]
    public async Task UnreadableCandidateFailsClosedEvenIfItMightBeAnotherCopy()
    {
        var host = new FakeHost { Read = _ => new[] { new PortableUpdateProcessObservation(9, 0, null, false) } };
        var error = await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() =>
            new PortableUpdateProcessGuard(host, root => root).WaitForQuiescenceAsync(Root));
        StringAssert.Contains(error.Message, "identity could not be verified");
        Assert.AreEqual(0, host.Delays);
    }

    [TestMethod]
    public async Task QueryFailureIsNotTreatedAsAnEmptyProcessList()
    {
        var host = new FakeHost { Read = _ => throw new UnauthorizedAccessException() };
        await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() =>
            new PortableUpdateProcessGuard(host, root => root).WaitForQuiescenceAsync(Root));
    }

    [TestMethod]
    public async Task UnboundedMetadataProviderCannotHoldTheUpdaterBeyondItsDeadline()
    {
        var host = new FakeHost { PendingRead = new TaskCompletionSource<IReadOnlyList<PortableUpdateProcessObservation>>().Task };
        var error = await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() =>
            new PortableUpdateProcessGuard(host, root => root).WaitForQuiescenceAsync(Root, timeout: TimeSpan.FromMilliseconds(30)));
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
    }

    [TestMethod]
    public async Task ParentPidReuseDoesNotWaitForAnUnrelatedReplacement()
    {
        var host = new FakeHost { Read = _ => new[] { new PortableUpdateProcessObservation(7, ParentStart + 1, @"C:\Windows\notepad.exe") } };
        Assert.AreEqual(Root, await new PortableUpdateProcessGuard(host, root => root)
            .WaitForQuiescenceAsync(Root, parentProcessId: 7, parentStartUtcTicks: ParentStart));
        Assert.AreEqual(7, host.RequestedParent);
        Assert.AreEqual(0, host.Delays);
    }

    [TestMethod]
    public async Task ReusedParentPidStillBlocksIfItsOwnImageUsesTargetFiles()
    {
        var host = new FakeHost { Read = _ => new[] { Live(7, "DS4Windows.exe") with { StartTimeUtcTicks = ParentStart + 1 } } };
        await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() => new PortableUpdateProcessGuard(host, root => root)
            .WaitForQuiescenceAsync(Root, parentProcessId: 7, parentStartUtcTicks: ParentStart, timeout: TimeSpan.FromMilliseconds(1)));
        Assert.AreEqual(1L, host.TimestampMilliseconds);
    }

    [TestMethod]
    public async Task MatchingParentIdentityOutsideTargetIsRejected()
    {
        var host = new FakeHost { Read = _ => new[] { new PortableUpdateProcessObservation(7, ParentStart, @"D:\Other portable\DS4Windows.exe") } };
        var error = await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() => new PortableUpdateProcessGuard(host, root => root)
            .WaitForQuiescenceAsync(Root, parentProcessId: 7, parentStartUtcTicks: ParentStart));
        StringAssert.Contains(error.Message, "initiating DS4Windows process does not belong");
    }

    [TestMethod]
    public async Task MatchingParentIdentityWaitsForItsNormalExit()
    {
        var host = new FakeHost { Read = count => count == 1 ? new[] { Live(7, "Custom.exe") } : Array.Empty<PortableUpdateProcessObservation>() };
        await new PortableUpdateProcessGuard(host, root => root)
            .WaitForQuiescenceAsync(Root, "Custom.exe", 7, ParentStart);
        Assert.AreEqual(1, host.Delays);
    }

    [TestMethod]
    public async Task NameChangeWaitsForOldNewAndCanonicalApphostsWithoutStoppingAnything()
    {
        var host = new FakeHost { Read = count => count == 1 ? new[]
        {
            Live(7, "Old Pad.exe"), Live(8, "New Pad.exe"), Live(9, "DS4Windows.exe"),
        } : Array.Empty<PortableUpdateProcessObservation>() };
        await new PortableUpdateProcessGuard(host, root => root).WaitForQuiescenceAsync(
            Root, "New Pad.exe", 7, ParentStart, originalExeName: "Old Pad.exe");
        Assert.IsTrue(host.RequestedNames.Contains("Old Pad"));
        Assert.IsTrue(host.RequestedNames.Contains("New Pad"));
        Assert.IsTrue(host.RequestedNames.Contains("DS4Windows"));
        Assert.AreEqual(1, host.Delays);
    }

    [DataTestMethod]
    [DataRow("New Pad.exe")]
    [DataRow("DS4Windows.exe")]
    public async Task ParentPidCannotAuthorizeADifferentApphostDuringNameChange(string actual)
    {
        var host = new FakeHost { Read = _ => new[] { Live(7, actual) } };
        await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() =>
            new PortableUpdateProcessGuard(host, root => root).WaitForQuiescenceAsync(
                Root, "New Pad.exe", 7, ParentStart, originalExeName: "Old Pad.exe"));
        Assert.AreEqual(0, host.Delays);
    }

    [TestMethod]
    public async Task MissingParentAlreadyExitedDoesNotPreventAnOtherwiseQuietUpdate()
    {
        var host = new FakeHost();
        await new PortableUpdateProcessGuard(host, root => root)
            .WaitForQuiescenceAsync(Root, parentProcessId: 7, parentStartUtcTicks: ParentStart);
        Assert.AreEqual(0, host.Delays);
    }

    [TestMethod]
    public async Task CancellationDoesNotBecomeSuccessOrGetWrappedAsAnAppError()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new FakeHost { Read = _ => new[] { Live(7, "viiper.exe") }, OnDelay = cancellation.Cancel };
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => new PortableUpdateProcessGuard(host, root => root)
            .WaitForQuiescenceAsync(Root, cancellationToken: cancellation.Token));
        Assert.AreEqual(1, host.Reads);
    }

    [TestMethod]
    public async Task ChangedRootAfterWaitingCannotAuthorizeTheTransaction()
    {
        int validations = 0;
        var guard = new PortableUpdateProcessGuard(new FakeHost(), root => ++validations == 1 ? root : @"D:\DifferentFolder");
        await Assert.ThrowsExceptionAsync<PortableUpdateProcessGuardException>(() => guard.WaitForQuiescenceAsync(Root));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(30001)]
    public async Task InvalidWaitBudgetsNeverInspectProcesses(int milliseconds)
    {
        var host = new FakeHost();
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => new PortableUpdateProcessGuard(host, root => root)
            .WaitForQuiescenceAsync(Root, timeout: TimeSpan.FromMilliseconds(milliseconds)));
        Assert.AreEqual(0, host.Reads);
    }

    [TestMethod]
    public async Task PartialOrInvalidParentIdentityIsRejectedBeforeInspection()
    {
        var host = new FakeHost();
        var guard = new PortableUpdateProcessGuard(host, root => root);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => guard.WaitForQuiescenceAsync(Root, parentProcessId: 7));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => guard.WaitForQuiescenceAsync(Root, parentStartUtcTicks: ParentStart));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => guard.WaitForQuiescenceAsync(Root, parentProcessId: 0, parentStartUtcTicks: ParentStart));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => guard.WaitForQuiescenceAsync(Root, parentProcessId: 7, parentStartUtcTicks: -1));
        Assert.AreEqual(0, host.Reads);
    }

    [DataTestMethod]
    [DataRow(@"..\other.exe")]
    [DataRow(@"C:\other.exe")]
    [DataRow("other.exe:stream")]
    [DataRow("other.exe ")]
    [DataRow("other.dll")]
    [DataRow("viiper.exe")]
    [DataRow("DS4Updater.exe")]
    [DataRow("CON.exe")]
    [DataRow("LPT1.exe")]
    public void AliasesCannotEscapeTargetOrMasqueradeAsHelpers(string alias) =>
        Assert.ThrowsException<ArgumentException>(() => PortableUpdateProcessGuard.ValidateCustomExeName(alias));

    [DataTestMethod]
    [DataRow(@"relative\folder")]
    [DataRow(@"\\server\share\folder")]
    [DataRow(@"\\?\C:\folder")]
    [DataRow(@"C:\folder:stream")]
    [DataRow(@"C:\folder\..\other")]
    [DataRow(@"C:\folder.\other")]
    [DataRow(@"C:\folder \other")]
    public void AmbiguousOrNonlocalPathsAreRejected(string path) =>
        Assert.ThrowsException<InvalidDataException>(() => PortableUpdateProcessGuard.NormalizeLocalPath(path));

    [TestMethod]
    public void MarkerAndCanonicalFolderAreBothRequired()
    {
        using var folder = new TestFolder();
        Assert.AreEqual(folder.Path, Validate(folder.Path));
        File.WriteAllText(System.IO.Path.Combine(folder.Path, PortableUpdateProcessGuard.MarkerFileName), "not portable");
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path));
        File.Delete(System.IO.Path.Combine(folder.Path, PortableUpdateProcessGuard.MarkerFileName));
        Assert.ThrowsException<FileNotFoundException>(() => Validate(folder.Path));
    }

    [TestMethod]
    public void MarkerCannotBeDirectoryOrOversizedFile()
    {
        using var folder = new TestFolder();
        string marker = System.IO.Path.Combine(folder.Path, PortableUpdateProcessGuard.MarkerFileName);
        File.WriteAllText(marker, new string('x', 129));
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path));
        File.Delete(marker);
        Directory.CreateDirectory(marker);
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path));
    }

    [TestMethod]
    public void MarkerDoesNotOverrideManagedOrBroadFolderBoundaries()
    {
        using var folder = new TestFolder();
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path, managed: new[] { folder.Path }));
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path, managed: new[] { System.IO.Path.GetDirectoryName(folder.Path) }));
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path, managed: new[] { System.IO.Path.Combine(folder.Path, "ManagedInstall") }));
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path, protectedRoots: new[] { System.IO.Path.GetDirectoryName(folder.Path) }));
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path, broad: new[] { folder.Path }));
        Assert.ThrowsException<InvalidDataException>(() => Validate(System.IO.Path.GetPathRoot(folder.Path)));
    }

    [TestMethod]
    public void ProtectedPathCheckUsesDirectoryBoundariesNotPrefixes()
    {
        using var folder = new TestFolder();
        Assert.AreEqual(folder.Path, Validate(folder.Path, protectedRoots: new[] { folder.Path + "Other" }));
    }

    [TestMethod]
    public void ResolvedDirectoryCannotBypassProtectedRootUsingAShortName()
    {
        using var folder = new TestFolder();
        string managed = @"C:\Program Files\DS4Windows";
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path,
            protectedRoots: new[] { @"C:\Program Files" }, resolve: _ => managed));
    }

    [TestMethod]
    public void ReparseInspectionFailureIsNotSwallowed()
    {
        using var folder = new TestFolder();
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path,
            inspect: _ => throw new InvalidDataException("Link rejected")));
    }

    [TestMethod]
    public void RootAndMarkerBothPassThroughReparseInspection()
    {
        using var folder = new TestFolder();
        var inspected = new List<string>();
        Validate(folder.Path, inspect: inspected.Add);
        Assert.IsTrue(inspected.Contains(folder.Path));
        Assert.IsTrue(inspected.Contains(System.IO.Path.Combine(folder.Path, PortableUpdateProcessGuard.MarkerFileName)));
    }

    [TestMethod]
    public void InvalidRegisteredRootCannotSilentlyBecomePortableAuthority()
    {
        using var folder = new TestFolder();
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path, managed: new[] { @"\\server\DS4Windows" }));
        Assert.ThrowsException<InvalidDataException>(() => Validate(folder.Path, managed: new[] { "relative" }));
    }

    private static string Validate(string root, string[] managed = null, string[] protectedRoots = null,
        string[] broad = null, Func<string, string> resolve = null, Action<string> inspect = null) =>
        PortableUpdateProcessGuard.ValidateTargetRootCore(root, managed ?? Array.Empty<string>(),
            protectedRoots ?? Array.Empty<string>(), broad ?? Array.Empty<string>(), resolve ?? (path => path),
            inspect ?? PortableUpdateProcessGuard.ValidateNoReparsePoints);

    private static PortableUpdateProcessObservation Live(int pid, string relative) => new(pid, ParentStart, System.IO.Path.Combine(Root, relative));

    private sealed class FakeHost : IPortableUpdateProcessHost
    {
        public long TimestampMilliseconds { get; private set; }
        internal Func<int, IReadOnlyList<PortableUpdateProcessObservation>> Read = _ => Array.Empty<PortableUpdateProcessObservation>();
        internal Task<IReadOnlyList<PortableUpdateProcessObservation>> PendingRead;
        internal Action OnDelay;
        internal int Reads, Delays;
        internal IReadOnlyCollection<string> RequestedNames;
        internal int? RequestedParent;
        public Task<IReadOnlyList<PortableUpdateProcessObservation>> SnapshotAsync(IReadOnlyCollection<string> names, int? parentProcessId, CancellationToken cancellationToken)
        {
            Reads++;
            RequestedNames = names;
            RequestedParent = parentProcessId;
            return PendingRead ?? Task.FromResult(Read(Reads));
        }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays++;
            TimestampMilliseconds += (long)delay.TotalMilliseconds;
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestFolder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DS4Updater-guard-tests-" + Guid.NewGuid().ToString("N"));
        internal TestFolder()
        {
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, PortableUpdateProcessGuard.MarkerFileName), PortableUpdateProcessGuard.MarkerText + "\r\n");
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
