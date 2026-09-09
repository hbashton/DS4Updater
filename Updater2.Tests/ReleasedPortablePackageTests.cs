using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class ReleasedPortablePackageTests
{
    [TestMethod]
    [TestCategory("ReleasedPackage")]
    public void ActualRc451ArchiveStagesAndAppliesWithoutTouchingUserData()
    {
        string archive = Environment.GetEnvironmentVariable("DS4UPDATER_RC451_PACKAGE");
        if (string.IsNullOrWhiteSpace(archive))
            Assert.Inconclusive("Opt in with DS4UPDATER_RC451_PACKAGE pointing to the unchanged RC4.5.1 portable ZIP.");
        const string digest = "985B7DA39AB682FAE9ED27EC4B1622F58C240911454CFFC29CD661EC13BBA109";
        using (var input = File.OpenRead(archive))
            Assert.AreEqual(digest, Convert.ToHexString(SHA256.HashData(input)));

        string root = Path.Combine(Path.GetTempPath(), "ds4w-released-package-tests-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "app");
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, PortablePackageTransaction.MarkerName), PortablePackageTransaction.MarkerText);
            File.WriteAllText(Path.Combine(target, "DS4Windows.release"), "VIIPERRC4.5");
            File.WriteAllText(Path.Combine(target, "old-only.dll"), "old owned fixture");
            File.WriteAllText(Path.Combine(target, PortablePackageTransaction.ManifestName),
                "DS4Windows.portable\nDS4Windows.release\nold-only.dll\n");
            var userFiles = new Dictionary<string, string>
            {
                ["Profiles/Default.xml"] = "<Profile><Name>Keep my profile</Name></Profile>",
                ["Actions.xml"] = "<Actions />", ["LinkedProfiles.xml"] = "<Links />",
                ["portable-data/Nintendo/OriginalJoyConPairs.json"] = "[]",
                ["portable-data/VIIPER/viiper.key.txt"] = "non-secret synthetic fixture",
                ["Plugins/custom.dll"] = "user-owned fixture", ["Logs/session.txt"] = "keep fixture log"
            };
            foreach (var file in userFiles)
            {
                string path = Path.Combine(target, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, file.Value);
            }
            using (var transaction = PortablePackageTransaction.Prepare(target, archive, digest, "VIIPERRC4.5.1"))
            {
                Assert.AreEqual("VIIPERRC4.5", File.ReadAllText(Path.Combine(target, "DS4Windows.release")));
                Assert.IsFalse(File.Exists(Path.Combine(target, "DS4Windows.exe")));
                Assert.AreEqual("5.0.5.1", FileVersionInfo.GetVersionInfo(
                    "\\\\?\\" + Path.Combine(transaction.StagedRoot, "DS4Windows.exe")).FileVersion);
                transaction.Apply();
                Assert.IsFalse(transaction.RecoveryRequired);
            }
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries.Where(item => !item.FullName.EndsWith('/')))
            {
                string relative = entry.FullName["DS4Windows/".Length..];
                if (relative == PortablePackageTransaction.ManifestName) continue;
                using var expected = entry.Open();
                using var actual = File.OpenRead(Path.Combine(target, relative));
                Assert.AreEqual(Convert.ToHexString(SHA256.HashData(expected)),
                    Convert.ToHexString(SHA256.HashData(actual)), relative);
            }
            Assert.IsFalse(File.Exists(Path.Combine(target, "old-only.dll")));
            foreach (var file in userFiles) Assert.AreEqual(file.Value, File.ReadAllText(Path.Combine(target, file.Key)), file.Key);
            Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(target, "Updates"), "portable-update-*").Length);
        }
        finally
        {
            string verified = Path.GetFullPath(root);
            if (Path.GetDirectoryName(verified) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(verified).StartsWith("ds4w-released-package-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unsafe released-package fixture cleanup.");
            if (Directory.Exists(verified)) Directory.Delete(verified, true);
        }
    }
}
