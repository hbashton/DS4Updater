/*
DS4Updater
Copyright (C) 2026  Harrison Bashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DS4Updater
{
    internal static class ManagedInstallCleanup
    {
        internal const string ManifestFileName = ".ds4windows-managed-files.txt";

        private static readonly string[] LegacyManagedDirectories =
        {
            "BezierCurveEditor",
            "extras",
            "Lang",
            "Resources",
            "runtimes",
            "ThirdParty",
        };

        private static readonly string[] ExplicitObsoleteFiles =
        {
            "Nefarius.ViGEm.Client.dll",
            "Nefarius.ViGEm.Client.xml",
            "DS4Control.dll",
            "DS4Library.dll",
            "HidLibrary.dll",
            "DS4Tool.exe",
            "DS4Windows.exe.config",
        };

        internal static HashSet<string> ReadInstalledManifest(string installRoot)
        {
            string manifestPath = Path.Combine(installRoot, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                File.ReadLines(manifestPath)
                    .Select(NormalizeRelativePath)
                    .Where(path => path != null),
                StringComparer.OrdinalIgnoreCase);
        }

        internal static HashSet<string> FindLegacyManagedFiles(string installRoot)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Dependencies historically shipped next to DS4Windows. Restrict this
            // migration scan to root DLLs and known package-owned directories so
            // profiles, settings, plugins, and arbitrary user files stay untouched.
            foreach (string filePath in Directory.EnumerateFiles(installRoot, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(filePath).StartsWith("DS4Updater.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddRelativePath(result, installRoot, filePath);
            }

            foreach (string directoryName in LegacyManagedDirectories)
            {
                string directoryPath = Path.Combine(installRoot, directoryName);
                if (!Directory.Exists(directoryPath)) continue;

                foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    AddRelativePath(result, installRoot, filePath);
                }
            }

            foreach (string obsoleteFile in ExplicitObsoleteFiles)
            {
                result.Add(obsoleteFile);
            }

            return result;
        }

        internal static HashSet<string> EnumeratePackageFiles(string packageRoot)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string filePath in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                AddRelativePath(result, packageRoot, filePath);
            }

            result.Remove(ManifestFileName);
            result.Remove("DS4Updater.exe");
            result.Remove("DS4Updater_x86.exe");
            return result;
        }

        internal static IReadOnlyList<string> DeleteObsoleteFiles(
            string installRoot,
            IEnumerable<string> previouslyManagedFiles,
            ISet<string> newManagedFiles)
        {
            var failures = new List<string>();
            var obsoleteFiles = new HashSet<string>(previouslyManagedFiles, StringComparer.OrdinalIgnoreCase);
            obsoleteFiles.UnionWith(ExplicitObsoleteFiles);
            obsoleteFiles.ExceptWith(newManagedFiles);

            foreach (string relativePath in obsoleteFiles)
            {
                string safePath = ResolveSafePath(installRoot, relativePath);
                if (safePath == null || !File.Exists(safePath)) continue;

                if (!TryDeleteFile(safePath))
                {
                    failures.Add(relativePath);
                }
            }

            PruneEmptyManagedDirectories(installRoot);
            return failures;
        }

        internal static void WriteInstalledManifest(string installRoot, IEnumerable<string> managedFiles)
        {
            string manifestPath = Path.Combine(installRoot, ManifestFileName);
            string temporaryPath = manifestPath + ".new";
            string[] normalizedFiles = managedFiles
                .Select(NormalizeRelativePath)
                .Where(path => path != null &&
                    !path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            File.WriteAllLines(temporaryPath, normalizedFiles);
            File.Move(temporaryPath, manifestPath, true);
        }

        private static void AddRelativePath(ISet<string> paths, string root, string filePath)
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(root, filePath));
            if (relativePath != null)
            {
                paths.Add(relativePath);
            }
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;

            string normalized = relativePath
                .Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

            if (normalized.Length == 0 ||
                normalized.Equals("..", StringComparison.Ordinal) ||
                normalized.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            return normalized;
        }

        private static string ResolveSafePath(string installRoot, string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (normalized == null) return null;

            string root = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, normalized));
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }

        private static bool TryDeleteFile(string filePath)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    File.Delete(filePath);
                    return true;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(75);
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Sleep(75);
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }

            return false;
        }

        private static void PruneEmptyManagedDirectories(string installRoot)
        {
            foreach (string directoryName in LegacyManagedDirectories)
            {
                string directoryPath = Path.Combine(installRoot, directoryName);
                if (!Directory.Exists(directoryPath)) continue;

                foreach (string childPath in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(childPath).Any())
                    {
                        Directory.Delete(childPath);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    Directory.Delete(directoryPath);
                }
            }
        }
    }
}
