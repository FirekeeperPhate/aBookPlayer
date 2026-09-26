using System.ComponentModel;
using System.Drawing.Drawing2D;
using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>Local Whisper transcription window: model/language choice, progress and a live text preview.</summary>
sealed class TranscribeForm : Form
{
    static readonly (string Code, string Name)[] Languages =
    [
        ("auto", "Auto-detect"), ("en", "English"), ("it", "Italian"), ("fr", "French"),
        ("de", "German"), ("es", "Spanish"), ("pt", "Portuguese"), ("nl", "Dutch"),
        ("ru", "Russian"), ("ja", "Japanese"), ("zh", "Chinese"),
    ];

    readonly string _audioPath;
    readonly IReadOnlyList<Chapter> _chapters;
    readonly AppSettings _settings;

    readonly ComboBox _cmbModel = MakeCombo(470);
    readonly ComboBox _cmbLanguage = MakeCombo(220);
    readonly Label _lblModelInfo = new() { AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(420, 0) };
    readonly CheckBox _chkText = new() { Text = "Also save the transcript as a .txt file", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly CheckBox _chkGpu = new() { Text = "Use the graphics card (GPU): much faster with a dedicated card", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly Label _lblGpuInfo = new() { AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(420, 0) };
    readonly ProgressView _progress = new() { Dock = DockStyle.Top, Height = 8 };
    readonly Label _lblStatus = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Theme.TextDim, Padding = new Padding(0, 8, 0, 0), AutoEllipsis = true, UseMnemonic = false };
    readonly TextBox _log = new()
    {
        // MaxLength 0 = no limit: the default 32K would silently stop the preview on long books
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, MaxLength = 0,
        BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f),
    };
    readonly Button _btnStart = MakeButton("Start transcription", 160, 34);
    readonly Button _btnClose = MakeButton("Close", 110, 34);

    CancellationTokenSource? _cts;
    bool _closeRequested;

    public TranscribeForm(string audioPath, IReadOnlyList<Chapter> chapters, AppSettings settings)
    {
        _audioPath = audioPath;
        _chapters = chapters;
        _settings = settings;

        SuspendLayout();
        Text = "Transcribe with Whisper";
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(680, 610);
        MinimumSize = new Size(560, 500);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(18, 16, 18, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "File", new Label { Text = Path.GetFileName(audioPath), AutoSize = true, UseMnemonic = false, MaximumSize = new Size(420, 0) });
        AddRow(grid, "Model", _cmbModel);
        AddRow(grid, "", _lblModelInfo);
        AddRow(grid, "Language", _cmbLanguage);
        AddRow(grid, "", _chkText);
        AddRow(grid, "Speed", _chkGpu);
        AddRow(grid, "", _lblGpuInfo);

        var progressHost = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(18, 12, 18, 0) };
        progressHost.Controls.Add(_lblStatus);
        progressHost.Controls.Add(_progress);

        var logFrame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(10, 8, 4, 8) };
        logFrame.Controls.Add(_log);
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 4) };
        logHost.Controls.Add(logFrame);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(18, 10, 18, 16) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_btnStart, 1, 0);
        buttons.Controls.Add(_btnClose, 2, 0);

        Controls.Add(logHost);
        Controls.Add(buttons);
        Controls.Add(progressHost);
        Controls.Add(grid);

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        _cmbModel.MaxDropDownItems = WhisperModels.All.Length;
        foreach (var m in WhisperModels.All) _cmbModel.Items.Add($"{m.Name}  —  {FormatSize(m.SizeMb)}, {m.Note}");
        int modelIndex = Array.FindIndex(WhisperModels.All, m => m.Id == settings.WhisperModel);
        _cmbModel.SelectedIndex = modelIndex >= 0 ? modelIndex : Array.FindIndex(WhisperModels.All, m => m.Type == Whisper.net.Ggml.GgmlType.BaseEn);
        foreach (var l in Languages) _cmbLanguage.Items.Add(l.Name);
        int langIndex = Array.FindIndex(Languages, l => l.Code == settings.WhisperLanguage);
        _cmbLanguage.SelectedIndex = langIndex >= 0 ? langIndex : 1;
        _chkText.Checked = settings.WhisperSaveText;
        _chkGpu.Checked = settings.WhisperUseGpu && GpuSupport.IsDriverAvailable;
        _chkGpu.Enabled = GpuSupport.IsDriverAvailable;
        _chkGpu.CheckedChanged += (_, _) => UpdateGpuInfo();
        UpdateGpuInfo();

        _cmbModel.SelectedIndexChanged += (_, _) => UpdateModelInfo();
        _btnStart.Click += (_, _) => StartOrCancel();
        _btnClose.Click += (_, _) => Close();
        UpdateModelInfo();
        _lblStatus.Text = "Transcription runs entirely on this PC.";
    }

    /// <summary>Path of the created .srt file if transcription succeeded.</summary>
    public string? SrtPath { get; private set; }

    WhisperModelInfo SelectedModel => WhisperModels.All[Math.Max(0, _cmbModel.SelectedIndex)];

    void UpdateModelInfo()
    {
        _lblModelInfo.Text = SelectedModel.IsDownloaded
            ? "✓ Model already on this PC."
            : $"The model will be downloaded once from Hugging Face ({FormatSize(SelectedModel.SizeMb)}), then it works offline.";

        // ".en" models only recognize English
        if (SelectedModel.IsEnglishOnly)
            _cmbLanguage.SelectedIndex = Array.FindIndex(Languages, l => l.Code == "en");
        _cmbLanguage.Enabled = !SelectedModel.IsEnglishOnly && _cts == null;
    }

    void UpdateGpuInfo()
    {
        _lblGpuInfo.Text =
            !GpuSupport.IsDriverAvailable ? "No Vulkan graphics driver found on this PC: the CPU is used."
            : !_chkGpu.Checked ? "The CPU is used."
            : GpuSupport.NeedsRestart ? "GPU support is installed: restart aBookPlayer to use it (the CPU is used until then)."
            : GpuSupport.IsInstalled ? "✓ GPU support on this PC (Vulkan). If the card cannot be used, the CPU takes over."
            : $"GPU support will be downloaded once from nuget.org ({GpuSupport.DownloadBytes / (1024 * 1024)} MB).";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
        Theme.UseDarkScrollBars(_log);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts != null)
        {
            // Only close once the running work has stopped
            e.Cancel = true;
            _closeRequested = true;
            _cts.Cancel();
            _lblStatus.Text = "Cancelling…";
        }
        base.OnFormClosing(e);
    }

    void StartOrCancel()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _btnStart.Enabled = false;
            _lblStatus.Text = "Cancelling…";
        }
        else
        {
            _ = RunAsync();
        }
    }

    async Task RunAsync()
    {
        var model = SelectedModel;
        var language = Languages[Math.Max(0, _cmbLanguage.SelectedIndex)].Code;
        _settings.WhisperModel = model.Id;
        _settings.WhisperLanguage = language;
        _settings.WhisperSaveText = _chkText.Checked;
        bool useGpu = _chkGpu.Checked;
        if (_chkGpu.Enabled) _settings.WhisperUseGpu = useGpu;
        bool downloadGpu = useGpu && !GpuSupport.IsInstalled;

        if (!model.IsDownloaded || downloadGpu)
        {
            var what = new List<string>();
            if (!model.IsDownloaded) what.Add($"• the \"{model.Name}\" speech model from Hugging Face ({FormatSize(model.SizeMb)})");
            if (downloadGpu) what.Add($"• GPU support (Vulkan) from nuget.org ({GpuSupport.DownloadBytes / (1024 * 1024)} MB)");
            if (MessageBox.Show(this,
                    $"This will be downloaded once and kept on this PC:\n{string.Join("\n", what)}\n\n" +
                    "After the download, transcription works without an internet connection. Continue?",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
        }

        var srtPath = ChooseOutputPath();
        if (srtPath == null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetRunning(true);
        _log.Clear();
        _progress.Value = 0;

        try
        {
            if (downloadGpu)
            {
                await GpuSupport.DownloadAsync(new Progress<long>(bytes =>
                {
                    _progress.Value = Math.Min(0.99, bytes / (double)GpuSupport.DownloadBytes);
                    _lblStatus.Text = $"Downloading GPU support: {bytes / (1024 * 1024)} / {GpuSupport.DownloadBytes / (1024 * 1024)} MB";
                }), ct);
                UpdateGpuInfo();
                _progress.Value = 0;
            }

            if (!model.IsDownloaded)
            {
                long expected = model.SizeMb * 1024L * 1024L;
                await WhisperModels.DownloadAsync(model, new Progress<long>(bytes =>
                {
                    _progress.Value = Math.Min(0.99, bytes / (double)expected);
                    _lblStatus.Text = $"Downloading {model.Name} model: {bytes / (1024 * 1024)} / ~{model.SizeMb} MB";
                }), ct);
                UpdateModelInfo();
            }

            var cues = await Transcriber.TranscribeAsync(_audioPath, model.FilePath, language, useGpu,
                new Progress<TranscriptionProgress>(p =>
                {
                    _progress.Value = p.Fraction;
                    _lblStatus.Text = p.Status;
                    if (p.LogLine != null) _log.AppendText(p.LogLine + Environment.NewLine);
                }), ct);

            if (cues.Count == 0)
            {
                MessageBox.Show(this, "No speech was recognized in the file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SrtPath = SaveResult(srtPath, cues);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "Transcription cancelled.";
        }
        catch (InvalidModelException ex)
        {
            // Remove the damaged file so the next attempt downloads it again
            try { File.Delete(ex.ModelPath); } catch { /* reported below either way */ }
            UpdateModelInfo();
            _lblStatus.Text = "The speech model was damaged.";
            if (!_closeRequested)
                MessageBox.Show(this,
                    $"{ex.Message}\n\nThe damaged file has been removed: start the transcription again to download the model again.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Transcription failed.";
            if (!_closeRequested)
                MessageBox.Show(this, $"Transcription failed:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }

        if (SrtPath != null) DialogResult = DialogResult.OK;   // closes the dialog
        else if (_closeRequested) Close();
    }

    /// <summary>Normally next to the audio file with the same name (so it loads automatically); asks before overwriting.</summary>
    string? ChooseOutputPath()
    {
        var path = Path.ChangeExtension(_audioPath, ".srt");
        if (!File.Exists(path)) return path;

        var answer = MessageBox.Show(this,
            $"\"{Path.GetFileName(path)}\" already exists.\n\nOverwrite it? Choose No to save under a different name.",
            Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) return path;
        if (answer == DialogResult.Cancel) return null;
        return AskSavePath(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(_audioPath) + ".whisper.srt");
    }

    string? AskSavePath(string? folder, string fileName)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "SubRip subtitles (*.srt)|*.srt",
            InitialDirectory = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = fileName,
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    /// <summary>
    /// Writes the .srt (and the optional .txt). A finished transcription can take a long time, so a write
    /// failure (read-only folder, network share, …) never discards it: the user can pick another location.
    /// Returns the saved .srt path, or null if the user gave up.
    /// </summary>
    string? SaveResult(string srtPath, List<SubtitleCue> cues)
    {
        while (true)
        {
            try
            {
                SubtitleTrack.WriteSrt(srtPath, cues);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var answer = MessageBox.Show(this,
                    $"Could not save \"{srtPath}\":\n{ex.Message}\n\nSave the transcription somewhere else?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                var other = answer == DialogResult.Yes
                    ? AskSavePath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.GetFileName(srtPath))
                    : null;
                if (other == null)
                {
                    if (MessageBox.Show(this, "Discard the transcription? It has not been saved.", Text,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        return null;
                    continue;
                }
                srtPath = other;
            }
        }

        if (_chkText.Checked)
        {
            var txtPath = Path.ChangeExtension(srtPath, ".txt");
            try { SubtitleTrack.WriteTranscript(txtPath, cues, _chapters); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"The subtitles were saved, but the text transcript could not be:\n{ex.Message}",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        return srtPath;
    }

    void SetRunning(bool running)
    {
        _cmbModel.Enabled = _chkText.Enabled = !running;
        _chkGpu.Enabled = !running && GpuSupport.IsDriverAvailable;
        _cmbLanguage.Enabled = !running && !SelectedModel.IsEnglishOnly;
        _btnStart.Text = running ? "Cancel" : "Start transcription";
        _btnStart.Enabled = true;
        UseWaitCursor = false;
    }

    static string FormatSize(int mb) => mb >= 1000 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";

}

/// <summary>Dark-themed progress bar (the system ProgressBar cannot be themed dark).</summary>
sealed class ProgressView : Control
{
    double _value;

    public ProgressView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = Height, y = h / 2f;
        using (var track = new Pen(Theme.Track, h) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, h / 2, y, Width - h / 2, y);
        if (_value > 0)
        {
            using var fill = new Pen(Theme.Accent, h) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(fill, h / 2, y, h / 2 + (float)_value * (Width - h), y);
        }
    }
}
