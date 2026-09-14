using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DS4Updater;

/// <summary>
/// Read-only preflight, not a process owner. The caller requests its own normal
/// shutdown; this guard never terminates an app or a borrowed broker. The file
/// transaction must still acquire its own exclusive handles after this check.
/// </summary>
internal sealed class PortableUpdateProcessGuard
{
    internal const string MarkerFileName = "DS4Windows.portable";
    internal const string MarkerText = "DS4Windows portable package v1";
    private const string CloseApps = "Close DS4Windows, VIIPER and HidGuardHelper running from this portable folder, then retry the update. No processes were stopped.";
    private readonly IPortableUpdateProcessHost host;
    private readonly Func<string, string> validateRoot;

    internal PortableUpdateProcessGuard() : this(new PortableUpdateProcessHost(), ValidateTargetRoot) { }

    internal PortableUpdateProcessGuard(IPortableUpdateProcessHost host, Func<string, string> validateRoot)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.validateRoot = validateRoot ?? throw new ArgumentNullException(nameof(validateRoot));
    }

    internal async Task<string> WaitForQuiescenceAsync(string root, string customExeName = null,
        int? parentProcessId = null, long? parentStartUtcTicks = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        string originalExeName = null)
    {
        TimeSpan budget = timeout ?? TimeSpan.FromSeconds(30);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "The update wait must be greater than zero and at most 30 seconds.");
        if (parentProcessId.HasValue != parentStartUtcTicks.HasValue ||
            parentProcessId is <= 0 || parentStartUtcTicks is <= 0 || parentStartUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentException("The parent process ID and its UTC start ticks must be supplied together and be valid.");
        string alias = ValidateCustomExeName(customExeName);
        string original = ValidateCustomExeName(originalExeName);
        cancellationToken.ThrowIfCancellationRequested();
        string target = validateRoot(root);
        var appPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(target, "DS4Windows.exe") };
        if (alias != null) appPaths.Add(Path.Combine(target, alias));
        string originalPath = original == null ? null : Path.Combine(target, original);
        if (originalPath != null) appPaths.Add(originalPath);
        var targetPaths = new HashSet<string>(appPaths, StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(target, "viiper.exe"),
            Path.Combine(target, "HidGuardHelper.exe"),
            // Older complete packages also carry the same broker in extras.
            Path.Combine(target, "extras", "viiper.exe"),
        };
        string[] names = targetPaths.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long started = host.TimestampMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = budget - TimeSpan.FromMilliseconds(host.TimestampMilliseconds - started);
            if (remaining <= TimeSpan.Zero) throw new PortableUpdateProcessGuardException(CloseApps);
            IReadOnlyList<PortableUpdateProcessObservation> processes;
            try
            {
                processes = await host.SnapshotAsync(names, parentProcessId, cancellationToken)
                    .WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new PortableUpdateProcessGuardException("The running apps could not be verified. " + CloseApps, error);
            }
            if (processes == null)
                throw new PortableUpdateProcessGuardException("The running apps could not be verified. " + CloseApps);
            bool waiting = false;
            foreach (PortableUpdateProcessObservation process in processes)
            {
                if (!process.IdentityReadable || process.ProcessId <= 0 || process.StartTimeUtcTicks <= 0)
                    throw new PortableUpdateProcessGuardException("A running app's identity could not be verified. " + CloseApps);
                string image;
                try { image = NormalizeLocalPath(process.ExecutablePath); }
                catch (Exception error)
                {
                    throw new PortableUpdateProcessGuardException("A running app's location could not be verified. " + CloseApps, error);
                }
                bool originalParent = parentProcessId == process.ProcessId && parentStartUtcTicks == process.StartTimeUtcTicks;
                if (originalParent && (!appPaths.Contains(image) ||
                    (originalPath != null && !string.Equals(image, originalPath, StringComparison.OrdinalIgnoreCase))))
                    throw new PortableUpdateProcessGuardException("The initiating DS4Windows process does not belong to this portable folder. The update was not started.");
                // A reused parent PID is not the old parent. It is considered
                // only if its independently verified image belongs to target.
                if (targetPaths.Contains(image)) waiting = true;
            }
            if (!waiting)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(target, validateRoot(root), StringComparison.OrdinalIgnoreCase))
                    throw new PortableUpdateProcessGuardException("The portable folder changed while waiting. Retry from the original folder.");
                return target;
            }
            remaining = budget - TimeSpan.FromMilliseconds(host.TimestampMilliseconds - started);
            if (remaining <= TimeSpan.Zero) throw new PortableUpdateProcessGuardException(CloseApps);
            await host.DelayAsync(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static string ValidateCustomExeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Length > 120 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name != Path.GetFileName(name) || name.EndsWith(' ') || name.EndsWith('.') ||
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Length <= 4 ||
            IsReservedFileName(name) ||
            new[] { "viiper.exe", "HidGuardHelper.exe", "DS4Updater.exe", "Updater.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The custom DS4Windows executable must be a simple .exe filename, not a path or helper name.", nameof(name));
        return name;
    }

    private static bool IsReservedFileName(string name)
    {
        string stem = name.Split('.')[0];
        return new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] >= '1' && stem[3] <= '9');
    }

    internal static string ValidateTargetRoot(string directory)
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        var broadRoots = new[]
        {
            user, Path.GetDirectoryName(user),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.GetTempPath(), string.IsNullOrEmpty(user) ? null : Path.Combine(user, "Downloads"),
        };
        try
        {
            // Same machine registration used by DS4Windows.PortableBrokerContext;
            // also inspect the 32-bit view so an older registration cannot be
            // mistaken for portable ownership. Malformed/unreadable data fails.
            return ValidateTargetRootCore(directory, ReadManagedRoots(), protectedRoots,
                broadRoots, PortableUpdateProcessHost.ResolveDirectory, ValidateNoReparsePoints);
        }
        catch (PortableUpdateProcessGuardException) { throw; }
        catch (Exception error)
        {
            throw new PortableUpdateProcessGuardException("This folder could not be verified as a portable DS4Windows package. Use an extracted portable folder outside Windows and Program Files; installed copies must use the installer.", error);
        }
    }

    internal static string ValidateTargetRootCore(string directory, IEnumerable<string> managedRoots,
        IEnumerable<string> protectedRoots, IEnumerable<string> broadRoots,
        Func<string, string> resolveDirectory, Action<string> inspectPath)
    {
        string root = NormalizeLocalPath(directory);
        var managedPaths = new List<string>();
        foreach (string path in managedRoots.Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeLocalPath))
        {
            if (path == Path.GetPathRoot(path)) throw new InvalidDataException("The registered DS4Windows path is a drive root.");
            managedPaths.Add(path);
            // Protect both the recorded spelling and the real directory. A
            // registry entry using an 8.3 name is still a managed installation.
            inspectPath(path);
            if (Directory.Exists(path)) managedPaths.Add(NormalizeLocalPath(resolveDirectory(path)));
        }
        string[] managed = managedPaths.ToArray();
        string[] protectedPaths = protectedRoots.Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeLocalPath).ToArray();
        string[] broad = broadRoots.Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeLocalPath).ToArray();
        RejectUnsafeRoot(root, managed, protectedPaths, broad);
        inspectPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The portable folder does not exist.");
        root = NormalizeLocalPath(resolveDirectory(root));
        RejectUnsafeRoot(root, managed, protectedPaths, broad);
        inspectPath(root);
        string marker = Path.Combine(root, MarkerFileName);
        inspectPath(marker);
        FileAttributes attributes = File.GetAttributes(marker);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The portable marker is not a regular file.");
        using (var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > 128) throw new InvalidDataException("The portable marker is invalid.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (reader.ReadToEnd().TrimEnd('\r', '\n') != MarkerText)
                throw new InvalidDataException("The portable marker is invalid.");
        }
        return root;
    }

    private static void RejectUnsafeRoot(string root, string[] managed, string[] protectedPaths, string[] broad)
    {
        if (root == Path.GetPathRoot(root) || broad.Contains(root, StringComparer.OrdinalIgnoreCase) ||
            protectedPaths.Any(path => AtOrBelow(root, path)) ||
            managed.Any(path => AtOrBelow(root, path) || AtOrBelow(path, root)))
            throw new InvalidDataException("A managed installation, system folder or broad user folder is not a portable update target.");
    }

    private static IEnumerable<string> ReadManagedRoots()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey key = machine.OpenSubKey(@"SOFTWARE\DS4Windows", writable: false);
            object value = key?.GetValue("InstallPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null && value is not string) throw new InvalidDataException("The registered DS4Windows path is not a string.");
            if (value is string path)
            {
                string normalized = NormalizeLocalPath(path);
                if (normalized == Path.GetPathRoot(normalized)) throw new InvalidDataException("The registered DS4Windows path is a drive root.");
                yield return normalized;
            }
        }
    }

    internal static string NormalizeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/') ||
            path.AsSpan(2).IndexOf(':') >= 0)
            throw new InvalidDataException("A local absolute drive path is required.");
        foreach (string part in path.Substring(3).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            if (part == "." || part == ".." || part.EndsWith(' ') || part.EndsWith('.'))
                throw new InvalidDataException("Ambiguous path components are not allowed.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool AtOrBelow(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateNoReparsePoints(string path)
    {
        PortableUpdateProcessHost.EnsureLocalDrive(path);
        var ancestors = new Stack<string>();
        for (string candidate = path; !string.IsNullOrEmpty(candidate); candidate = Path.GetDirectoryName(candidate))
            ancestors.Push(candidate);
        // Inspect from the drive downward: do not first touch a child beneath
        // an as-yet-unchecked junction (which might lead to a network share).
        foreach (string candidate in ancestors)
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Portable update paths must not traverse links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

internal sealed class PortableUpdateProcessGuardException : InvalidOperationException
{
    internal PortableUpdateProcessGuardException(string message, Exception inner = null) : base(message, inner) { }
}

internal readonly record struct PortableUpdateProcessObservation(int ProcessId, long StartTimeUtcTicks, string ExecutablePath, bool IdentityReadable = true);

internal interface IPortableUpdateProcessHost
{
    long TimestampMilliseconds { get; }
    Task<IReadOnlyList<PortableUpdateProcessObservation>> SnapshotAsync(IReadOnlyCollection<string> names, int? parentProcessId, CancellationToken cancellationToken);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class PortableUpdateProcessHost : IPortableUpdateProcessHost
{
    public long TimestampMilliseconds => Environment.TickCount64;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    public Task<IReadOnlyList<PortableUpdateProcessObservation>> SnapshotAsync(IReadOnlyCollection<string> names, int? parentProcessId, CancellationToken cancellationToken) =>
        Task.Run(() => Snapshot(names, parentProcessId, cancellationToken), cancellationToken);

    private static IReadOnlyList<PortableUpdateProcessObservation> Snapshot(IReadOnlyCollection<string> names, int? parentProcessId, CancellationToken cancellationToken)
    {
        var processIds = new HashSet<int>();
        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process[] processes = Process.GetProcessesByName(name);
            try { foreach (Process process in processes) processIds.Add(process.Id); }
            finally { foreach (Process process in processes) process.Dispose(); }
        }
        if (parentProcessId.HasValue) processIds.Add(parentProcessId.Value);
        var result = new List<PortableUpdateProcessObservation>();
        foreach (int processId in processIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SafeProcessHandle handle = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION only
            if (handle.IsInvalid)
            {
                // With these fixed valid flags, ERROR_INVALID_PARAMETER means
                // the PID is already absent. Access denied remains a blocker.
                if (Marshal.GetLastWin32Error() != 87) result.Add(new(processId, 0, null, false));
                continue;
            }
            if (GetExitCodeProcess(handle, out uint exitCode) && exitCode != 259) continue;
            var image = new StringBuilder(32_768);
            int length = image.Capacity;
            long creation = 0;
            bool readable = QueryFullProcessImageName(handle, 0, image, ref length) && length > 0 &&
                GetProcessTimes(handle, out creation, out _, out _, out _);
            // Metadata comes from one retained process handle, never two PID
            // lookups which could accidentally combine different lifetimes.
            if (GetExitCodeProcess(handle, out exitCode) && exitCode != 259) continue;
            if (!readable) result.Add(new(processId, 0, null, false));
            else
            {
                // Compare canonical long spellings: an app started through an
                // 8.3 filename must not evade an exact-folder update check.
                var longImage = new StringBuilder(32_768);
                uint longLength = GetLongPathName(image.ToString(0, length), longImage, (uint)longImage.Capacity);
                if (longLength == 0 || longLength >= longImage.Capacity) result.Add(new(processId, 0, null, false));
                else result.Add(new(processId, DateTime.FromFileTimeUtc(creation).Ticks, longImage.ToString()));
            }
        }
        return result;
    }

    internal static string ResolveDirectory(string path)
    {
        EnsureLocalDrive(path);
        using SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The portable directory could not be opened for verification.");
        var finalPath = new StringBuilder(32_768);
        uint length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (length == 0 || length >= finalPath.Capacity) throw new IOException("The portable directory's final path could not be verified.");
        string resolved = finalPath.ToString();
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) || resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The portable directory is not a local drive path.");
        return resolved.Substring(4);
    }

    internal static void EnsureLocalDrive(string path)
    {
        uint driveType = GetDriveType(Path.GetPathRoot(path));
        if (driveType is not (2 or 3)) throw new InvalidDataException("Portable updates require a local fixed or removable drive.");
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint length);
}
