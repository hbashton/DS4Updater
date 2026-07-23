using DS4Updater.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DS4Updater
{
    internal static class ReleaseChannelPolicy
    {
        internal const string InstalledReleaseFileName = "DS4Windows.release";

        private static readonly Regex prereleaseNameRegex = new(
            @"(?i)(alpha|beta|preview|pre[- ]?release|prerelease|release candidate|(?:^|[^a-z])rc(?:\d|[^a-z]|$))",
            RegexOptions.Compiled);

        internal static bool IsPrereleaseBuild(string versionText)
        {
            return !string.IsNullOrWhiteSpace(versionText) &&
                prereleaseNameRegex.IsMatch(versionText);
        }

        internal static bool IsPrerelease(GitHubRelease release)
        {
            return release is not null &&
                (release.prerelease || IsPrereleaseBuild(release.tag_name));
        }

        internal static DateTimeOffset GetReleaseDate(GitHubRelease release)
        {
            return release?.published_at ?? release?.created_at ?? DateTimeOffset.MinValue;
        }

        internal static GitHubRelease SelectPreferredRelease(
            IEnumerable<GitHubRelease> releases,
            bool currentBuildIsPrerelease)
        {
            GitHubRelease[] published = (releases ?? Array.Empty<GitHubRelease>())
                .Where(release => release is not null &&
                    !release.draft &&
                    !string.IsNullOrWhiteSpace(release.tag_name))
                .ToArray();

            GitHubRelease latestStable = SelectNewest(
                published.Where(release => !IsPrerelease(release)));
            if (!currentBuildIsPrerelease)
            {
                return latestStable;
            }

            GitHubRelease latestPrerelease = SelectNewest(
                published.Where(IsPrerelease));
            if (latestPrerelease is null)
            {
                return latestStable;
            }

            if (latestStable is not null &&
                GetReleaseDate(latestStable) > GetReleaseDate(latestPrerelease))
            {
                return latestStable;
            }

            return latestPrerelease;
        }

        internal static bool ShouldUpdate(
            GitHubRelease selectedRelease,
            string currentVersionText,
            bool currentBuildIsPrerelease,
            string installedReleaseTag)
        {
            if (selectedRelease is null)
            {
                return false;
            }

            bool markerMatches = !string.IsNullOrWhiteSpace(installedReleaseTag) &&
                string.Equals(installedReleaseTag.Trim(), selectedRelease.tag_name,
                    StringComparison.OrdinalIgnoreCase);
            if (markerMatches && IsPrerelease(selectedRelease))
            {
                return false;
            }

            if (markerMatches &&
                TryParseReleaseVersion(currentVersionText, out Version markedCurrentVersion) &&
                TryParseReleaseVersion(selectedRelease.tag_name, out Version markedReleaseVersion) &&
                markedCurrentVersion >= markedReleaseVersion)
            {
                return false;
            }

            if (currentBuildIsPrerelease)
            {
                if (IsPrerelease(selectedRelease))
                {
                    return true;
                }

                return TryParseReleaseVersion(currentVersionText, out Version prereleaseVersion) &&
                    TryParseReleaseVersion(selectedRelease.tag_name, out Version stableVersion) &&
                    prereleaseVersion <= stableVersion;
            }

            if (IsPrerelease(selectedRelease))
            {
                return false;
            }

            return TryParseReleaseVersion(currentVersionText, out Version currentVersion) &&
                TryParseReleaseVersion(selectedRelease.tag_name, out Version selectedVersion) &&
                currentVersion < selectedVersion;
        }

        internal static bool TryParseReleaseVersion(
            string versionText,
            out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return false;
            }

            Match match = Regex.Match(versionText, @"\d+(?:\.\d+){1,3}");
            return match.Success && Version.TryParse(match.Value, out version);
        }

        private static GitHubRelease SelectNewest(IEnumerable<GitHubRelease> releases)
        {
            return releases
                .OrderByDescending(GetReleaseDate)
                .ThenByDescending(release =>
                    TryParseReleaseVersion(release.tag_name, out Version version) ?
                        version : new Version(0, 0, 0))
                .FirstOrDefault();
        }
    }
}
