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
    /// Finds environments that contain PTC-1\webserver\srm2\EN structure.
    /// Searches all top-level directories in C:\ for the path pattern.
    /// </summary>
    public static IEnumerable<EnvironmentTarget> FindPeekVriEnvironments()
    {
        var root = "C:\\";
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var baseDir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            // Skip system directories
            var dirName = Path.GetFileName(baseDir);
            if (dirName.StartsWith("$") || dirName.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Program Files", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Users", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                continue;

            var enPath = Path.Combine(baseDir, "PTC-1", "webserver", "srm2", "EN");
            var srm2Path = Path.GetDirectoryName(enPath)!;

            // Check if the PTC-1\webserver\srm2 structure exists (EN folder may or may not exist yet)
            if (Directory.Exists(enPath) || Directory.Exists(srm2Path))
                yield return new EnvironmentTarget(baseDir, enPath);
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

    /// <summary>
    /// Computes a stable SHA256 for the EN content inside a loadweb.zip file.
    /// Extracts to a temp folder, finds EN root, computes checksum, then cleans up.
    /// </summary>
    public static string ComputeZipContentsSha256(string zipPath)
    {
        if (!File.Exists(zipPath))
            return "(missing zip)";

        var tempDir = Path.Combine(Path.GetTempPath(), "PeekVriWebSwitcher_temp_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            var enRoot = WebPackageLocator.FindEnRoot(tempDir);
            if (enRoot == null)
                return "(no EN root in zip)";

            return ComputeSha256(enRoot);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
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
