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

        [DataTestMethod]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.3", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.4", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.5", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.5.1", true)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.5", false)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.5.1", false)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.5.2", true)]
        [DataRow("VIIPERRC4.5.2", "VIIPERRC4.5.1", false)]
        [DataRow("VIIPERRC4.5.2", "VIIPERRC4.5.2", false)]
        [DataRow("VIIPERRC4.5.2", "VIIPERRC4.5.3", true)]
        [DataRow("VIIPERRC4.5.3", "VIIPERRC4.5.2", false)]
        [DataRow("VIIPERRC4.5.3", "VIIPERRC4.5.3", false)]
        [DataRow("VIIPERRC4.5.1", "VIIPERRC4.6", true)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.10", true)]
        [DataRow("VIIPERRC4.10", "VIIPERRC4.9", false)]
        [DataRow("VIIPERRC4", "VIIPERRC4.0", false)]
        [DataRow("VIIPERRC4", "VIIPERRC4.1", true)]
        [DataRow("VIIPERRC4.5", "VIIPERBeta9", false)]
        [DataRow("VIIPERBeta9", "VIIPERRC1", true)]
        [DataRow("VIIPERBeta8", "VIIPERBeta7", false)]
        [DataRow("VIIPERBeta", "VIIPERBeta2", true)]
        [DataRow(" viiperrc4.5 ", "VIIPERRC4.3", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC4.5-hotfix-unknown", false)]
        [DataRow("VIIPERRC4.5", "VIIPERRC9999999999999999999", false)]
        public void NamedChannelMovesForwardOnlyLikeTheMainApplication(string installed, string candidate, bool update) =>
            Assert.AreEqual(update, ReleaseChannelPolicy.ShouldUpdate(
                Release(candidate, true, "2026-09-09T00:00:00Z"), "5.0.5.0", true, installed));

        [DataTestMethod]
        [DataRow("v5.0.6.0-rc1", "VIIPERRC4.3", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.5.0-rc9", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.6-rc2", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.7.0-rc1", true)]
        [DataRow(" v5.0.6.0-rc1 ", " v5.0.7.0-rc1 ", true)]
        [DataRow("v5.0.6.0-rc1", "v5.0.9999999999999-rc1", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.7.0.0.0-rc1", false)]
        [DataRow("v5.0.6.0-rc1", "v5.0.7.0 broken", false)]
        [DataRow("unrecognized-preview", "VIIPERRC4.3", false)]
        public void UnknownOrNewNumericMarkersCannotRollBackIntoOldNamedChannel(string installed, string candidate, bool update) =>
            Assert.AreEqual(update, ReleaseChannelPolicy.ShouldUpdate(
                Release(candidate, true, "2026-09-09T00:00:00Z"), "5.0.6.0", true, installed));

        [TestMethod]
        public void StablePromotionIsNormalizedAndNeverAcceptsDraftOrDowngrade()
        {
            var stable = Release("v5.0.5", false, "2026-09-09T00:00:00Z");
            Assert.IsTrue(ReleaseChannelPolicy.ShouldUpdate(stable, "5.0.5.0", true, "VIIPERRC4.5"));
            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(stable, "5.0.5.1", true, "VIIPERRC4.5.1"));
            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(stable, "5.0.5.0", false, "v5.0.5.0"));
            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(stable with { draft = true }, "4.0.0.0", false, null));
            Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(Release("VIIPERRC4.5.1", true,
                "2026-09-09T00:00:00Z"), "5.0.5.0", false, "v5.0.5"));
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
