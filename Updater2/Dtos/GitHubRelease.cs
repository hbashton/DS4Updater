using System;

namespace DS4Updater.Dtos
{
    // https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28
    public record GitHubRelease(
        string tag_name,
        bool prerelease,
        bool draft,
        DateTimeOffset? published_at,
        DateTimeOffset? created_at,
        GitHubReleaseAsset[] assets,
        long? id = null);

    public record GitHubReleaseAsset(string name, string browser_download_url,
        long? size = null, string digest = null);
}
