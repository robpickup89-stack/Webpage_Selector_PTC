using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PeekVriWebSwitcher;

public sealed record EnvironmentTarget(string BasePath, string EnPath)
{
    public string DisplayName => $"{BasePath}  ->  {EnPath.Replace(BasePath, "").TrimStart(Path.DirectorySeparatorChar)}";
}

public static class EnvironmentScanner
{
    /// <summary>
    /// Finds environments like C:\PeekVri_UK_* and tries to locate the SRM2 EN folder.
    /// Supports both:
    ///   C:\PeekVri_UK_x\PTC-1\webserver\srm2\EN
    ///   C:\PeekVri_UK_x\webserver\srm2\EN
    /// </summary>
    public static IEnumerable<EnvironmentTarget> FindPeekVriEnvironments()
    {
        var root = "C:\\";
        var dirs = Directory.EnumerateDirectories(root, "PeekVri_UK_*", SearchOption.TopDirectoryOnly);

        foreach (var baseDir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var cand1 = Path.Combine(baseDir, "PTC-1", "webserver", "srm2", "EN");
            var cand2 = Path.Combine(baseDir, "webserver", "srm2", "EN");

            if (Directory.Exists(cand1) || Directory.Exists(Path.GetDirectoryName(cand1)!))
                yield return new EnvironmentTarget(baseDir, cand1);
            else if (Directory.Exists(cand2) || Directory.Exists(Path.GetDirectoryName(cand2)!))
                yield return new EnvironmentTarget(baseDir, cand2);
            else
            {
                // If neither exists, still offer cand1 as default (so user can create it)
                yield return new EnvironmentTarget(baseDir, cand1);
            }
        }
    }
}

public sealed record WebPackage(string Name, string PackageDir, string EnRootPath, string Checksum, bool IsEmbedded)
{
    public string MetadataPath => Path.Combine(PackageDir, "package.json");

    public void SaveMetadata()
    {
        var meta = new
        {
            name = Name,
            enRootPath = EnRootPath,
            checksum = Checksum,
            isEmbedded = IsEmbedded,
            savedUtc = DateTime.UtcNow
        };
        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(MetadataPath, json, Encoding.UTF8);
    }

    public static IEnumerable<WebPackage> LoadAll(string packagesRoot)
    {
        if (!Directory.Exists(packagesRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(packagesRoot))
        {
            var metaPath = Path.Combine(dir, "package.json");
            if (!File.Exists(metaPath)) continue;

            var pkg = TryLoadPackage(dir, metaPath);
            if (pkg != null)
                yield return pkg;
        }
    }

    private static WebPackage? TryLoadPackage(string dir, string metaPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
            var root = doc.RootElement;

            var name = root.GetProperty("name").GetString() ?? Path.GetFileName(dir);
            var enRoot = root.GetProperty("enRootPath").GetString() ?? Path.Combine(dir, "extracted");
            var checksum = root.GetProperty("checksum").GetString() ?? "";
            var embedded = root.TryGetProperty("isEmbedded", out var e) && e.GetBoolean();

            if (!Directory.Exists(enRoot))
                return null;

            return new WebPackage(name, dir, enRoot, checksum, embedded);
        }
        catch
        {
            // ignore broken package
            return null;
        }
    }
}

public static class WebPackageLocator
{
    /// <summary>
    /// Tries to locate the folder that should be copied into ...\webserver\srm2\EN.
    /// Supports zips with:
    ///  - webserver\srm2\EN\(files)
    ///  - srm2\EN\(files)
    ///  - root already is EN (i.e. contains typical web files/folders)
    /// </summary>
    public static string? FindEnRoot(string extractedRoot)
    {
        // 1) ...\webserver\srm2\EN
        var a = Directory.EnumerateDirectories(extractedRoot, "EN", SearchOption.AllDirectories)
                         .FirstOrDefault(p => p.EndsWith(Path.Combine("webserver","srm2","EN"), StringComparison.OrdinalIgnoreCase));
        if (a != null) return a;

        // 2) ...\srm2\EN
        var b = Directory.EnumerateDirectories(extractedRoot, "EN", SearchOption.AllDirectories)
                         .FirstOrDefault(p => p.EndsWith(Path.Combine("srm2","EN"), StringComparison.OrdinalIgnoreCase));
        if (b != null) return b;

        // 3) Root is already EN content (heuristic: contains index.html OR frames folder OR editor folder)
        var rootFiles = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rootDirs = Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool looksLikeEn = rootDirs.Contains("frames") || rootDirs.Contains("editor") || rootFiles.Contains("browser_detect.js") || rootFiles.Contains("index.html");
        if (looksLikeEn) return extractedRoot;

        // 4) Single top folder that is EN content
        var onlyDir = Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly).SingleOrDefault();
        if (onlyDir != null)
        {
            var files = Directory.EnumerateFiles(onlyDir, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dirs = Directory.EnumerateDirectories(onlyDir, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool looks = dirs.Contains("frames") || dirs.Contains("editor") || files.Contains("browser_detect.js") || files.Contains("index.html");
            if (looks) return onlyDir;
        }

        return null;
    }
}

public static class DirectoryChecksum
{
    /// <summary>
    /// Computes a stable SHA256 for a directory based on relative paths + file bytes.
    /// </summary>
    public static string ComputeSha256(string dir)
    {
        if (!Directory.Exists(dir))
            return "(missing)";

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var sha = SHA256.Create();

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var relBytes = Encoding.UTF8.GetBytes(rel);
            sha.TransformBlock(relBytes, 0, relBytes.Length, null, 0);

            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}

public static class DirectoryCopy
{
    public static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static void CleanDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var d in Directory.EnumerateDirectories(dir))
            Directory.Delete(d, recursive: true);
        foreach (var f in Directory.EnumerateFiles(dir))
            File.Delete(f);
    }
}
