using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PvzBackupTool;

public sealed class MainForm : Form
{
    const string DeviceBackupDir = "/sdcard/MIUI/backup/AllBackup";
    const string BackupActivity = "com.miui.backup/.local.LocalHomeActivity";

    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PvzBackupTool", "settings.txt");

    readonly TextBox _adbBox = new() { Dock = DockStyle.Fill };
    readonly TextBox _pkgBox = new() { Dock = DockStyle.Fill };
    readonly TextBox _destBox = new() { Dock = DockStyle.Fill };
    readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly Button _btnBackup = new() { Text = "BACK UP", Dock = DockStyle.Fill, Height = 52 };
    readonly Button _btnRestore = new() { Text = "RESTORE", Dock = DockStyle.Fill, Height = 52 };
    readonly Button _btnCancel = new() { Text = "Cancel", Dock = DockStyle.Fill, Enabled = false };
    readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        BackColor = Color.FromArgb(24, 24, 24),
        ForeColor = Color.FromArgb(220, 220, 220),
        Font = new Font("Consolas", 9f),
    };

    CancellationTokenSource? _cts;

    public MainForm()
    {
        Text = "Android App Data Backup / Restore (MIUI - HyperOS)";
        MinimumSize = new Size(760, 560);
        Size = new Size(880, 660);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadSettings();

        Shown += async (_, _) => await RefreshDeviceAsync();
        FormClosing += (_, _) => { _cts?.Cancel(); SaveSettings(); };
    }

    // ---------------------------------------------------------------- layout

    void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 7,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // buttons
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // log label
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // log

        // adb path
        root.Controls.Add(new Label { Text = "adb.exe", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        root.Controls.Add(_adbBox, 1, 0);
        var browseAdb = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browseAdb.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "adb.exe|adb.exe|All files|*.*", Title = "Locate adb.exe" };
            if (d.ShowDialog(this) == DialogResult.OK) _adbBox.Text = d.FileName;
        };
        root.Controls.Add(browseAdb, 2, 0);

        // package
        root.Controls.Add(new Label { Text = "Package", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        root.Controls.Add(_pkgBox, 1, 1);
        var pickPkg = new Button { Text = "Pick...", Dock = DockStyle.Fill };
        pickPkg.Click += async (_, _) => await PickPackageAsync();
        root.Controls.Add(pickPkg, 2, 1);

        // destination
        root.Controls.Add(new Label { Text = "Save to", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        root.Controls.Add(_destBox, 1, 2);
        var browseDest = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browseDest.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Where to store backups" };
            if (Directory.Exists(_destBox.Text)) d.SelectedPath = _destBox.Text;
            if (d.ShowDialog(this) == DialogResult.OK) _destBox.Text = d.SelectedPath;
        };
        root.Controls.Add(browseDest, 2, 2);

        // device status
        root.Controls.Add(new Label { Text = "Device", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        root.Controls.Add(_status, 1, 3);
        var refresh = new Button { Text = "Refresh", Dock = DockStyle.Fill };
        refresh.Click += async (_, _) => await RefreshDeviceAsync();
        root.Controls.Add(refresh, 2, 3);

        // action buttons
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 6, 0, 6) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

        _btnBackup.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _btnRestore.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _btnBackup.Click += async (_, _) => await RunGuarded(BackupAsync);
        _btnRestore.Click += async (_, _) => await RunGuarded(RestoreAsync);
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); Log("Cancel requested..."); };

        actions.Controls.Add(_btnBackup, 0, 0);
        actions.Controls.Add(_btnRestore, 1, 0);
        actions.Controls.Add(_btnCancel, 2, 0);
        root.Controls.Add(actions, 0, 4);
        root.SetColumnSpan(actions, 3);

        var logLabel = new Label { Text = "Log", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        root.Controls.Add(logLabel, 0, 5);
        root.SetColumnSpan(logLabel, 3);

        root.Controls.Add(_log, 0, 6);
        root.SetColumnSpan(_log, 3);

        Controls.Add(root);
    }

    // ------------------------------------------------------------- settings

    void LoadSettings()
    {
        _adbBox.Text = @"D:\Downloads\platform-tools-latest-windows\platform-tools\adb.exe";
        _pkgBox.Text = "com.ea.game.pvzfree_row";
        _destBox.Text = @"D:\pvz-backup";

        try
        {
            if (!File.Exists(SettingsPath)) return;
            foreach (var line in File.ReadAllLines(SettingsPath))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var (k, v) = (line[..i].Trim(), line[(i + 1)..].Trim());
                if (v.Length == 0) continue;
                switch (k)
                {
                    case "adb": _adbBox.Text = v; break;
                    case "package": _pkgBox.Text = v; break;
                    case "dest": _destBox.Text = v; break;
                }
            }
        }
        catch { /* defaults are fine */ }
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllLines(SettingsPath,
            [
                $"adb={_adbBox.Text.Trim()}",
                $"package={_pkgBox.Text.Trim()}",
                $"dest={_destBox.Text.Trim()}",
            ]);
        }
        catch { /* non-fatal */ }
    }

    // ------------------------------------------------------------- plumbing

    void Log(string msg)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => Log(msg)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }

    void SetBusy(bool busy)
    {
        if (InvokeRequired) { BeginInvoke(() => SetBusy(busy)); return; }
        _btnBackup.Enabled = _btnRestore.Enabled = !busy;
        _btnCancel.Enabled = busy;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    Adb RequireAdb()
    {
        var p = _adbBox.Text.Trim();
        if (!File.Exists(p))
            throw new FileNotFoundException($"adb.exe not found at:\n{p}\n\nUse Browse... to locate it.");
        return new Adb(p);
    }

    string RequirePackage()
    {
        var p = _pkgBox.Text.Trim();
        if (p.Length == 0 || !Regex.IsMatch(p, @"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+$"))
            throw new InvalidOperationException($"'{p}' is not a valid package name.");
        return p;
    }

    async Task<Adb> RequireReadyDeviceAsync(CancellationToken ct)
    {
        var adb = RequireAdb();
        var devices = await adb.ListDevicesAsync(ct);

        if (devices.Count == 0)
            throw new InvalidOperationException("No device detected.\n\nCheck the USB cable and that USB debugging is enabled.");

        var unauthorized = devices.FirstOrDefault(d => d.State == "unauthorized");
        if (unauthorized is not null)
            throw new InvalidOperationException(
                "Device is unauthorized.\n\nUnlock the phone and tap Allow on the 'Allow USB debugging?' prompt, then press Refresh.");

        var ready = devices.FirstOrDefault(d => d.IsReady)
            ?? throw new InvalidOperationException($"Device is in state '{devices[0].State}', not ready.");

        if (devices.Count(d => d.IsReady) > 1)
            throw new InvalidOperationException("More than one device is connected. Disconnect the others.");

        Log($"Device: {ready.Model} ({ready.Serial})");
        return adb;
    }

    async Task RunGuarded(Func<CancellationToken, Task> work)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        SaveSettings();

        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("== CANCELLED ==");
        }
        catch (Exception ex)
        {
            Log($"!! ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task RefreshDeviceAsync()
    {
        try
        {
            var adb = RequireAdb();
            var devices = await adb.ListDevicesAsync(CancellationToken.None);

            _status.Text = devices.Count switch
            {
                0 => "no device detected - check cable / USB debugging",
                _ => string.Join("   |   ", devices.Select(d => $"{d.Model} [{d.State}]")),
            };
            _status.ForeColor = devices.Any(d => d.IsReady) ? Color.DarkGreen : Color.Firebrick;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message.Split('\n')[0];
            _status.ForeColor = Color.Firebrick;
        }
    }

    async Task PickPackageAsync()
    {
        try
        {
            var adb = await RequireReadyDeviceAsync(CancellationToken.None);
            var r = await adb.ShellAsync("pm list packages -3", CancellationToken.None, 60_000);

            var pkgs = r.StdOut.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("package:"))
                .Select(l => l["package:".Length..])
                .OrderBy(p => p)
                .ToList();

            if (pkgs.Count == 0) { Log("No third-party packages returned."); return; }

            using var dlg = new PackagePicker(pkgs);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Selected is { } sel)
                _pkgBox.Text = sel;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // --------------------------------------------------------------- BACKUP

    async Task BackupAsync(CancellationToken ct)
    {
        _log.Clear();
        Log("=== BACKUP ===");

        var adb = await RequireReadyDeviceAsync(ct);
        var pkg = RequirePackage();
        var dest = _destBox.Text.Trim();

        if (dest.Length == 0) throw new InvalidOperationException("Choose a destination folder first.");
        Directory.CreateDirectory(dest);

        var installed = await adb.ShellAsync($"pm list packages {pkg}", ct, 30_000);
        if (!installed.StdOut.Contains($"package:{pkg}"))
            throw new InvalidOperationException($"'{pkg}' is not installed on the device.");
        Log($"Package: {pkg}");

        var before = await adb.ListBackupZipsAsync(DeviceBackupDir, ct);
        Log($"Existing backups on device: {before.Count}");

        Log("Opening the MIUI backup screen on the phone...");
        await adb.ShellAsync($"am start -n {BackupActivity}", ct, 30_000);

        Log("");
        Log("  >> ON THE PHONE, DO THIS NOW:");
        Log("     1. Unlock, and enter your PIN if asked");
        Log("     2. Tap Agree if a permission screen appears");
        Log($"     3. Deselect everything, then tick ONLY your app");
        Log("     4. Tap 'Back up'");
        Log("");
        Log("Waiting for a new backup to appear (up to 15 min)...");

        string? newZip = null;
        var deadline = Environment.TickCount64 + 15 * 60 * 1000;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var now = await adb.ListBackupZipsAsync(DeviceBackupDir, ct);
            var added = now.Except(before).ToList();
            if (added.Count > 0) { newZip = added[0]; break; }
            await Task.Delay(3000, ct);
        }

        if (newZip is null)
            throw new TimeoutException("No new backup appeared within 15 minutes.");

        Log($"Found: {newZip}");

        var devicePath = $"{DeviceBackupDir}/{newZip}";
        var size = await adb.WaitForStableSizeAsync(devicePath, ct, Log);
        if (size <= 0) throw new InvalidOperationException("Backup file disappeared before it could be pulled.");
        Log($"Finished writing: {Fmt.Bytes(size)}");

        var folder = Path.Combine(dest, Path.GetFileNameWithoutExtension(newZip));
        Directory.CreateDirectory(folder);
        var localZip = Path.Combine(folder, newZip);

        Log("Pulling to PC...");
        var pull = await adb.RunAsync($"pull \"{devicePath}\" \"{localZip}\"", ct, 30 * 60 * 1000);
        if (!pull.Ok && !File.Exists(localZip))
            throw new InvalidOperationException($"adb pull failed:\n{pull.All}");

        var localSize = new FileInfo(localZip).Length;
        if (localSize != size)
            throw new InvalidDataException($"Size mismatch: device {size}, local {localSize}. The pull was incomplete.");
        Log($"Pulled OK, byte-for-byte match ({Fmt.Bytes(localSize)})");

        Log("Validating archive...");
        var info = BackupArchive.Inspect(localZip);
        Log($"  contains: {string.Join(", ", info.Packages)}");
        Log($"  payload:  {info.BakEntryName} ({Fmt.Bytes(info.BakSize)})");

        if (!info.Packages.Contains(pkg))
            Log($"  ! warning: '{pkg}' not listed in the archive manifest");

        Log("Extracting readable save data...");
        var saveDir = Path.Combine(folder, "SAVE-DATA-ONLY");
        var n = await Task.Run(() => BackupArchive.ExtractSaveData(localZip, saveDir, Log, ct), ct);

        if (n >= 0) Log($"  extracted {n} files -> SAVE-DATA-ONLY\\");

        Log("");
        Log($"== DONE ==  {folder}");
        OpenFolder(folder);
    }

    // -------------------------------------------------------------- RESTORE

    async Task RestoreAsync(CancellationToken ct)
    {
        _log.Clear();
        Log("=== RESTORE ===");

        string zipPath;
        using (var d = new OpenFileDialog
        {
            Title = "Choose the backup zip to restore",
            Filter = "MIUI backup (*.zip)|*.zip|All files|*.*",
            InitialDirectory = Directory.Exists(_destBox.Text) ? _destBox.Text : null,
        })
        {
            if (d.ShowDialog(this) != DialogResult.OK) { Log("Cancelled at file picker."); return; }
            zipPath = d.FileName;
        }

        Log($"Archive: {zipPath}");
        var info = BackupArchive.Inspect(zipPath);
        Log($"  contains: {string.Join(", ", info.Packages)}");
        Log($"  size:     {Fmt.Bytes(new FileInfo(zipPath).Length)}");

        var adb = await RequireReadyDeviceAsync(ct);

        var confirm = MessageBox.Show(this,
            $"Restore these apps?\n\n  {string.Join("\n  ", info.Packages)}\n\n" +
            "WARNING: MIUI restore ERASES the app's current data before writing the backup back.\n\n" +
            "Any progress newer than this backup will be lost.\n\nContinue?",
            "Confirm restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.Yes) { Log("Cancelled at confirmation."); return; }

        // MIUI only lists files named like 20260905_170621.zip
        var name = Path.GetFileName(zipPath);
        if (!Regex.IsMatch(name, @"^\d{8}_\d{6}\.zip$", RegexOptions.IgnoreCase))
        {
            var renamed = $"{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            Log($"  name '{name}' is not in MIUI format; pushing as '{renamed}'");
            name = renamed;
        }

        await adb.ShellAsync($"mkdir -p {DeviceBackupDir}", ct, 30_000);

        Log("Pushing to phone...");
        var push = await adb.RunAsync($"push \"{zipPath}\" \"{DeviceBackupDir}/{name}\"", ct, 30 * 60 * 1000);
        if (!push.Ok) throw new InvalidOperationException($"adb push failed:\n{push.All}");

        var onDevice = await adb.FileSizeAsync($"{DeviceBackupDir}/{name}", ct);
        var local = new FileInfo(zipPath).Length;
        if (onDevice != local)
            throw new InvalidDataException($"Push verification failed: local {local}, device {onDevice}.");
        Log($"Pushed OK, byte-for-byte match ({Fmt.Bytes(onDevice)})");

        Log("Opening the MIUI restore screen on the phone...");
        await adb.ShellAsync($"am start -n {BackupActivity}", ct, 30_000);

        Log("");
        Log("  >> ON THE PHONE, DO THIS NOW:");
        Log("     1. Unlock, and enter your PIN if asked");
        Log($"     2. Open the backup dated {name.Replace(".zip", "")}");
        Log("     3. Tick your app, then tap 'Restore'");
        Log("     4. Leave the phone alone until it finishes");
        Log("");
        Log("== ARCHIVE IS ON THE PHONE - finish the restore on the device ==");
    }

    static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }
}

/// <summary>Simple filterable list for choosing an installed package.</summary>
public sealed class PackagePicker : Form
{
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox _filter = new() { Dock = DockStyle.Top };
    readonly List<string> _all;

    public string? Selected { get; private set; }

    public PackagePicker(List<string> packages)
    {
        _all = packages;
        Text = "Installed packages";
        Size = new Size(520, 560);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;

        _filter.PlaceholderText = "type to filter...";
        _filter.TextChanged += (_, _) => Populate(_filter.Text);

        _list.DoubleClick += (_, _) => Accept();

        var ok = new Button { Text = "OK", Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.None };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);

        Controls.Add(_list);
        Controls.Add(_filter);
        Controls.Add(bottom);
        CancelButton = cancel;

        Populate("");
    }

    void Populate(string filter)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _all)
            if (filter.Length == 0 || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _list.Items.Add(p);
        _list.EndUpdate();
    }

    void Accept()
    {
        if (_list.SelectedItem is string s) { Selected = s; DialogResult = DialogResult.OK; Close(); }
    }
}
