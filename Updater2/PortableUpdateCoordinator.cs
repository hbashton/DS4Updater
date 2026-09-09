using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DS4Updater.Dtos;

namespace DS4Updater;

internal sealed record PortableInstalledIdentity(string FileVersion, string ProductVersion, string ReleaseTag);
internal sealed record PortableUpdateProgress(string Message, bool Applying = false);

internal interface IPortablePreparedPackage : IDisposable
{
    string StagedRoot { get; }
    void Apply(string customExeBaseName);
}

internal interface IPortableUpdateOperations
{
    string ValidateRoot(string target);
    PortableInstalledIdentity ReadIdentity(string root, string launchExe);
    Task<GitHubRelease> FetchReleaseAsync(string exactTag, CancellationToken cancellation);
    Task<string> DownloadAsync(GitHubReleaseAsset asset, CancellationToken cancellation);
    IPortablePreparedPackage Prepare(string target, string archive, string digest, string tag);
    Task WaitForQuiescenceAsync(PortableUpdateRequest request, CancellationToken cancellation);
}

internal static class PortableUpdateCoordinator
{
    internal static async Task ExecuteAsync(PortableUpdateRequest request, IPortableUpdateOperations operations,
        IProgress<PortableUpdateProgress> progress, CancellationToken cancellation)
    {
        if (!request.IsWorker) throw new InvalidOperationException("Only the isolated worker can apply a portable update.");
        string target = operations.ValidateRoot(request.TargetDirectory);
        if (!string.Equals(target, request.TargetDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected portable root changed.");
        cancellation.ThrowIfCancellationRequested();
        PortableInstalledIdentity installed = operations.ReadIdentity(target, request.LaunchExe);
        progress?.Report(new("Checking the exact requested release…"));
        GitHubRelease release = await operations.FetchReleaseAsync(request.ReleaseTag, cancellation).ConfigureAwait(false);
        if (release == null || release.draft || !string.Equals(release.tag_name, request.ReleaseTag, StringComparison.Ordinal) ||
            !ReleaseChannelPolicy.ShouldUpdate(release, installed.FileVersion,
                ReleaseChannelPolicy.IsPrereleaseInstall(installed.ProductVersion, installed.ReleaseTag), installed.ReleaseTag) ||
            !IsNonDowngradingBinary(release.tag_name, installed.FileVersion))
            throw new InvalidDataException("The requested release is not a verified forward update for this portable copy.");
        GitHubReleaseAsset asset = ReleaseChannelPolicy.SelectPortableAsset(release, "x64");
        if (asset == null || !ReleaseChannelPolicy.TryGetAssetSha256(asset, out string digest) ||
            asset.size.GetValueOrDefault() > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The release does not have one supported x64 portable ZIP with a GitHub SHA-256 digest.");
        progress?.Report(new("Downloading the verified portable package…"));
        string archive = await operations.DownloadAsync(asset, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("Verifying and staging every packaged file…"));
        using IPortablePreparedPackage package = operations.Prepare(target, archive, digest, release.tag_name);
        PortableInstalledIdentity staged = operations.ReadIdentity(package.StagedRoot, "DS4Windows.exe");
        if (!ReleaseChannelPolicy.VerifyInstalledIdentity(release.tag_name, staged.FileVersion, staged.ProductVersion, staged.ReleaseTag))
            throw new InvalidDataException("The staged Windows binaries do not match the selected release identity.");
        progress?.Report(new("Waiting for this portable DS4Windows and VIIPER to close…"));
        await operations.WaitForQuiescenceAsync(request, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (!string.Equals(operations.ValidateRoot(target), target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The portable root changed before installation.");
        cancellation.ThrowIfCancellationRequested();
        // From here ordinary failures are handled by the file transaction's
        // rollback, not by abandoning a background task on cancellation.
        progress?.Report(new("Installing verified files. Please keep this window open…", Applying: true));
        package.Apply(request.CustomExeBaseName);
        PortableInstalledIdentity final = operations.ReadIdentity(target, request.LaunchExe);
        if (!ReleaseChannelPolicy.VerifyInstalledIdentity(release.tag_name, final.FileVersion, final.ProductVersion, final.ReleaseTag))
            throw new IOException("The installed identity could not be confirmed. Do not launch DS4Windows until the update folder is inspected.");
        progress?.Report(new("Portable DS4Windows was updated successfully."));
    }

    private static bool IsNonDowngradingBinary(string selectedTag, string installedFileVersion) =>
        ReleaseChannelPolicy.TryGetExpectedFileVersion(selectedTag, out Version expected) &&
        Version.TryParse(installedFileVersion, out Version installed) &&
        expected >= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build), Math.Max(0, installed.Revision));
}

internal sealed class PortableUpdateOperations : IPortableUpdateOperations, IDisposable
{
    private readonly string workerDirectory;
    private readonly PortableWorkerRecord worker;
    private readonly HttpClient client;
    private readonly TimeSpan transferTimeout;

    internal PortableUpdateOperations(string workerDirectory, PortableWorkerRecord record,
        HttpMessageHandler handler = null, TimeSpan? transferTimeout = null)
    {
        this.workerDirectory = workerDirectory;
        worker = record;
        this.transferTimeout = transferTimeout ?? TimeSpan.FromMinutes(15);
        if (this.transferTimeout <= TimeSpan.Zero || this.transferTimeout > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(transferTimeout));
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DS4Windows-Portable-Updater/2.0.5");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public string ValidateRoot(string target) => PortableUpdateProcessGuard.ValidateTargetRoot(target);

    public PortableInstalledIdentity ReadIdentity(string root, string launchExe)
    {
        string executable = Path.Combine(root, launchExe);
        string assembly = Path.Combine(root, "DS4Windows.dll");
        string marker = Path.Combine(root, "DS4Windows.release");
        foreach (string path in new[] { executable, assembly, marker }) PortablePackageTransaction.ValidateNoReparse(path);
        if (!File.Exists(executable) || !File.Exists(assembly))
            throw new InvalidDataException("The portable application identity files are missing or invalid.");
        FileVersionInfo app = ReadVersionInfo(executable);
        FileVersionInfo dll = ReadVersionInfo(assembly);
        if (string.IsNullOrWhiteSpace(app.FileVersion) ||
            !string.Equals(app.FileVersion, dll.FileVersion, StringComparison.Ordinal) ||
            !string.Equals(app.ProductVersion, dll.ProductVersion, StringComparison.Ordinal))
            throw new InvalidDataException("DS4Windows.exe and DS4Windows.dll do not carry the same release identity.");
        return new(app.FileVersion, app.ProductVersion,
            PortablePackageTransaction.ReadBoundedTextSnapshot(marker, 256).Text.TrimEnd('\r', '\n'));
    }

    private static FileVersionInfo ReadVersionInfo(string path)
    {
        // Staging beneath a portable folder can exceed MAX_PATH even when
        // the installed path does not. Win32 otherwise reports an empty
        // version resource for an intact file that ordinary streams can read.
        string full = Path.GetFullPath(path);
        string native = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full :
            full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        return FileVersionInfo.GetVersionInfo(native);
    }

    public async Task<GitHubRelease> FetchReleaseAsync(string exactTag, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(transferTimeout < TimeSpan.FromSeconds(45) ? transferTimeout : TimeSpan.FromSeconds(45));
        cancellation = deadline.Token;
        var uri = new Uri("https://api.github.com/repos/hbashton/DS4Windows/releases/tags/" + Uri.EscapeDataString(exactTag));
        using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using Stream input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var metadata = new MemoryStream();
        await CopyBoundedAsync(input, metadata, 4 * 1024 * 1024, null, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GitHubRelease>(metadata.ToArray());
    }

    public async Task<string> DownloadAsync(GitHubReleaseAsset asset, CancellationToken cancellation)
    {
        // ResponseHeadersRead transfers body ownership to this method: the
        // HttpClient timeout alone does not bound a stalled response stream.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(transferTimeout);
        cancellation = deadline.Token;
        Uri current = new(asset.browser_download_url);
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            ValidateDownloadUri(current);
            using HttpResponseMessage response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri location = response.Headers.Location ?? throw new HttpRequestException("The release download redirect is missing.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != asset.size)
                throw new InvalidDataException("The release download length does not match GitHub metadata.");
            string archive = Path.Combine(workerDirectory, "package.zip");
            PortablePackageTransaction.ValidateNoReparse(archive);
            using Stream input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await CopyBoundedAsync(input, output, asset.size.Value, asset.size.Value, cancellation).ConfigureAwait(false);
                output.Flush(true);
            }
            return archive;
        }
        throw new HttpRequestException("The release download exceeded its redirect limit.");
    }

    internal static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The release download redirected outside the supported HTTPS asset hosts.");
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, long? expected, CancellationToken cancellation)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("The download exceeds its approved size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
        }
        if (expected.HasValue && total != expected.Value) throw new InvalidDataException("The release download is incomplete.");
    }

    public IPortablePreparedPackage Prepare(string target, string archive, string digest, string tag) =>
        new PreparedPackage(PortablePackageTransaction.Prepare(target, archive, digest, tag));

    public async Task WaitForQuiescenceAsync(PortableUpdateRequest request, CancellationToken cancellation)
    {
        // The initial updater exits normally after launching this worker. Do
        // not race its mapped executable, and do not terminate it ourselves.
        try
        {
            using Process launcher = Process.GetProcessById(worker.LauncherPid);
            if (launcher.StartTime.ToUniversalTime().Ticks == worker.LauncherStartUtcTicks)
            {
                if (!string.Equals(launcher.MainModule?.FileName, worker.LauncherPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The updater launcher identity changed.");
                await launcher.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
            }
        }
        catch (ArgumentException) { } // The original PID has already exited.
        await new PortableUpdateProcessGuard().WaitForQuiescenceAsync(request.TargetDirectory, request.LaunchExe,
            request.ParentPid, request.ParentStartUtcTicks, cancellationToken: cancellation).ConfigureAwait(false);
        PortableWorkerSession.ValidateLaunchConfiguration(request);
    }

    public void Dispose() => client.Dispose();
    private sealed class PreparedPackage : IPortablePreparedPackage
    {
        private readonly PortablePackageTransaction transaction;
        internal PreparedPackage(PortablePackageTransaction value) { transaction = value; }
        public string StagedRoot => transaction.StagedRoot;
        public void Apply(string customExeBaseName) => transaction.Apply(customExeBaseName);
        public void Dispose() => transaction.Dispose();
    }
}
