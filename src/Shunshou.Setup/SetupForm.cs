using System.Diagnostics;
using Shunshou.Deployment;
using Shunshou.DesktopIntegration;

namespace Shunshou.Setup;

internal sealed record SetupEnvironment(string TargetDirectory, string DesktopDirectory, string LockDirectory, IInstallationRegistration Registration);

internal sealed class SetupForm : Form
{
    private readonly TextBox _directory = new() { Dock = DockStyle.Fill, Margin = new(0, 0, 10, 0) };
    private readonly Button _browse = new() { Text = "选择文件夹…", AutoSize = true, Padding = new(12, 5, 12, 5) };
    private readonly CheckBox _shortcut = new() { Text = "在桌面创建快捷方式", Checked = true, AutoSize = true };
    private readonly CheckBox _launch = new() { Text = "完成后打开顺手工具箱", Checked = true, AutoSize = true };
    private readonly Label _detail = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(80, 94, 111), Margin = new(0, 12, 0, 16) };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 16, 0, 8) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 6, Visible = false };
    private readonly Button _start = new() { Text = "解压并使用", AutoSize = true, Padding = new(22, 9, 22, 9), BackColor = Color.FromArgb(0, 116, 124), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _cancelButton = new() { Text = "关闭", AutoSize = true, Padding = new(15, 9, 15, 9) };
    private readonly Button _openDirectory = new() { Text = "打开软件文件夹", AutoSize = true, Padding = new(12, 9, 12, 9), Visible = false };
    private readonly PayloadMetadata? _metadata;
    private CancellationTokenSource? _cancellation;
    private bool _finished;
    private string? _installedDirectory;
    private int _targetRevision;
    private readonly SetupEnvironment? _environment;
    private readonly IInstallationRegistration _registration;
    private readonly DesktopShortcutService _shortcuts;
    private static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ShunshouToolbox");

    public SetupForm(bool discoverPrevious = true, SetupEnvironment? environment = null)
    {
        _environment = environment;
        _registration = environment?.Registration ?? new InstallationRegistry();
        _shortcuts = new DesktopShortcutService(environment?.DesktopDirectory, _registration);
        if (environment is not null) _launch.Checked = false;
        Text = "顺手工具箱 · 解压与更新";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(247, 249, 251);
        ForeColor = Color.FromArgb(28, 36, 44);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 535);
        MinimumSize = new Size(700, 545);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(32, 26, 32, 24), ColumnCount = 1, RowCount = 8 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "顺手工具箱", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true, Margin = new(0, 0, 0, 7) }, 0, 0);
        root.Controls.Add(new Label { Text = "解压即可使用，以后也在这里更新。", AutoSize = true, ForeColor = _detail.ForeColor, Margin = new(0, 0, 0, 26) }, 0, 1);
        string versionText;
        try { _metadata = Payload.ReadMetadata(); versionText = $"软件位置  ·  版本 {_metadata.Version}"; }
        catch (Exception ex) { versionText = "软件位置"; _status.Text = ex.Message; _start.Enabled = false; }
        root.Controls.Add(new Label { Text = versionText, AutoSize = true, Margin = new(0, 0, 0, 10) }, 0, 2);
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new(SizeType.AutoSize));
        pathRow.Controls.Add(_directory, 0, 0);
        pathRow.Controls.Add(_browse, 1, 0);
        root.Controls.Add(pathRow, 0, 3);
        root.Controls.Add(_detail, 0, 4);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Margin = Padding.Empty, WrapContents = false };
        _shortcut.Margin = new(0, 0, 0, 10);
        _launch.Margin = Padding.Empty;
        options.Controls.AddRange([_shortcut, _launch]);
        root.Controls.Add(options, 0, 5);
        var feedback = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        feedback.Controls.Add(_status, 0, 0);
        feedback.Controls.Add(_progress, 0, 1);
        root.Controls.Add(feedback, 0, 6);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Margin = new(0, 20, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.Controls.Add(new Label { Text = "本地处理 · 离线可用", AutoSize = true, ForeColor = _start.BackColor, Anchor = AnchorStyles.Left }, 0, 0);
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.AddRange([_openDirectory, _cancelButton, _start]);
        footer.Controls.Add(actions, 1, 0);
        root.Controls.Add(footer, 0, 7);
        AcceptButton = _start;
        CancelButton = _cancelButton;
        _start.FlatAppearance.BorderSize = 0;
        _directory.TextChanged += (_, _) => RefreshTargetSummary();
        _browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "选择软件文件夹。更新时请选择旧版程序所在的文件夹。", UseDescriptionForTitle = true, InitialDirectory = Directory.Exists(_directory.Text) ? _directory.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            if (picker.ShowDialog(this) == DialogResult.OK) _directory.Text = picker.SelectedPath;
        };
        _start.Click += async (_, _) => { if (_finished) Close(); else await RunAsync(); };
        _cancelButton.Click += (_, _) => { if (_cancellation is not null) { _cancellation.Cancel(); _status.Text = "正在安全停止，请稍候…"; } else Close(); };
        _openDirectory.Click += (_, _) => { if (_installedDirectory is not null) Process.Start(new ProcessStartInfo(_installedDirectory) { UseShellExecute = true }); };
        FormClosing += (_, e) =>
        {
            if (_cancellation is null) return;
            e.Cancel = true;
            _cancellation.Cancel();
            _status.Text = "正在安全停止，请稍候…";
        };
        string? previous = null;
        if (discoverPrevious) { try { previous = _registration.ReadLastDirectory(); } catch { /* The user can choose a path. */ } }
        _directory.Text = environment?.TargetDirectory ?? previous ?? DefaultDirectory;
    }

    private async void RefreshTargetSummary()
    {
        int revision = ++_targetRevision;
        string path = _directory.Text.Trim();
        bool existing = false;
        await Task.Delay(180);
        if (revision != _targetRevision || IsDisposed) return;
        if (IsLocalPath(path)) existing = await Task.Run(() => PackageIdentity.IsValidDirectory(path));
        if (revision != _targetRevision || IsDisposed || _finished || _cancellation is not null) return;
        _detail.Text = existing
            ? "将更新这个文件夹中的软件，保留处理记录和文件。旧版另存为备份。"
            : "首次使用请选择一个空文件夹；更新时请选择旧版软件所在的文件夹。";
        if (!_finished) _start.Text = existing ? $"更新到 {_metadata?.Version}" : "解压并使用";
    }

    private static bool IsLocalPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
                && !path.StartsWith("//", StringComparison.Ordinal)
                && new DriveInfo(Path.GetPathRoot(path)!).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch { return false; }
    }

    internal static void RequireStopped(string target)
    {
        var blockers = UpdateCoordination.FindRunningAppProcesses(target);
        if (blockers.Count > 0)
            throw new IOException("顺手工具箱或它的处理任务还在运行。请保存结果并关闭软件，再重新开始更新。");
    }

    private async Task RunAsync()
    {
        if (_metadata is null) return;
        string? temporary = null;
        _cancellation = new();
        _directory.Enabled = _browse.Enabled = _shortcut.Enabled = _launch.Enabled = _start.Enabled = false;
        _cancelButton.Text = "取消";
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        _status.ForeColor = ForeColor;
        try
        {
            if (!IsLocalPath(_directory.Text.Trim())) throw new IOException("请选择本地磁盘上的软件文件夹。");
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_directory.Text.Trim()));
            if (target.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(target)!), StringComparison.OrdinalIgnoreCase))
                throw new IOException("请为软件选择一个单独的文件夹，不能使用磁盘根目录。");
            // A clean Windows profile may not yet have its per-user Programs directory.
            if (target.Equals(DefaultDirectory, StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Environment.ProcessPath!.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请先把这个 EXE 更新包移到软件文件夹之外，再打开更新。");
            var progress = new Progress<DeploymentProgress>(p =>
            {
                if (_finished || _cancellation is null) return;
                _status.Text = p.Message;
                if (p.Percent is { } percent) { _progress.Style = ProgressBarStyle.Continuous; _progress.Value = Math.Clamp(percent, 0, 100); }
                else _progress.Style = ProgressBarStyle.Marquee;
            });
            DeploymentResult result;
            using (var lease = UpdateCoordination.AcquireUpdateLease(target, _environment?.LockDirectory)
                ?? throw new IOException("软件仍在运行，或另一个更新包正在处理这个文件夹。请先保存并退出后重试。"))
            {
                RequireStopped(target);
                var service = new DeploymentService();
                await Task.Run(() => service.RecoverAsync(target, progress, _cancellation.Token));
                temporary = Path.Combine(Path.GetTempPath(), "ShunshouSetup", Guid.NewGuid().ToString("N"));
                var zip = await Payload.ExtractAsync(temporary, _metadata, new Progress<string>(s => { if (!_finished && _cancellation is not null) _status.Text = s; }), _cancellation.Token);
                var request = new DeploymentRequest(zip, _metadata.Sha256, target)
                {
                    ExpectedVersion = _metadata.Version, ZipRoot = _metadata.ZipRoot,
                    BeforeCommit = () => RequireStopped(target)
                };
                result = await Task.Run(() => service.InstallOrUpdateAsync(request, progress, _cancellation.Token));
            }
            _installedDirectory = result.TargetDirectory;
            var notes = new List<string>();
            try { _registration.RememberDirectory(result.TargetDirectory); }
            catch { notes.Add("本次未能记住软件位置，下次更新时可手动选择。"); }
            notes.AddRange(SetupShortcutPreference.Apply(_shortcuts, result.TargetDirectory, _shortcut.Checked));
            _finished = true;
            _openDirectory.Visible = true;
            _start.Text = "完成";
            _detail.Text = result.PreviousBackupDirectory is null
                ? "软件已准备好。请保留软件文件夹，桌面快捷方式会从这里打开它。"
                : "旧版备份：" + result.PreviousBackupDirectory;
            _status.Text = (result.WasUpdate ? "更新完成。" : "解压完成。") + string.Join(" ", notes);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;
            if (_launch.Checked)
            {
                try
                {
                    var executable = PackageIdentity.GetExecutablePath(result.TargetDirectory)
                        ?? throw new IOException("软件主程序校验失败，请重新解压完整软件包。");
                    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = result.TargetDirectory });
                }
                catch (Exception ex) { _status.Text += " 自动打开失败，请从软件文件夹启动：" + ex.Message; }
            }
        }
        catch (OperationCanceledException) { _status.Text = "已取消。原来的软件仍保留。"; }
        catch (Exception ex) { _status.Text = ex.Message; _status.ForeColor = Color.FromArgb(163, 38, 38); }
        finally
        {
            if (temporary is not null) Payload.DeleteOwnTemporaryDirectory(temporary);
            _cancellation.Dispose();
            _cancellation = null;
            _start.Enabled = true;
            _cancelButton.Text = "关闭";
            if (!_finished)
            {
                _directory.Enabled = _browse.Enabled = _shortcut.Enabled = _launch.Enabled = true;
                _progress.Visible = false;
            }
        }
    }

    internal async Task VerifyUserFlowAsync(string screenshotPath)
    {
        if (_environment is null) throw new InvalidOperationException("UI verification requires isolated desktop, registry and lock fixtures.");
        await RunAsync();
        if (!_finished || _installedDirectory != _environment.TargetDirectory || !_start.Enabled || !_openDirectory.Visible || _cancellation is not null)
            throw new InvalidOperationException("Setup UI did not complete correctly: " + _status.Text);
        if (!File.Exists(Path.Combine(_environment.DesktopDirectory, "顺手工具箱.lnk")))
            throw new InvalidOperationException("The setup checkbox did not create the fixture shortcut.");
        await Task.Delay(250);
        PerformLayout();
        if (_progress.Value != 100 || _status.Text != "解压完成。")
            throw new InvalidOperationException("A queued progress event overwrote the completion state.");
        var statusBounds = RectangleToClient(_status.RectangleToScreen(_status.ClientRectangle));
        var startBounds = RectangleToClient(_start.RectangleToScreen(_start.ClientRectangle));
        if (!ClientRectangle.Contains(statusBounds) || !ClientRectangle.Contains(startBounds))
            throw new InvalidOperationException("Setup status or final action is clipped.");
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
    }
}
