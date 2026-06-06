namespace DS4Updater.Dtos
{
    // https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28
    public record GitHubRelease(string tag_name, GitHubReleaseAsset[] assets);

    public record GitHubReleaseAsset(string name, string browser_download_url);
}
