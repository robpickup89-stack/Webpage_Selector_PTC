using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PeekVriWebSwitcher;

public sealed class MainForm : Form
{
    private readonly ListBox _envList = new() { Dock = DockStyle.Fill };
    private readonly ListBox _pkgList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = false };
    private readonly Button _refreshBtn = new() { Text = "Refresh environments" };
    private readonly Button _addZipBtn = new() { Text = "Add web ZIP..." };
    private readonly Button _activateBtn = new() { Text = "Activate selected package" };
    private readonly Button _openEnvBtn = new() { Text = "Open environment folder" };
    private readonly Label _envStatus = new() { AutoSize = true, Text = "Active checksum: (select environment)" };
    private readonly Label _pkgStatus = new() { AutoSize = true, Text = "Package checksum: (select package)" };

    private readonly string _appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeekVriWebSwitcher");
    private readonly string _packagesRoot;
    private readonly string _backupsRoot;

    private List<EnvironmentTarget> _envs = new();
    private List<WebPackage> _pkgs = new();

    public MainForm()
    {
        Text = "PeekVri Web Switcher (SRM2 EN)";
        Width = 1100;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;

        // Load and set form icon from embedded resource
        var asm = Assembly.GetExecutingAssembly();
        var iconRes = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("swarco.ico", StringComparison.OrdinalIgnoreCase));
        if (iconRes != null)
        {
            using var iconStream = asm.GetManifestResourceStream(iconRes);
            if (iconStream != null)
                Icon = new Icon(iconStream);
        }

        _packagesRoot = Path.Combine(_appRoot, "Packages");
        _backupsRoot = Path.Combine(_appRoot, "Backups");

        Directory.CreateDirectory(_packagesRoot);
        Directory.CreateDirectory(_backupsRoot);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        Controls.Add(root);

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Padding = new Padding(8, 8, 8, 8) };
        top.Controls.AddRange(new Control[] { _refreshBtn, _addZipBtn, _activateBtn, _openEnvBtn });
        root.Controls.Add(top, 0, 0);
        root.SetColumnSpan(top, 2);

        var envGroup = new GroupBox { Text = "Environments (C:\\*\\PTC-1\\webserver\\srm2\\EN)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        envGroup.Controls.Add(_envList);
        root.Controls.Add(envGroup, 0, 1);

        var pkgGroup = new GroupBox { Text = "Web packages", Dock = DockStyle.Fill, Padding = new Padding(8) };
        pkgGroup.Controls.Add(_pkgList);
        root.Controls.Add(pkgGroup, 1, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        bottom.Controls.Add(_envStatus, 0, 0);
        bottom.Controls.Add(_pkgStatus, 1, 0);
        bottom.Controls.Add(new Label { AutoSize = true, Text = "Log:" }, 0, 1);
        bottom.SetColumnSpan(bottom.Controls[^1], 2);
        bottom.Controls.Add(_log, 0, 2);
        bottom.SetColumnSpan(_log, 2);

        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);

        _refreshBtn.Click += async (_, __) => await RefreshEnvironmentsAsync();
        _addZipBtn.Click += async (_, __) => await AddZipAsync();
        _activateBtn.Click += async (_, __) => await ActivateSelectedAsync();
        _openEnvBtn.Click += (_, __) => OpenSelectedEnvironmentFolder();

        _envList.SelectedIndexChanged += async (_, __) => await UpdateEnvironmentStatusAsync();
        _pkgList.SelectedIndexChanged += async (_, __) => await UpdatePackageStatusAsync();

        Shown += async (_, __) =>
        {
            Log("App data: " + _appRoot);
            await EnsureEmbeddedPackagesExtractedAsync(force: false);
            await RefreshPackagesAsync();
            await RefreshEnvironmentsAsync();
        };
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        _log.AppendText(line);
    }

    private async Task EnsureEmbeddedPackagesExtractedAsync(bool force)
    {
        try
        {
            var embedded = new[]
            {
                new { ResourceEndsWith = "MCA_Webpages_20260224.zip", Name = "MCA Webpages (2026-02-24)" },
                new { ResourceEndsWith = "Swarco_Default_20260224.zip", Name = "Swarco Default (2026-02-24)" }
            };

            foreach (var e in embedded)
            {
                var destDir = Path.Combine(_packagesRoot, SafeName(e.Name));
                var metaPath = Path.Combine(destDir, "package.json");

                if (!force && Directory.Exists(destDir) && File.Exists(metaPath))
                    continue;

                Directory.CreateDirectory(destDir);

                var asm = Assembly.GetExecutingAssembly();
                var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(e.ResourceEndsWith, StringComparison.OrdinalIgnoreCase));
                if (resName is null)
                {
                    Log($"Embedded resource not found: {e.ResourceEndsWith}");
                    continue;
                }

                var zipPath = Path.Combine(destDir, "source.zip");
                await using (var resStream = asm.GetManifestResourceStream(resName)!)
                await using (var outStream = File.Create(zipPath))
                    await resStream.CopyToAsync(outStream);

                var extractDir = Path.Combine(destDir, "extracted");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

                var enRoot = WebPackageLocator.FindEnRoot(extractDir);
                if (enRoot is null)
                {
                    Log($"Could not find EN root in embedded package: {e.Name}");
                    continue;
                }

                var checksum = await Task.Run(() => DirectoryChecksum.ComputeSha256(enRoot));
                var pkg = new WebPackage(e.Name, destDir, enRoot, checksum, IsEmbedded: true);
                pkg.SaveMetadata();

                Log($"Extracted embedded: {e.Name} (checksum {checksum[..12]}...)");
            }
        }
        catch (Exception ex)
        {
            Log("ERROR extracting embedded packages: " + ex);
            MessageBox.Show(this, ex.Message, "Extract embedded packages", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RefreshEnvironmentsAsync()
    {
        try
        {
            _envList.Items.Clear();
            _envs = await Task.Run(() => EnvironmentScanner.FindPeekVriEnvironments().ToList());

            foreach (var env in _envs)
                _envList.Items.Add(env.DisplayName);

            Log($"Found {_envs.Count} environment(s).");
            _envStatus.Text = "Active checksum: (select environment)";
        }
        catch (Exception ex)
        {
            Log("ERROR scanning environments: " + ex);
            MessageBox.Show(this, ex.Message, "Refresh environments", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RefreshPackagesAsync()
    {
        _pkgList.Items.Clear();
        _pkgs = await Task.Run(() => WebPackage.LoadAll(_packagesRoot).ToList());

        foreach (var p in _pkgs.OrderByDescending(p => p.IsEmbedded).ThenBy(p => p.Name))
            _pkgList.Items.Add(p.Name + (p.IsEmbedded ? "  (embedded)" : ""));

        Log($"Loaded {_pkgs.Count} package(s).");
        _pkgStatus.Text = "Package checksum: (select package)";
    }

    private async Task UpdateEnvironmentStatusAsync()
    {
        _envStatus.Text = "Active checksum: (calculating...)";
        var env = GetSelectedEnvironment();
        if (env is null)
        {
            _envStatus.Text = "Active checksum: (select environment)";
            return;
        }

        try
        {
            var checksum = await Task.Run(() => Directory.Exists(env.EnPath) ? DirectoryChecksum.ComputeSha256(env.EnPath) : "(missing EN folder)");
            _envStatus.Text = $"Active checksum: {checksum}";
            var match = _pkgs.FirstOrDefault(p => p.Checksum.Equals(checksum, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                _envStatus.Text += $"   (matches: {match.Name})";
        }
        catch (Exception ex)
        {
            _envStatus.Text = "Active checksum: (error)";
            Log("ERROR checksum env: " + ex);
        }
    }

    private async Task UpdatePackageStatusAsync()
    {
        var pkg = GetSelectedPackage();
        _pkgStatus.Text = pkg is null ? "Package checksum: (select package)" : $"Package checksum: {pkg.Checksum}";
        await Task.CompletedTask;
    }

    private async Task AddZipAsync()
    {
        try
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select a web ZIP (e.g. loadweb.zip)",
                Filter = "ZIP files (*.zip)|*.zip|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            var name = Path.GetFileNameWithoutExtension(ofd.FileName);
            var destDir = Path.Combine(_packagesRoot, SafeName($"{name} ({DateTime.Now:yyyy-MM-dd})"));
            Directory.CreateDirectory(destDir);

            var zipPath = Path.Combine(destDir, "source.zip");
            File.Copy(ofd.FileName, zipPath, overwrite: true);

            var extractDir = Path.Combine(destDir, "extracted");
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            var enRoot = WebPackageLocator.FindEnRoot(extractDir);
            if (enRoot is null)
                throw new InvalidOperationException("Couldn't find an EN folder inside the zip. Expected either: webserver\\srm2\\EN, srm2\\EN, or a zip where the root is already the EN content.");

            var checksum = await Task.Run(() => DirectoryChecksum.ComputeSha256(enRoot));
            var pkg = new WebPackage(name, destDir, enRoot, checksum, IsEmbedded: false);
            pkg.SaveMetadata();

            Log($"Added package: {pkg.Name} (checksum {checksum[..12]}...)");
            await RefreshPackagesAsync();
        }
        catch (Exception ex)
        {
            Log("ERROR adding zip: " + ex);
            MessageBox.Show(this, ex.Message, "Add ZIP", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ActivateSelectedAsync()
    {
        var env = GetSelectedEnvironment();
        var pkg = GetSelectedPackage();

        if (env is null || pkg is null)
        {
            MessageBox.Show(this, "Select an environment and a package first.", "Activate", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (!Directory.Exists(env.EnPath))
                Directory.CreateDirectory(env.EnPath);

            // Backup current EN
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupDir = Path.Combine(_backupsRoot, SafeName(env.DisplayName), ts);
            Directory.CreateDirectory(backupDir);

            if (Directory.Exists(env.EnPath))
                DirectoryCopy.CopyDirectory(env.EnPath, backupDir);

            Log($"Backup created: {backupDir}");

            // Replace EN contents
            DirectoryCopy.CleanDirectory(env.EnPath);
            DirectoryCopy.CopyDirectory(pkg.EnRootPath, env.EnPath);

            // Copy source.zip as loadweb.zip to EN folder
            var sourceZip = Path.Combine(pkg.PackageDir, "source.zip");
            var loadwebZip = Path.Combine(env.EnPath, "loadweb.zip");
            if (File.Exists(sourceZip))
            {
                File.Copy(sourceZip, loadwebZip, overwrite: true);
                Log($"Copied package zip as: {loadwebZip}");
            }

            Log($"Activated package '{pkg.Name}' to: {env.EnPath}");
            await UpdateEnvironmentStatusAsync();
            MessageBox.Show(this, $"Activated successfully.\n\nPath: {loadwebZip}", "Activate", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR activating: " + ex);
            MessageBox.Show(this, ex.Message, "Activate", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedEnvironmentFolder()
    {
        var env = GetSelectedEnvironment();
        if (env is null) return;

        try
        {
            if (Directory.Exists(env.BasePath))
                System.Diagnostics.Process.Start("explorer.exe", env.BasePath);
        }
        catch (Exception ex)
        {
            Log("ERROR opening folder: " + ex);
        }
    }

    private EnvironmentTarget? GetSelectedEnvironment()
    {
        var i = _envList.SelectedIndex;
        if (i < 0 || i >= _envs.Count) return null;
        return _envs[i];
    }

    private WebPackage? GetSelectedPackage()
    {
        var i = _pkgList.SelectedIndex;
        if (i < 0 || i >= _pkgs.Count) return null;
        // list is sorted embedded then by name, so map by text
        var display = _pkgList.Items[i].ToString() ?? "";
        var name = display.Replace("  (embedded)", "");
        return _pkgs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
