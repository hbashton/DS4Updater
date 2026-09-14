using DS4Updater;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class PortableCustomLaunchTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \r\n")]
    [DataRow("DS4Windows")]
    [DataRow("ds4windows")]
    public void DefaultConfigurationKeepsDefaultLaunch(string configured)
    {
        using var f = new Fixture(configured);
        Assert.AreEqual(f.Request, PortableWorkerSession.ResolveLaunchConfiguration(f.Request));
        PortableWorkerSession.ValidateLaunchConfiguration(f.Request);
    }

    [DataTestMethod]
    [DataRow("Game.Pad")]
    [DataRow("My Controller")]
    [DataRow("Contrôleur")]
    public void DefaultParentSelectsConfiguredAliasBeforeWorkerRequestIsBound(string name)
    {
        using var f = new Fixture(name);
        f.App(name + ".exe");
        var selected = PortableWorkerSession.ResolveLaunchConfiguration(f.Request);
        Assert.AreEqual(f.Request with { LaunchExe = name + ".exe", OriginalExe = "DS4Windows.exe" }, selected);
        Assert.AreEqual(name, selected.CustomExeBaseName);
        string worker = Path.Combine(f.Root, "Updates", "portable-worker-" + Guid.NewGuid().ToString("N"), "DS4Updater.exe");
        Assert.AreEqual(selected with { IsWorker = true }, PortableUpdateRequest.Parse(selected.WorkerArguments(), worker));
        PortableWorkerSession.ValidateLaunchConfiguration(selected);
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(f.Root, "DS4Windows.exe")),
            File.ReadAllBytes(Path.Combine(f.Root, name + ".exe")), "Selection must not rewrite either executable.");
        Assert.AreEqual(name, File.ReadAllText(f.Config));
    }

    [TestMethod]
    public void WorkerCannotIgnoreOrRetargetAChangedCustomPreference()
    {
        using var f = new Fixture("Chosen");
        f.App("Chosen.exe");
        f.App("Other.exe");
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateLaunchConfiguration(f.Request));
        var selected = PortableWorkerSession.ResolveLaunchConfiguration(f.Request);
        File.WriteAllText(f.Config, "Other");
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ValidateLaunchConfiguration(selected));
        Assert.ThrowsException<InvalidOperationException>(() => PortableWorkerSession.ResolveLaunchConfiguration(selected));
        Assert.ThrowsException<InvalidOperationException>(() => PortableWorkerSession.ResolveLaunchConfiguration(selected with { IsWorker = true }));
    }

    [TestMethod]
    public void ChangedOrClearedNameUsesOldIdentityUntilTransactionCreatesDestination()
    {
        using var f = new Fixture("Chosen");
        var selected = PortableWorkerSession.ResolveLaunchConfiguration(f.Request);
        Assert.AreEqual("Chosen.exe", selected.LaunchExe);
        Assert.AreEqual("DS4Windows.exe", selected.InstalledExe);
        PortableWorkerSession.ValidateLaunchConfiguration(selected, beforeUpdate: true);
        Assert.ThrowsException<FileNotFoundException>(() => PortableWorkerSession.ValidateLaunchConfiguration(selected));
        f.App("Manual.exe");
        selected = PortableWorkerSession.ResolveLaunchConfiguration(f.Request with { LaunchExe = "Manual.exe" });
        Assert.AreEqual("Manual.exe", selected.InstalledExe);
        Assert.AreEqual("Chosen.exe", selected.LaunchExe);
        File.Delete(f.Config);
        File.Delete(Path.Combine(f.Root, "DS4Windows.exe"));
        selected = PortableWorkerSession.ResolveLaunchConfiguration(f.Request with { LaunchExe = "Manual.exe" });
        Assert.AreEqual("DS4Windows.exe", selected.LaunchExe);
        Assert.AreEqual("Manual.exe", selected.InstalledExe);
        PortableWorkerSession.ValidateLaunchConfiguration(selected, beforeUpdate: true);
        // After a successful transaction the old managed alias can be gone;
        // launch validates only the new apphost and still-current preference.
        f.App("DS4Windows.exe");
        File.Delete(Path.Combine(f.Root, "Manual.exe"));
        PortableWorkerSession.ValidateLaunchConfiguration(selected);
    }

    [TestMethod]
    public void LauncherCannotSupplyWorkerOnlyOriginalNameOrRetargetItsProcess()
    {
        using var f = new Fixture(null);
        string[] arguments = { PortableUpdateRequest.PortableFlag, "--parentPid", "123", "--parentStartUtcTicks", "638000000000000000",
            "--releaseTag", "VIIPERRC4.6.1", "--launchExe", "DS4Windows.exe", "--originalExe", "Other.exe" };
        Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(arguments, Path.Combine(f.Root, "DS4Updater.exe")));
        foreach (string invalid in new[] { "", "../other.exe", "viiper.exe" })
        {
            var request = f.Request with { OriginalExe = invalid };
            string path = Path.Combine(f.Root, "Updates", "portable-worker-" + Guid.NewGuid().ToString("N"), "DS4Updater.exe");
            Assert.ThrowsException<ArgumentException>(() => PortableUpdateRequest.Parse(request.WorkerArguments(), path));
        }
    }

    [DataTestMethod]
    [DataRow("../outside")]
    [DataRow("nested/name")]
    [DataRow("DS4Updater")]
    [DataRow("viiper")]
    [DataRow("CON")]
    public void UnsafeConfiguredNamesNeverBecomeAWorkerTarget(string name)
    {
        using var f = new Fixture(name);
        Assert.ThrowsException<ArgumentException>(() => PortableWorkerSession.ResolveLaunchConfiguration(f.Request));
        Assert.AreEqual(name, File.ReadAllText(f.Config));
        Assert.AreEqual(2, Directory.GetFiles(f.Root).Length);
    }

    [TestMethod]
    public void OversizedAndDirectoryConfigurationFailWithoutChangingFiles()
    {
        using var f = new Fixture(new string('x', 513));
        Assert.ThrowsException<InvalidDataException>(() => PortableWorkerSession.ResolveLaunchConfiguration(f.Request));
        File.Delete(f.Config);
        Directory.CreateDirectory(f.Config);
        Assert.ThrowsException<UnauthorizedAccessException>(() => PortableWorkerSession.ResolveLaunchConfiguration(f.Request));
        Assert.IsTrue(File.Exists(Path.Combine(f.Root, "DS4Windows.exe")));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ds4updater-custom-launch-" + Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(Root, "custom_exe_name.txt");
        internal PortableUpdateRequest Request => new(Root, 123, 638000000000000000, "VIIPERRC4.6.1", "DS4Windows.exe", false);
        internal Fixture(string configuration)
        {
            Directory.CreateDirectory(Root);
            App("DS4Windows.exe");
            if (configuration != null) File.WriteAllText(Config, configuration);
        }
        internal void App(string name) => File.WriteAllText(Path.Combine(Root, name), "fixture app bytes; never executed");
        public void Dispose() => Directory.Delete(Root, true);
    }
}
