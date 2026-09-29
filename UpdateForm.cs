namespace aBookPlayer;

/// <summary>
/// A newer version is out: what is new, and one click to download and install it (the installer of the same kind
/// as this copy), skip it, or decide later. The portable copy opens the download page instead.
/// </summary>
sealed class UpdateForm : DarkDialog
{
    public enum Choice { Later, Skip, Install, OpenedPage }

    readonly ReleaseAsset? _installer;
    readonly Button _install, _skip, _later;
    readonly ProgressView _progress = new() { Dock = DockStyle.Top, Height = 8, Visible = false };
    readonly Label _status = new() { Dock = DockStyle.Top, Height = 50, ForeColor = Theme.TextDim, Padding = new Padding(0, 6, 0, 6), UseMnemonic = false };
    CancellationTokenSource? _download;

    public Choice Result { get; private set; } = Choice.Later;

    /// <summary>The downloaded installer, when <see cref="Result"/> is <see cref="Choice.Install"/>.</summary>
    public string? InstallerPath { get; private set; }

    public UpdateForm(ReleaseInfo release, ReleaseAsset? installer) : base("Update available", new Size(620, 460), resizable: true)
    {
        _installer = installer;
        var title = new Label
        {
            Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI Semibold", 12f), UseMnemonic = false,
            Text = $"aBookPlayer {release.Version} is available (you have {UpdateCheck.CurrentVersion})",
        };
        var notes = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Theme.Panel, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10f), TabStop = false,
            Text = UpdateCheck.NotesForDisplay(release.Notes),
        };
        notes.HandleCreated += (_, _) => Theme.UseDarkScrollBars(notes);
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(10, 8, 4, 8) };
        frame.Controls.Add(notes);
        _status.Text = installer != null
            ? $"The installer ({installer.Size / (1024.0 * 1024):0.0} MB) is downloaded from GitHub; settings and positions are kept, and aBookPlayer reopens when done."
            : AppPaths.Portable ? "This is the portable version: download the new zip and replace this folder's files (keep the Data folder)."
            : "Download the new version from the releases page.";

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 4) };
        host.Controls.Add(frame);
        host.Controls.Add(_progress);
        host.Controls.Add(_status);
        host.Controls.Add(title);
        _status.BringToFront();
        _progress.BringToFront();
        frame.BringToFront();
        Controls.Add(host);
        host.BringToFront();

        // Right-to-left button row: the first one added (the main action) is at the right
        _install = AddButton(installer != null ? "Install and restart" : "Open download page", width: 160);
        _skip = AddButton("Skip this version", width: 140);
        _later = AddButton("Later", width: 96);
        // It can appear by itself at startup: Enter (typed for something else) must not start an installation
        CancelButton = _later;
        Shown += (_, _) => _later.Focus();

        _install.Click += async (_, _) => await InstallAsync();
        _skip.Click += (_, _) => { Result = Choice.Skip; DialogResult = DialogResult.OK; };
        _later.Click += (_, _) =>
        {
            if (_download != null) _download.Cancel(); // "Cancel" while downloading
            else DialogResult = DialogResult.Cancel;
        };
    }

    async Task InstallAsync()
    {
        if (_installer == null)
        {
            UpdateCheck.OpenReleasesPage();
            Result = Choice.OpenedPage;
            DialogResult = DialogResult.OK;
            return;
        }
        _download = new CancellationTokenSource();
        _install.Enabled = _skip.Enabled = false;
        _later.Text = "Cancel";
        _progress.Visible = true;
        try
        {
            var size = _installer.Size;
            InstallerPath = await UpdateCheck.DownloadAsync(_installer, new Progress<long>(bytes =>
            {
                _progress.Value = bytes / (double)Math.Max(1, size);
                _status.Text = $"Downloading {bytes / (1024.0 * 1024):0.0} of {size / (1024.0 * 1024):0.0} MB…";
            }), _download.Token);
            Result = Choice.Install;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) _status.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "Could not download the update: " + ex.Message;
        }
        finally
        {
            _download.Dispose();
            _download = null;
            if (!IsDisposed && DialogResult == DialogResult.None)
            {
                _install.Enabled = _skip.Enabled = true;
                _later.Text = "Later";
                _progress.Visible = false;
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _download?.Cancel(); // closed with the X while downloading
        base.OnFormClosing(e);
    }
}
