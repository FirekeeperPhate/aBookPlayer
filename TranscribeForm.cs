using System.ComponentModel;
using System.Drawing.Drawing2D;
using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>
/// Local Whisper transcription window: the books to transcribe (the open one, plus any added to the queue),
/// model/language choice, progress and a live text preview.
/// </summary>
sealed class TranscribeForm : Form
{
    static readonly (string Code, string Name)[] Languages =
    [
        ("auto", "Auto-detect"), ("en", "English"), ("it", "Italian"), ("fr", "French"),
        ("de", "German"), ("es", "Spanish"), ("pt", "Portuguese"), ("nl", "Dutch"),
        ("ru", "Russian"), ("ja", "Japanese"), ("zh", "Chinese"),
    ];

    enum QueueState { Waiting, Running, Done, Failed }

    sealed class QueueItem(string path)
    {
        public string Path { get; } = path;
        public string Name { get; } = BookSource.DisplayName(path);
        public QueueState State { get; set; }
        public string? Detail { get; set; }
        public string? Output { get; set; }
    }

    readonly string _audioPath;
    readonly IReadOnlyList<Chapter> _chapters;
    readonly AppSettings _settings;
    readonly List<QueueItem> _queue = [];

    readonly DarkList _lstQueue = new(28) { Width = 470, Height = 88 };
    readonly Button _btnAddBooks = MakeButton("Add books…", 112, 30);
    readonly Button _btnAddFolder = MakeButton("Add folder…", 112, 30);
    readonly Button _btnRemove = MakeButton("Remove", 90, 30);
    readonly ComboBox _cmbModel = MakeCombo(470);
    readonly ComboBox _cmbLanguage = MakeCombo(220);
    readonly Label _lblModelInfo = new() { AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(420, 0) };
    readonly CheckBox _chkText = new() { Text = "Also save the transcript as a .txt file", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly CheckBox _chkTranslate = new() { AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly CheckBox _chkGpu = new() { Text = "Use the graphics card (GPU): much faster with a dedicated card", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly CheckBox _chkCuda = new() { AutoSize = true, FlatStyle = FlatStyle.Flat };
    /// <summary>CUDA is offered: the PC has an NVIDIA card (see <see cref="NvidiaLibraries.HasCard"/>).</summary>
    readonly bool _cudaOffered = NvidiaLibraries.HasCard;
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
        _queue.Add(new QueueItem(audioPath));

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
        MinimumSize = new Size(560, 520);

        // The queue: the open book first; more can be added and are transcribed one after the other
        var queueButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        foreach (var b in new[] { _btnAddBooks, _btnAddFolder, _btnRemove }) b.Margin = new Padding(0, 0, 8, 0);
        queueButtons.Controls.AddRange([_btnAddBooks, _btnAddFolder, _btnRemove]);
        var queueBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        queueBox.Controls.AddRange([_lstQueue, queueButtons]);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(18, 16, 18, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "Books", queueBox);
        AddRow(grid, "Model", _cmbModel);
        AddRow(grid, "", _lblModelInfo);
        AddRow(grid, "Language", _cmbLanguage);
        AddRow(grid, "", _chkTranslate);
        AddRow(grid, "", _chkText);
        AddRow(grid, "Speed", _chkGpu);
        if (_cudaOffered) AddRow(grid, "", _chkCuda);
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
        _chkTranslate.Checked = settings.WhisperTranslate;
        _chkGpu.Checked = settings.WhisperUseGpu && GpuSupport.IsDriverAvailable;
        _chkGpu.Enabled = GpuSupport.IsDriverAvailable;
        _chkCuda.Checked = settings.WhisperUseCuda;
        _chkGpu.CheckedChanged += (_, _) => UpdateGpuInfo();
        _chkCuda.CheckedChanged += (_, _) => UpdateGpuInfo();
        UpdateGpuInfo();

        _lstQueue.DrawRow = DrawQueueItem;
        _btnAddBooks.Click += (_, _) => AddBooks();
        _btnAddFolder.Click += (_, _) => AddFolder();
        _btnRemove.Click += (_, _) => RemoveSelected();
        RefillQueue();

        _cmbModel.SelectedIndexChanged += (_, _) => UpdateModelInfo();
        _btnStart.Click += (_, _) => StartOrCancel();
        _btnClose.Click += (_, _) => Close();
        UpdateModelInfo();
        _lblStatus.Text = "Transcription runs entirely on this PC.";
    }

    /// <summary>Path of the .srt created for the open book, if its transcription succeeded.</summary>
    public string? SrtPath { get; private set; }

    /// <summary>The .srt is an English translation (to show under the book's own subtitles).</summary>
    public bool Translated { get; private set; }

    bool Translate => _chkTranslate.Checked && _chkTranslate.Enabled;

    /// <summary>Next to the book: "Book.srt", or "Book.en.srt" for a translation (so the subtitles are kept).</summary>
    string OutputPath(string book) => Translate ? BookSource.TranslationPath(book) : BookSource.SubtitlePath(book);

    WhisperModelInfo SelectedModel => WhisperModels.All[Math.Max(0, _cmbModel.SelectedIndex)];

    // ───────────────────────────── Queue ─────────────────────────────

    void AddBooks()
    {
        using var dlg = new OpenFileDialog { Title = "Add books to transcribe", Filter = AudioFormats.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in dlg.FileNames) Enqueue(file);
    }

    void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder with a book's audio files (one book)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) Enqueue(dlg.SelectedPath);
    }

    void Enqueue(string path)
    {
        if (_queue.Any(q => SamePath(q.Path, path))) return;
        _queue.Add(new QueueItem(Path.GetFullPath(path)));
        RefillQueue();
    }

    void RemoveSelected()
    {
        if (_lstQueue.SelectedItem is not QueueItem item || item.State == QueueState.Running || _queue.Count == 1) return;
        _queue.Remove(item);
        RefillQueue();
    }

    void RefillQueue()
    {
        int selected = _lstQueue.SelectedIndex;
        _lstQueue.Items.Clear();
        foreach (var q in _queue) _lstQueue.Items.Add(q);
        _lstQueue.SelectedIndex = Math.Clamp(selected, 0, _queue.Count - 1);
        int waiting = _queue.Count(q => q.State != QueueState.Done);
        _btnStart.Text = _cts != null ? "Cancel"
            : _queue.Count == 1 ? "Start transcription"
            : waiting == 0 ? "All done"
            : waiting == 1 ? "Transcribe 1 book" : $"Transcribe {waiting} books";
        _btnStart.Enabled = _cts != null || waiting > 0 || _queue.Count == 1;
    }

    void SetState(QueueItem item, QueueState state, string? detail = null)
    {
        item.State = state;
        item.Detail = detail;
        _lstQueue.Invalidate();
    }

    void DrawQueueItem(Graphics g, Rectangle r, int index)
    {
        if (_lstQueue.Items[index] is not QueueItem q) return;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        var (text, color) = q.State switch
        {
            QueueState.Running => ("transcribing…", Theme.Accent),
            QueueState.Done => (q.Detail ?? "done", Theme.TextDim),
            QueueState.Failed => ("failed: " + q.Detail, Color.FromArgb(230, 120, 110)),
            _ => ("", Theme.TextDim),
        };
        int pad = _lstQueue.L(8), statusWidth = text.Length > 0 ? _lstQueue.L(170) : 0;
        TextRenderer.DrawText(g, q.Name, _lstQueue.Font, new Rectangle(r.X + pad, r.Y, r.Width - statusWidth - 2 * pad, r.Height),
            q.State == QueueState.Done ? Theme.TextDim : Theme.Text, flags);
        if (text.Length > 0)
            TextRenderer.DrawText(g, text, _lstQueue.Font, new Rectangle(r.Right - statusWidth - pad, r.Y, statusWidth, r.Height), color, flags | TextFormatFlags.Right);
    }

    // ───────────────────────────── Options ─────────────────────────────

    void UpdateModelInfo()
    {
        _lblModelInfo.Text = SelectedModel.IsDownloaded
            ? "✓ Model already on this PC."
            : $"The model will be downloaded once from Hugging Face ({FormatSize(SelectedModel.SizeMb)}), then it works offline.";

        // ".en" models only recognize English
        if (SelectedModel.IsEnglishOnly)
            _cmbLanguage.SelectedIndex = Array.FindIndex(Languages, l => l.Code == "en");
        _cmbLanguage.Enabled = !SelectedModel.IsEnglishOnly && _cts == null;
        _chkTranslate.Enabled = SelectedModel.CanTranslate && _cts == null;
        _chkTranslate.Text = SelectedModel.CanTranslate
            ? "Translate into English (saved as .en.srt, shown under the book's subtitles)"
            : SelectedModel.IsEnglishOnly ? "Translate into English (needs a model that is not \"English only\")"
            : "Translate into English (not with Large v3 Turbo: choose Medium or Small)";
    }

    /// <summary>CUDA is asked for: an NVIDIA card, the graphics card wanted, and CUDA chosen for it.</summary>
    bool CudaChosen => _cudaOffered && _chkGpu.Checked && _chkCuda.Checked;

    static string Megabytes(long bytes) => bytes >= 1000 * 1024 * 1024L ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB" : $"{bytes / (1024 * 1024)} MB";

    void UpdateGpuInfo()
    {
        bool cuda = CudaChosen;
        GpuSupport.PreferCuda = cuda;
        _chkCuda.Enabled = _chkGpu.Enabled && _chkGpu.Checked;
        _chkCuda.Text = "NVIDIA card: use CUDA instead of Vulkan";
        _lblGpuInfo.Text =
            !GpuSupport.IsDriverAvailable ? "No Vulkan graphics driver found on this PC: the CPU is used."
            : !_chkGpu.Checked ? "The CPU is used."
            : GpuSupport.NeedsRestart ? $"{(cuda ? "CUDA support" : "GPU support")} is installed: restart aBookPlayer to use it ({(GpuSupport.IsActive ? "Vulkan" : "the CPU")} is used until then)."
            : cuda && GpuSupport.IsCudaInstalled ? "✓ CUDA support on this PC. If the card cannot be used, the CPU takes over."
            : cuda ? $"CUDA support will be downloaded once from nuget.org and from NVIDIA ({Megabytes(GpuSupport.CudaMissingBytes)})."
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
        else if (SrtPath != null)
        {
            DialogResult = DialogResult.OK; // the open book got its subtitles: attach them
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

    // ───────────────────────────── Transcription ─────────────────────────────

    async Task RunAsync()
    {
        var model = SelectedModel;
        var language = Languages[Math.Max(0, _cmbLanguage.SelectedIndex)].Code;
        _settings.WhisperModel = model.Id;
        _settings.WhisperLanguage = language;
        _settings.WhisperSaveText = _chkText.Checked;
        if (_chkTranslate.Enabled) _settings.WhisperTranslate = _chkTranslate.Checked;
        bool translate = Translate;
        bool useGpu = _chkGpu.Checked, cuda = CudaChosen;
        if (_chkGpu.Enabled) _settings.WhisperUseGpu = useGpu;
        _settings.WhisperUseCuda = _chkCuda.Checked;
        GpuSupport.PreferCuda = cuda;
        long cudaBytes = cuda ? GpuSupport.CudaMissingBytes : 0;
        bool downloadCuda = cudaBytes > 0, downloadGpu = useGpu && !cuda && !GpuSupport.IsInstalled;

        var todo = _queue.Where(q => q.State != QueueState.Done).ToList();
        if (todo.Count == 0) return;

        if (!model.IsDownloaded || downloadGpu || downloadCuda)
        {
            var what = new List<string>();
            if (!model.IsDownloaded) what.Add($"• the \"{model.Name}\" speech model from Hugging Face ({FormatSize(model.SizeMb)})");
            if (downloadGpu) what.Add($"• GPU support (Vulkan) from nuget.org ({GpuSupport.DownloadBytes / (1024 * 1024)} MB)");
            if (downloadCuda) what.Add($"• CUDA support: Whisper's library for it from nuget.org, CUDA from NVIDIA under NVIDIA's license ({Megabytes(cudaBytes)})");
            if (MessageBox.Show(this,
                    $"This will be downloaded once and kept on this PC:\n{string.Join("\n", what)}\n\n" +
                    "After the download, transcription works without an internet connection. Continue?",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
        }

        if (!ChooseOutputPaths(todo)) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetRunning(true);
        _log.Clear();
        _progress.Value = 0;

        try
        {
            if (downloadCuda)
            {
                await GpuSupport.DownloadCudaAsync(new Progress<long>(bytes =>
                {
                    _progress.Value = Math.Min(0.99, bytes / (double)cudaBytes);
                    _lblStatus.Text = $"Downloading CUDA support: {bytes / (1024 * 1024)} / {cudaBytes / (1024 * 1024)} MB";
                }), ct);
                UpdateGpuInfo();
                _progress.Value = 0;
            }
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

            var work = todo.Where(q => q.Output != null).ToList();
            for (int n = 0; n < work.Count; n++)
            {
                var item = work[n];
                var prefix = work.Count > 1 ? $"[{n + 1}/{work.Count}] " : "";
                SetState(item, QueueState.Running);
                if (work.Count > 1) _log.AppendText($"── {item.Name} ──{Environment.NewLine}");
                try
                {
                    var cues = await Transcriber.TranscribeAsync(item.Path, model.FilePath, language, translate, useGpu,
                        new Progress<TranscriptionProgress>(p =>
                        {
                            _progress.Value = p.Fraction;
                            _lblStatus.Text = prefix + p.Status;
                            if (p.LogLine != null) _log.AppendText(p.LogLine + Environment.NewLine);
                        }), ct);

                    if (cues.Count == 0)
                    {
                        SetState(item, QueueState.Failed, "no speech recognized");
                        if (work.Count == 1)
                            MessageBox.Show(this, "No speech was recognized in the file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        continue;
                    }
                    var saved = SaveResult(item.Output!, cues, await ChaptersForAsync(item.Path));
                    SetState(item, saved != null ? QueueState.Done : QueueState.Failed, saved != null ? null : "not saved");
                    if (saved != null && SamePath(item.Path, _audioPath)) (SrtPath, Translated) = (saved, translate);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidModelException)
                {
                    SetState(item, QueueState.Waiting);
                    throw;
                }
                catch (Exception ex) when (work.Count > 1)
                {
                    // One unreadable book must not stop the others
                    SetState(item, QueueState.Failed, ex.Message);
                    _log.AppendText($"Failed: {ex.Message}{Environment.NewLine}");
                }
            }

            if (_queue.Count > 1)
            {
                int done = _queue.Count(q => q.State == QueueState.Done), failed = _queue.Count(q => q.State == QueueState.Failed);
                _lblStatus.Text = $"Finished: {done} of {_queue.Count} books have subtitles" + (failed > 0 ? $", {failed} failed" : "") + ".";
                _progress.Value = 1;
            }
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

        // A single book closes the window when done; with a queue the results stay visible
        if (_queue.Count == 1 && SrtPath != null) DialogResult = DialogResult.OK;
        else if (_closeRequested) Close();
    }

    /// <summary>
    /// Subtitles go next to each book (so they load automatically). One book: ask before overwriting, as before.
    /// Several: ask once whether the books that already have subtitles are overwritten or skipped.
    /// </summary>
    bool ChooseOutputPaths(List<QueueItem> items)
    {
        if (items.Count == 1)
        {
            items[0].Output = ChooseOutputPath(items[0].Path);
            return items[0].Output != null;
        }
        var existing = items.Where(i => File.Exists(OutputPath(i.Path))).ToList();
        bool overwrite = true;
        if (existing.Count > 0)
        {
            var answer = MessageBox.Show(this,
                existing.Count == 1
                    ? $"\"{existing[0].Name}\" already has subtitles.\n\nOverwrite them? Choose No to skip that book."
                    : $"{existing.Count} of these books already have subtitles.\n\nOverwrite them? Choose No to skip those books.",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            overwrite = answer == DialogResult.Yes;
        }
        foreach (var item in items)
        {
            bool skip = !overwrite && existing.Contains(item);
            item.Output = skip ? null : OutputPath(item.Path);
            if (skip) SetState(item, QueueState.Done, "has subtitles already");
        }
        return items.Any(i => i.Output != null);
    }

    /// <summary>Normally next to the audio file with the same name (so it loads automatically); asks before overwriting.</summary>
    string? ChooseOutputPath(string book)
    {
        var path = OutputPath(book);
        if (!File.Exists(path)) return path;

        var answer = MessageBox.Show(this,
            $"\"{Path.GetFileName(path)}\" already exists.\n\nOverwrite it? Choose No to save under a different name.",
            Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) return path;
        if (answer == DialogResult.Cancel) return null;
        return AskSavePath(Path.GetDirectoryName(path), BookSource.DisplayName(book) + (Translate ? ".en" : "") + ".whisper.srt");
    }

    /// <summary>Chapter headings for the .txt transcript: the open book's are known, the others are read now.</summary>
    async Task<IReadOnlyList<Chapter>> ChaptersForAsync(string book)
    {
        if (SamePath(book, _audioPath)) return _chapters;
        return await Task.Run(() =>
        {
            try
            {
                var (info, reader) = BookAudio.OpenWithInfo(book);
                reader.Dispose();
                return (IReadOnlyList<Chapter>)info.Chapters.OrderBy(c => c.Start).ToList();
            }
            catch { return []; }
        });
    }

    static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
    string? SaveResult(string srtPath, List<SubtitleCue> cues, IReadOnlyList<Chapter> chapters)
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
            try { SubtitleTrack.WriteTranscript(txtPath, cues, chapters); }
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
        _chkTranslate.Enabled = !running && SelectedModel.CanTranslate;
        _chkGpu.Enabled = !running && GpuSupport.IsDriverAvailable;
        _chkCuda.Enabled = _chkGpu.Enabled && _chkGpu.Checked;
        _cmbLanguage.Enabled = !running && !SelectedModel.IsEnglishOnly;
        _btnAddBooks.Enabled = _btnAddFolder.Enabled = _btnRemove.Enabled = !running;
        _btnStart.Enabled = true;
        UseWaitCursor = false;
        RefillQueue();
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
