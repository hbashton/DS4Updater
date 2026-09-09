using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class PortablePackageTransactionTests
{
    [TestMethod]
    public void PrepareStagesVerifiedBytesWithoutChangingLiveFilesAndApplyPreservesUserData()
    {
        using var f = new Fixture();
        f.OwnOldFile("old-only.dll", "obsolete owned bytes");
        f.WriteUser("Profiles/Default.xml", "profile");
        f.WriteUser("Actions.xml", "actions");
        f.WriteUser("settings.xml", "settings");
        f.WriteUser("LinkedProfiles.xml", "links");
        f.WriteUser("portable-data/VIIPER/viiper.key.txt", "private key fixture");
        f.WriteUser("Logs/session.txt", "log");
        f.WriteUser("Plugins/custom.dll", "plugin");
        f.WriteUser("unowned.dll", "not ours");
        f.Payload["new-libs/new.dll"] = Bytes("new dependency");
        f.BuildArchive();
        Dictionary<string, string> before = f.LiveSnapshot();
        using (var plan = f.Prepare())
        {
            Assert.IsTrue(Directory.Exists(plan.StagedRoot));
            AssertSnapshot(before, f.LiveSnapshot());
            plan.Apply();
            foreach (var item in f.Payload)
                CollectionAssert.AreEqual(item.Value, File.ReadAllBytes(Path.Combine(f.Target, item.Key)));
            Assert.IsFalse(File.Exists(Path.Combine(f.Target, "old-only.dll")));
            foreach (var user in f.UserFiles) Assert.AreEqual(user.Value, File.ReadAllText(Path.Combine(f.Target, user.Key)));
            Assert.IsFalse(plan.RecoveryRequired);
        }
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(f.Target, "Updates"), "portable-update-*").Length);
    }

    [TestMethod]
    public void ArchiveDigestMismatchDoesNotEvenCreateUpdatesDirectory()
    {
        using var f = new Fixture();
        Assert.ThrowsException<InvalidDataException>(() => PortablePackageTransaction.Prepare(
            f.Target, f.Archive, new string('0', 64), Fixture.Tag));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Target, "Updates")));
    }

    [DataTestMethod]
    [DataRow("../escape.dll")]
    [DataRow("folder/../escape.dll")]
    [DataRow("C:/escape.dll")]
    [DataRow("file.dll:stream")]
    [DataRow("trailing.")]
    [DataRow("trailing ")]
    [DataRow("aux.txt")]
    [DataRow("COM1.dll")]
    [DataRow("Profiles/Default.xml")]
    [DataRow("Plugins/custom.dll")]
    [DataRow("portable-data/VIIPER/viiper.key.txt")]
    [DataRow("Actions.xml")]
    [DataRow("settings.xml")]
    [DataRow("custom_exe_name.txt")]
    [DataRow("Updates/owned.dll")]
    public void UnsafeOrUserOwnedIncomingPathsAreRejectedBeforeStaging(string path)
    {
        using var f = new Fixture();
        f.Payload[path] = Bytes("forbidden");
        f.BuildArchive();
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Target, "Updates")));
    }

    [DataTestMethod]
    [DataRow("extra")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("self")]
    [DataRow("case")]
    public void OwnershipManifestMustMatchEveryPayloadExactly(string error)
    {
        using var f = new Fixture();
        string manifest = f.ManifestText();
        if (error == "extra") manifest += "not-present.dll\n";
        if (error == "missing") manifest = manifest.Replace("DS4Windows.dll\n", "");
        if (error == "duplicate") manifest += "ds4windows.dll\n";
        if (error == "self") manifest += PortablePackageTransaction.ManifestName + "\n";
        if (error == "case") manifest = manifest.Replace("DS4Windows.dll", "DS4WINDOWS.dll");
        f.BuildArchive(manifest);
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
    }

    [TestMethod]
    public void DifferentlyCasedImplicitDirectoriesAreRejectedEvenWithoutDuplicateFiles()
    {
        using var f = new Fixture();
        f.Payload["Library/a.dll"] = Bytes("a");
        f.Payload["library/b.dll"] = Bytes("b");
        f.BuildArchive();
        var error = Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
        StringAssert.Contains(error.Message, "casing");
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Target, "Updates")));
    }

    [TestMethod]
    public void ExistingOwnershipManifestAlsoRejectsConflictingDirectoryCasing()
    {
        using var f = new Fixture();
        File.AppendAllText(Path.Combine(f.Target, PortablePackageTransaction.ManifestName), "Library/a.dll\nlibrary/b.dll\n");
        var error = Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
        StringAssert.Contains(error.Message, "casing");
    }

    [DataTestMethod]
    [DataRow("Other/file.dll", 0)]
    [DataRow("DS4Windows/DS4WINDOWS.dll", 0)]
    [DataRow("DS4Windows/link.dll", unchecked((int)0xA1FF0000))]
    [DataRow("DS4Windows/reparse.dll", (int)FileAttributes.ReparsePoint)]
    [DataRow("DS4Windows\\backslash.dll", 0)]
    [DataRow("DS4Windows/DS4Windows.dll/child.bin", 0)]
    public void ArchiveRootsCaseCollisionsLinksAndFileDirectoryCollisionsAreRejected(string rawPath, int attributes)
    {
        using var f = new Fixture();
        f.BuildArchive(extraRawPath: rawPath, externalAttributes: attributes);
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
    }

    [DataTestMethod]
    [DataRow("DS4Windows.exe")]
    [DataRow("DS4Windows.dll")]
    [DataRow("DS4Windows.runtimeconfig.json")]
    [DataRow("DS4Windows.deps.json")]
    [DataRow("DS4Windows.release")]
    [DataRow("DS4Windows.portable")]
    [DataRow("viiper.exe")]
    [DataRow("viiper.exe.sha256")]
    [DataRow(Fixture.ExtrasBroker)]
    [DataRow(Fixture.ExtrasBroker + ".sha256")]
    public void RequiredPackageComponentsCannotBeOmitted(string missing)
    {
        using var f = new Fixture();
        f.Payload.Remove(missing);
        f.BuildArchive();
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
    }

    [DataTestMethod]
    [DataRow("DS4Windows.release", "VIIPERRC4.4")]
    [DataRow("DS4Windows.portable", "not a portable package")]
    [DataRow("viiper.exe", "substituted binary")]
    [DataRow("viiper.exe.sha256", "not a digest")]
    [DataRow(Fixture.ExtrasBroker, "different extras broker")]
    [DataRow(Fixture.ExtrasBroker + ".sha256", "not a digest")]
    public void SelectedIdentityAndBothBrokerPinsAreVerified(string path, string changed)
    {
        using var f = new Fixture();
        f.Payload[path] = Bytes(changed);
        f.BuildArchive();
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
        Assert.AreEqual("old app", File.ReadAllText(Path.Combine(f.Target, "DS4Windows.exe")));
    }

    [TestMethod]
    public void PublishedChecksumLinesBindBothDigestAndExactFilename()
    {
        string hash = new string('a', 64);
        Assert.AreEqual(hash.ToUpperInvariant(), PortablePackageTransaction.ParseChecksumSidecar(hash + " *viiper.exe\n", "viiper.exe"));
        Assert.AreEqual(hash.ToUpperInvariant(), PortablePackageTransaction.ParseChecksumSidecar(hash + "  viiper.exe\r\n", "viiper.exe"));
        Assert.AreEqual(hash.ToUpperInvariant(), PortablePackageTransaction.ParseChecksumSidecar(hash, "viiper.exe"));
        Assert.AreEqual(hash.ToUpperInvariant(), PortablePackageTransaction.ParseChecksumSidecar("sha256:" + hash, "viiper.exe"));
        foreach (string invalid in new[] { hash + " *other.exe", hash + " *../viiper.exe", hash + " *viiper.exe\n" + hash,
                     hash + " *VIIPER.exe", hash + " garbage", hash + " *" })
            Assert.ThrowsException<InvalidDataException>(() => PortablePackageTransaction.ParseChecksumSidecar(invalid, "viiper.exe"));
    }

    [TestMethod]
    public void ExcessiveCompressionRatioIsRejectedBeforeExtraction()
    {
        using var f = new Fixture();
        f.Payload["compressed.bin"] = new byte[12 * 1024 * 1024];
        f.BuildArchive();
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Target, "Updates")));
    }

    [TestMethod]
    public void InvalidTargetMarkerMissingOwnershipAndBroadRootsFailClosed()
    {
        using var f = new Fixture();
        Assert.ThrowsException<InvalidDataException>(() => PortablePackageTransaction.ValidateTargetRoot(Path.GetPathRoot(f.Target)));
        Assert.ThrowsException<InvalidDataException>(() => PortablePackageTransaction.ValidateTargetRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.ThrowsException<InvalidDataException>(() => PortablePackageTransaction.ValidateTargetRoot("\\\\server\\share\\app"));
        File.WriteAllText(Path.Combine(f.Target, PortablePackageTransaction.MarkerName), "wrong");
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
        File.WriteAllText(Path.Combine(f.Target, PortablePackageTransaction.MarkerName), PortablePackageTransaction.MarkerText);
        File.Delete(Path.Combine(f.Target, PortablePackageTransaction.ManifestName));
        Assert.ThrowsException<FileNotFoundException>(() => f.Prepare());
    }

    [TestMethod]
    public void PriorOwnershipCannotAuthorizeDeletingUserFiles()
    {
        using var f = new Fixture();
        f.WriteUser("Profiles/Default.xml", "keep");
        File.AppendAllText(Path.Combine(f.Target, PortablePackageTransaction.ManifestName), "Profiles/Default.xml\n");
        Assert.ThrowsException<InvalidDataException>(() => f.Prepare());
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(f.Target, "Profiles/Default.xml")));
    }

    [TestMethod]
    public void IncomingCollisionWithAnUnownedFileDoesNotOverwriteIt()
    {
        using var f = new Fixture();
        f.Payload["plugin-independent.dll"] = Bytes("package");
        f.BuildArchive();
        f.WriteUser("plugin-independent.dll", "user");
        Dictionary<string, string> before = f.LiveSnapshot();
        using var plan = f.Prepare();
        Assert.ThrowsException<IOException>(() => plan.Apply());
        AssertSnapshot(before, f.LiveSnapshot());
    }

    [DataTestMethod]
    [DataRow("viiper.exe")]
    [DataRow("DS4Windows.exe")]
    [DataRow("DS4Updater.exe")]
    [DataRow("old-only.dll")]
    public void AnyLockedChangedOrStaleExecutablePreventsAllLiveMutation(string locked)
    {
        using var f = new Fixture();
        if (locked is "DS4Updater.exe" or "old-only.dll") f.OwnOldFile(locked, "locked old file");
        if (locked == "DS4Updater.exe") { f.Payload[locked] = Bytes("updated updater"); f.BuildArchive(); }
        Dictionary<string, string> before = f.LiveSnapshot();
        using var plan = f.Prepare();
        using (var held = new FileStream(Path.Combine(f.Target, locked), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsException<IOException>(() => plan.Apply());
        AssertSnapshot(before, f.LiveSnapshot());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChangedStageOrOwnershipIsRejectedBeforeLiveMutation(bool ownership)
    {
        using var f = new Fixture();
        using var plan = f.Prepare();
        if (ownership) File.AppendAllText(Path.Combine(f.Target, PortablePackageTransaction.ManifestName), "new-owner.dll\n");
        else File.WriteAllText(Path.Combine(plan.StagedRoot, "DS4Windows.dll"), "tampered");
        Dictionary<string, string> before = f.LiveSnapshot();
        if (ownership) Assert.ThrowsException<IOException>(() => plan.Apply());
        else Assert.ThrowsException<InvalidDataException>(() => plan.Apply());
        AssertSnapshot(before, f.LiveSnapshot());
    }

    [TestMethod]
    public void FailedReplacementRestoresOriginalFilesAndRemovesOnlyNewPackageFiles()
    {
        using var f = new Fixture();
        f.Payload["A-new.bin"] = Bytes("new file");
        f.BuildArchive();
        f.WriteUser("untouched.txt", "keep");
        Dictionary<string, string> before = f.LiveSnapshot();
        using var plan = f.Prepare();
        plan.BeforeMutationForTesting = (path, rollback) =>
        {
            if (!rollback && path == "DS4Windows.dll") throw new IOException("injected disk failure");
        };
        IOException failure = Assert.ThrowsException<IOException>(() => plan.Apply());
        StringAssert.Contains(failure.Message, "restored");
        Assert.IsFalse(plan.RecoveryRequired);
        AssertSnapshot(before, f.LiveSnapshot());
        StringAssert.Contains(File.ReadAllText(Path.Combine(plan.TransactionRoot, "transaction.json")), "rolled-back");
    }

    [TestMethod]
    public void NewlyAppearingUnownedDestinationIsNeverDeletedByRollback()
    {
        using var f = new Fixture();
        const string incoming = "A-new.bin";
        f.Payload[incoming] = Bytes("package bytes");
        f.BuildArchive();
        using var plan = f.Prepare();
        plan.BeforeMutationForTesting = (path, rollback) =>
        {
            if (!rollback && path == incoming)
                File.WriteAllText(Path.Combine(f.Target, incoming), "unrelated user file");
        };
        Assert.ThrowsException<IOException>(() => plan.Apply());
        Assert.AreEqual("unrelated user file", File.ReadAllText(Path.Combine(f.Target, incoming)));
        Assert.AreEqual("old app", File.ReadAllText(Path.Combine(f.Target, "DS4Windows.exe")));
        Assert.IsFalse(plan.RecoveryRequired, "No transaction-owned live mutation occurred.");
    }

    [TestMethod]
    public void ChangedExistingDestinationIsNotOverwrittenAfterItsPreflightLockIsReleased()
    {
        using var f = new Fixture();
        using var plan = f.Prepare();
        plan.BeforeMutationForTesting = (path, rollback) =>
        {
            if (!rollback && path == "DS4Windows.deps.json")
                File.WriteAllText(Path.Combine(f.Target, path), "a concurrent edit");
        };
        Assert.ThrowsException<IOException>(() => plan.Apply());
        Assert.AreEqual("a concurrent edit", File.ReadAllText(Path.Combine(f.Target, "DS4Windows.deps.json")));
        Assert.AreEqual("old app", File.ReadAllText(Path.Combine(f.Target, "DS4Windows.exe")));
        Assert.IsFalse(plan.RecoveryRequired);
    }

    [TestMethod]
    public void RollbackPreservesConcurrentEditsAndRetainsRecoveryInsteadOfDeletingThem()
    {
        using var f = new Fixture();
        const string incoming = "A-new.bin";
        f.Payload[incoming] = Bytes("package bytes");
        f.BuildArchive();
        var plan = f.Prepare();
        string recovery = plan.TransactionRoot;
        plan.BeforeMutationForTesting = (path, rollback) =>
        {
            if (!rollback && path == "DS4Windows.dll")
            {
                File.WriteAllText(Path.Combine(f.Target, incoming), "user edit after the update wrote it");
                throw new IOException("injected later failure");
            }
        };
        Assert.ThrowsException<IOException>(() => plan.Apply());
        Assert.IsTrue(plan.RecoveryRequired);
        Assert.AreEqual("user edit after the update wrote it", File.ReadAllText(Path.Combine(f.Target, incoming)));
        plan.Dispose();
        Assert.IsTrue(File.Exists(Path.Combine(recovery, "backup/DS4Windows.exe")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(recovery, "transaction.json")), "recovery-required");
    }

    [TestMethod]
    public void OwnershipSnapshotHashCoversExactBytesIncludingBomAndNewlineStyle()
    {
        using var f = new Fixture();
        string path = Path.Combine(f.Target, PortablePackageTransaction.ManifestName);
        byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Bytes("DS4Windows.exe\r\nDS4Windows.dll\r\n")).ToArray();
        File.WriteAllBytes(path, bytes);
        var snapshot = PortablePackageTransaction.ReadBoundedTextSnapshot(path, 1024);
        Assert.AreEqual("DS4Windows.exe\r\nDS4Windows.dll\r\n", snapshot.Text);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), snapshot.Sha256);
        Assert.ThrowsException<InvalidDataException>(() => PortablePackageTransaction.ReadBoundedTextSnapshot(path, 4));
    }

    [TestMethod]
    public void IncompleteRollbackKeepsVerifiedBackupJournalAndBlocksAnotherTransaction()
    {
        using var f = new Fixture();
        var plan = f.Prepare();
        string recovery = plan.TransactionRoot;
        plan.BeforeMutationForTesting = (path, rollback) =>
        {
            if (rollback || path == "DS4Windows.dll") throw new IOException("injected persistent disk failure");
        };
        IOException failure = Assert.ThrowsException<IOException>(() => plan.Apply());
        Assert.IsTrue(plan.RecoveryRequired);
        StringAssert.Contains(failure.Message, recovery);
        plan.Dispose();
        Assert.IsTrue(File.Exists(Path.Combine(recovery, "backup/DS4Windows.deps.json")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(recovery, "transaction.json")), "recovery-required");
        Assert.ThrowsException<IOException>(() => f.Prepare());
    }

    [TestMethod]
    public void OnlyOnePreparedTransactionCanOwnTheSamePortableRoot()
    {
        using var f = new Fixture();
        using var first = f.Prepare();
        Assert.ThrowsException<IOException>(() => f.Prepare());
        Assert.IsTrue(Directory.Exists(first.StagedRoot));
    }

    [TestMethod]
    public void ValidatedExistingCustomNameReceivesExactAliasesWithoutChangingItsConfiguration()
    {
        using var f = new Fixture();
        const string name = "Game.Pad";
        f.WriteUser("custom_exe_name.txt", name + "\r\n");
        f.WriteUser(name + ".exe", "old alias");
        using var plan = f.Prepare();
        plan.Apply(name);
        foreach (string suffix in new[] { ".exe", ".runtimeconfig.json", ".deps.json" })
            CollectionAssert.AreEqual(f.Payload["DS4Windows" + suffix], File.ReadAllBytes(Path.Combine(f.Target, name + suffix)));
        Assert.AreEqual(name + "\r\n", File.ReadAllText(Path.Combine(f.Target, "custom_exe_name.txt")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(f.Target, PortablePackageTransaction.ManifestName)), name + ".exe\n");
    }

    [DataTestMethod]
    [DataRow("../outside")]
    [DataRow("viiper")]
    [DataRow("DS4Updater")]
    [DataRow("not-configured")]
    public void UnsafeOrUnconfirmedAliasesCannotTouchLiveFiles(string requested)
    {
        using var f = new Fixture();
        f.WriteUser("custom_exe_name.txt", "GamePad");
        Dictionary<string, string> before = f.LiveSnapshot();
        using var plan = f.Prepare();
        Assert.ThrowsException<InvalidDataException>(() => plan.Apply(requested));
        AssertSnapshot(before, f.LiveSnapshot());
    }

    [TestMethod]
    public void ReparseTargetAndStagingComponentsAreRejected()
    {
        using var f = new Fixture();
        string link = Path.Combine(f.Root, "linked-app");
        try { Directory.CreateSymbolicLink(link, f.Target); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("This runner cannot create an isolated filesystem link."); }
        try { Assert.ThrowsException<IOException>(() => PortablePackageTransaction.ValidateTargetRoot(link)); }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
        using var plan = f.Prepare();
        string stage = Path.Combine(plan.StagedRoot, "DS4Windows.dll");
        File.Delete(stage);
        try { File.CreateSymbolicLink(stage, Path.Combine(f.Target, "DS4Windows.dll")); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("This runner cannot create an isolated file link."); }
        try { Assert.ThrowsException<IOException>(() => plan.Apply()); }
        finally { File.Delete(stage); }
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static void AssertSnapshot(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var item in expected) Assert.AreEqual(item.Value, actual[item.Key], item.Key);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Tag = "VIIPERRC4.5.2";
        internal const string ExtrasBroker = "extras/VIIPER-0.1.3-rc4.5-x64.exe";
        internal readonly string Root, Target, Archive;
        internal readonly Dictionary<string, byte[]> Payload = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> UserFiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> oldOwnership = new(StringComparer.Ordinal);
        private string archiveHash;

        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "ds4w-portable-transaction-tests-" + Guid.NewGuid().ToString("N"));
            Target = Path.Combine(Root, "app");
            Archive = Path.Combine(Root, "package.zip");
            Directory.CreateDirectory(Target);
            byte[] broker = Bytes("verified broker fixture, never executable");
            string brokerHash = Convert.ToHexString(SHA256.HashData(broker));
            foreach (var item in new Dictionary<string, string>
            {
                ["DS4Windows.exe"] = "new app", ["DS4Windows.dll"] = "new assembly",
                ["DS4Windows.runtimeconfig.json"] = "{\"runtimeOptions\":{}}", ["DS4Windows.deps.json"] = "{}",
                ["DS4Windows.release"] = Tag, [PortablePackageTransaction.MarkerName] = PortablePackageTransaction.MarkerText,
                ["viiper.exe.sha256"] = brokerHash + " *viiper.exe\n",
                [ExtrasBroker + ".sha256"] = brokerHash + " *" + Path.GetFileName(ExtrasBroker) + "\n",
            }) Payload.Add(item.Key, Bytes(item.Value));
            Payload.Add("viiper.exe", broker);
            Payload.Add(ExtrasBroker, broker);
            foreach (var item in Payload)
            {
                string destination = Path.Combine(Target, item.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, item.Key == PortablePackageTransaction.MarkerName ? item.Value : Bytes("old app"));
                oldOwnership.Add(item.Key);
            }
            WriteOldManifest();
            BuildArchive();
        }

        internal void OwnOldFile(string relative, string contents)
        {
            WriteUser(relative, contents);
            UserFiles.Remove(relative);
            oldOwnership.Add(relative);
            WriteOldManifest();
        }
        internal void WriteUser(string relative, string contents)
        {
            string path = Path.Combine(Target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
            UserFiles[relative] = contents;
        }
        private void WriteOldManifest() => File.WriteAllText(Path.Combine(Target, PortablePackageTransaction.ManifestName),
            string.Join("\n", oldOwnership.Order()) + "\n");
        internal string ManifestText() => string.Join("\n", Payload.Keys.Order()) + "\n";
        internal void BuildArchive(string manifest = null, string extraRawPath = null, int externalAttributes = 0)
        {
            using (var file = new FileStream(Archive, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var item in Payload) WriteEntry(zip, "DS4Windows/" + item.Key, item.Value);
                WriteEntry(zip, "DS4Windows/" + PortablePackageTransaction.ManifestName, Bytes(manifest ?? ManifestText()));
                if (extraRawPath != null) WriteEntry(zip, extraRawPath, Bytes("extra"), externalAttributes);
            }
            using var archive = File.OpenRead(Archive);
            archiveHash = Convert.ToHexString(SHA256.HashData(archive));
        }
        private static void WriteEntry(ZipArchive zip, string name, byte[] data, int attributes = 0)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            if (attributes != 0) entry.ExternalAttributes = attributes;
            using Stream output = entry.Open();
            output.Write(data);
        }
        internal PortablePackageTransaction Prepare() => PortablePackageTransaction.Prepare(Target, Archive, archiveHash, Tag);
        internal Dictionary<string, string> LiveSnapshot() => Directory.EnumerateFiles(Target, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(Target, p).StartsWith("Updates" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(p => Path.GetRelativePath(Target, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        public void Dispose()
        {
            string verified = Path.GetFullPath(Root);
            string expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(verified), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(verified).StartsWith("ds4w-portable-transaction-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unsafe fixture cleanup.");
            if (Directory.Exists(verified)) Directory.Delete(verified, recursive: true);
        }
    }
}
