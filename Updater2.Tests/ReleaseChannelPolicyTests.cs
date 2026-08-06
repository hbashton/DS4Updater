using DS4Updater.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests
{
    [TestClass]
    public class ReleaseChannelPolicyTests
    {
        [TestMethod]
        public void StableInstallIgnoresNewerPrerelease()
        {
            GitHubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                new[]
                {
                    Release("v4.0.3", false, "2026-07-20T00:00:00Z"),
                    Release("VIIPERBeta7", true, "2026-07-22T00:00:00Z"),
                },
                currentBuildIsPrerelease: false);

            Assert.AreEqual("v4.0.3", selected.tag_name);
        }

        [TestMethod]
        public void PrereleaseInstallFollowsNewerPrerelease()
        {
            GitHubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                new[]
                {
                    Release("v4.0.3", false, "2026-07-20T00:00:00Z"),
                    Release("VIIPERBeta7", true, "2026-07-22T00:00:00Z"),
                },
                currentBuildIsPrerelease: true);

            Assert.AreEqual("VIIPERBeta7", selected.tag_name);
            Assert.IsTrue(ReleaseChannelPolicy.ShouldUpdate(
                selected, "4.0.2.1", true, installedReleaseTag: null));
        }

        [TestMethod]
        public void NewerStableReleaseMovesPrereleaseInstallToStableChannel()
        {
            GitHubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                new[]
                {
                    Release("v4.1.0", false, "2026-07-23T00:00:00Z"),
                    Release("VIIPERBeta7", true, "2026-07-22T00:00:00Z"),
                },
                currentBuildIsPrerelease: true);

            Assert.AreEqual("v4.1.0", selected.tag_name);
        }

        [TestMethod]
        public void InstalledTagPreventsRepeatedPrereleaseUpdate()
        {
            GitHubRelease selected = Release(
                "VIIPERBeta7", true, "2026-07-22T00:00:00Z");

            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(
                selected, "4.0.2.1", true, "viiperbeta7"));
        }

        [TestMethod]
        public void StableReleaseCannotDowngradeHigherPrereleaseVersion()
        {
            GitHubRelease selected = Release(
                "v4.1.0", false, "2026-07-24T00:00:00Z");

            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(
                selected, "5.0.0", true, installedReleaseTag: null));
        }

        [TestMethod]
        public void StaleStableMarkerDoesNotHideOlderBinary()
        {
            GitHubRelease selected = Release(
                "v4.1.0", false, "2026-07-24T00:00:00Z");

            Assert.IsTrue(ReleaseChannelPolicy.ShouldUpdate(
                selected, "4.0.0", false, installedReleaseTag: "v4.1.0"));
        }

        [TestMethod]
        public void PackagedPrereleaseMarkerKeepsNumericBinaryOnPrereleaseChannel()
        {
            Assert.IsTrue(ReleaseChannelPolicy.IsPrereleaseInstall(
                "5.0.2.0", "VIIPERRC4.2"));

            GitHubRelease selected = ReleaseChannelPolicy.SelectPreferredRelease(
                new[]
                {
                    Release("v5.0.1", false, "2026-08-05T00:00:00Z"),
                    Release("VIIPERRC4.2", true, "2026-08-06T00:00:00Z"),
                },
                currentBuildIsPrerelease: true);

            Assert.AreEqual("VIIPERRC4.2", selected.tag_name);
        }

        private static GitHubRelease Release(
            string tag,
            bool prerelease,
            string publishedAt)
        {
            return new GitHubRelease(
                tag,
                prerelease,
                draft: false,
                DateTimeOffset.Parse(publishedAt),
                created_at: null,
                assets: Array.Empty<GitHubReleaseAsset>());
        }
    }
}
