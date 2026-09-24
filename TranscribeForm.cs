using System.ComponentModel;
using System.Drawing.Drawing2D;

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
    readonly ProgressView _progress = new() { Dock = DockStyle.Top, Height = 8 };
    readonly Label _lblStatus = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Theme.TextDim, Padding = new Padding(0, 8, 0, 0), AutoEllipsis = true, UseMnemonic = false };
    readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f),
    };
    readonly Button _btnStart = MakeButton("Start transcription", 160);
    readonly Button _btnClose = MakeButton("Close", 110);

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
        ClientSize = new Size(680, 600);
        MinimumSize = new Size(560, 480);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(18, 16, 18, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "File", new Label { Text = Path.GetFileName(audioPath), AutoSize = true, UseMnemonic = false, MaximumSize = new Size(420, 0) });
        AddRow(grid, "Model", _cmbModel);
        AddRow(grid, "", _lblModelInfo);
        AddRow(grid, "Language", _cmbLanguage);
        AddRow(grid, "", _chkText);

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

        foreach (var m in WhisperModels.All) _cmbModel.Items.Add($"{m.Name}  —  {FormatSize(m.SizeMb)}, {m.Note}");
        int modelIndex = Array.FindIndex(WhisperModels.All, m => m.Type.ToString() == settings.WhisperModel);
        _cmbModel.SelectedIndex = modelIndex >= 0 ? modelIndex : Array.FindIndex(WhisperModels.All, m => m.Type == Whisper.net.Ggml.GgmlType.BaseEn);
        foreach (var l in Languages) _cmbLanguage.Items.Add(l.Name);
        int langIndex = Array.FindIndex(Languages, l => l.Code == settings.WhisperLanguage);
        _cmbLanguage.SelectedIndex = langIndex >= 0 ? langIndex : 1;
        _chkText.Checked = settings.WhisperSaveText;

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
        _settings.WhisperModel = model.Type.ToString();
        _settings.WhisperLanguage = language;
        _settings.WhisperSaveText = _chkText.Checked;

        if (!model.IsDownloaded && MessageBox.Show(this,
                $"The \"{model.Name}\" model is not on this PC yet.\n\n" +
                $"It will be downloaded once from Hugging Face ({FormatSize(model.SizeMb)}) and saved to:\n{WhisperModels.Folder}\n\n" +
                "After the download, transcription works without an internet connection. Continue?",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        var srtPath = ChooseOutputPath();
        if (srtPath == null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetRunning(true);
        _log.Clear();
        _progress.Value = 0;

        try
        {
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

            var cues = await Transcriber.TranscribeAsync(_audioPath, model.FilePath, language,
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

            SubtitleTrack.WriteSrt(srtPath, cues);
            if (_chkText.Checked) SubtitleTrack.WriteTranscript(Path.ChangeExtension(srtPath, ".txt"), cues, _chapters);
            SrtPath = srtPath;
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "Transcription cancelled.";
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

        using var dlg = new SaveFileDialog
        {
            Filter = "SubRip subtitles (*.srt)|*.srt",
            InitialDirectory = Path.GetDirectoryName(path),
            FileName = Path.GetFileNameWithoutExtension(_audioPath) + ".whisper.srt",
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    void SetRunning(bool running)
    {
        _cmbModel.Enabled = _chkText.Enabled = !running;
        _cmbLanguage.Enabled = !running && !SelectedModel.IsEnglishOnly;
        _btnStart.Text = running ? "Cancel" : "Start transcription";
        _btnStart.Enabled = true;
        UseWaitCursor = false;
    }

    static string FormatSize(int mb) => mb >= 1000 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";

    static ComboBox MakeCombo(int width) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Surface, ForeColor = Theme.Text, Width = width,
    };

    static Button MakeButton(string text, int width)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.Text,
            Size = new Size(width, 34), Margin = new Padding(8, 0, 0, 0), UseMnemonic = false,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        return b;
    }

    static void AddRow(TableLayoutPanel grid, string caption, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            Text = caption, AutoSize = true, ForeColor = Theme.TextDim,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 20, 6),
        }, 0, row);
        control.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(0, 5, 0, 5);
        grid.Controls.Add(control, 1, row);
    }
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
