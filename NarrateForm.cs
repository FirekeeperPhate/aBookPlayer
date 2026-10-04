using System.Diagnostics;
using NAudio.Wave;
using static aBookPlayer.DialogControls;

namespace aBookPlayer;

/// <summary>
/// Makes an audiobook out of a book in text (EPUB, PDF, text file), spoken on this PC by a Piper or a Kokoro voice:
/// which chapters, which voice (it can be heard first), how fast; then the progress, sentence by sentence. The
/// window does not block the player: a book takes from minutes to hours.
/// </summary>
sealed class NarrateForm : Form
{
    static readonly double[] Speeds = [0.8, 0.9, 1.0, 1.1, 1.2, 1.3];
    const int WordsPerMinute = 155;
    const string Sample = "This is how the book will sound. Every sentence is spoken on this PC, and shown as a subtitle while you listen.";

    sealed class ChapterItem(int index, TextChapter chapter)
    {
        public int Index { get; } = index;
        public TextChapter Chapter { get; } = chapter;
        public int Words { get; } = chapter.Words;
        public bool Checked { get; set; } = !chapter.FrontMatter;
    }

    readonly AppSettings _settings;
    readonly TextBox _txtFile = MakeTextBox(374);
    readonly Button _btnBrowse = MakeButton("Browse…", 90, 30);
    readonly Label _lblBook = new() { AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(470, 0), UseMnemonic = false };
    readonly DarkList _lstChapters = new(26) { Width = 470, Height = 130 };
    readonly Button _btnAll = MakeButton("All", 70, 28);
    readonly Button _btnNone = MakeButton("None", 70, 28);
    readonly ComboBox _cmbEngine = MakeCombo(150);
    readonly ComboBox _cmbVoice = MakeCombo(312);
    readonly Label _lblVoice = new() { AutoSize = true, ForeColor = Theme.TextDim, MaximumSize = new Size(470, 0) };
    readonly ComboBox _cmbSpeed = MakeCombo(90);
    readonly Button _btnListen = MakeButton("Listen", 90, 30);
    readonly CheckBox _chkGpu = new() { Text = "Use the graphics card (GPU) when it is faster than the processor", AutoSize = true, FlatStyle = FlatStyle.Flat };
    readonly CheckBox _chkCuda = new() { AutoSize = true, FlatStyle = FlatStyle.Flat };
    /// <summary>CUDA is offered: the PC has an NVIDIA card (see <see cref="NvidiaLibraries.HasCard"/>).</summary>
    readonly bool _cudaOffered = NvidiaLibraries.HasCard;
    readonly TextBox _txtFolder = MakeTextBox(374);
    readonly Button _btnFolder = MakeButton("Change…", 90, 30);
    readonly ProgressView _progress = new() { Dock = DockStyle.Top, Height = 8 };
    readonly Label _lblStatus = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Theme.TextDim, Padding = new Padding(0, 8, 0, 0), AutoEllipsis = true, UseMnemonic = false };
    readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, MaxLength = 0,
        BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f),
    };
    readonly Button _btnStart = MakeButton("Create the audiobook", 170, 34);
    readonly Button _btnOpen = MakeButton("Open it", 100, 34);
    readonly Button _btnClose = MakeButton("Close", 100, 34);

    TextBook? _book;
    string? _bookPath;
    string? _folder;       // set by the user; else beside the text
    CancellationTokenSource? _cts;
    bool _closeRequested;
    WaveOutEvent? _preview;

    /// <summary>The audiobook was made (its folder): the main window opens it when asked.</summary>
    public event Action<string>? OpenRequested;

    static TextBox MakeTextBox(int width) => new()
    {
        ReadOnly = true, Width = width, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
    };

    public NarrateForm(AppSettings settings)
    {
        _settings = settings;
        _folder = settings.NarrationFolder is { Length: > 0 } f && Directory.Exists(f) ? f : null;

        SuspendLayout();
        Text = "Create an audiobook from a text";
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(680, 740);
        MinimumSize = new Size(600, 640);

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in controls) c.Margin = new Padding(0, 0, 8, 0);
            row.Controls.AddRange(controls);
            return row;
        }
        var chapterButtons = Row(_btnAll, _btnNone);
        chapterButtons.Margin = new Padding(0, 6, 0, 0);
        var chapterBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        chapterBox.Controls.AddRange([_lstChapters, chapterButtons]);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(18, 16, 18, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(grid, "Book", Row(_txtFile, _btnBrowse));
        AddRow(grid, "", _lblBook);
        AddRow(grid, "Chapters", chapterBox);
        AddRow(grid, "Voice", Row(_cmbEngine, _cmbVoice));
        AddRow(grid, "", _lblVoice);
        AddRow(grid, "Speed", Row(_cmbSpeed, _btnListen));
        AddRow(grid, "", _chkGpu);
        if (_cudaOffered) AddRow(grid, "", _chkCuda);
        AddRow(grid, "Save in", Row(_txtFolder, _btnFolder));

        var progressHost = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(18, 12, 18, 0) };
        progressHost.Controls.Add(_lblStatus);
        progressHost.Controls.Add(_progress);
        var logFrame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(10, 8, 4, 8) };
        logFrame.Controls.Add(_log);
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 4) };
        logHost.Controls.Add(logFrame);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(18, 10, 18, 16) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_btnOpen, 1, 0);
        buttons.Controls.Add(_btnStart, 2, 0);
        buttons.Controls.Add(_btnClose, 3, 0);
        _btnOpen.Visible = false;

        Controls.Add(logHost);
        Controls.Add(buttons);
        Controls.Add(progressHost);
        Controls.Add(grid);

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        _cmbEngine.Items.AddRange(["Kokoro — natural", "Piper — fast"]);
        _cmbEngine.SelectedIndex = string.Equals(settings.NarrationEngine, "Piper", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        foreach (var s in Speeds) _cmbSpeed.Items.Add($"{s:0.##}×");
        _cmbSpeed.SelectedIndex = Math.Max(0, Array.FindIndex(Speeds, s => Math.Abs(s - settings.NarrationSpeed) < 0.01));
        if (_cmbSpeed.SelectedIndex < 0) _cmbSpeed.SelectedIndex = 2;
        _chkGpu.Checked = settings.NarrationUseGpu;
        _chkCuda.Checked = settings.NarrationUseCuda;
        _chkCuda.Enabled = _chkGpu.Checked;
        _chkGpu.CheckedChanged += (_, _) => _chkCuda.Enabled = _chkGpu.Checked;
        UpdateCudaText();
        _lstChapters.DrawRow = DrawChapter;
        _lstChapters.MouseDown += (_, e) => ToggleChapter(_lstChapters.IndexFromPoint(e.Location));
        _lstChapters.KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) { ToggleChapter(_lstChapters.SelectedIndex); e.Handled = true; } };

        _btnBrowse.Click += (_, _) => Browse();
        _btnAll.Click += (_, _) => CheckAll(true);
        _btnNone.Click += (_, _) => CheckAll(false);
        _cmbEngine.SelectedIndexChanged += (_, _) => FillVoices();
        _cmbVoice.SelectedIndexChanged += (_, _) => UpdateVoiceInfo();
        _cmbSpeed.SelectedIndexChanged += (_, _) => UpdateBookInfo();
        _btnListen.Click += (_, _) => _ = ListenAsync();
        _btnFolder.Click += (_, _) => ChooseFolder();
        _btnStart.Click += (_, _) => StartOrCancel();
        _btnOpen.Click += (_, _) => { if (_made != null) OpenRequested?.Invoke(_made); };
        _btnClose.Click += (_, _) => Close();
        FillVoices();
        UpdateBookInfo();
        _lblStatus.Text = "The book is spoken entirely on this PC.";
        EnableFileDrop(this);
    }

    /// <summary>A book in text dropped anywhere on the window is read, as one chosen with Browse (not while one is being spoken).</summary>
    void EnableFileDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = _cts == null && DroppedBook(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) =>
        {
            if (_cts != null || DroppedBook(e.Data) is not { } file) return;
            Activate();
            // (After the drop is over, so Explorer does not wait for the book to be read)
            BeginInvoke(() => _ = LoadBookAsync(file));
        };
        foreach (Control child in control.Controls) EnableFileDrop(child);
    }

    static string? DroppedBook(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(f => File.Exists(f) && TextBookReader.IsSupported(f)) : null;

    string? _made;

    SpeechEngine Engine => _cmbEngine.SelectedIndex == 1 ? SpeechEngine.Piper : SpeechEngine.Kokoro;
    SpeechVoiceInfo Voice => SpeechVoices.Of(Engine).ElementAt(Math.Max(0, _cmbVoice.SelectedIndex));
    double Speed => Speeds[Math.Max(0, _cmbSpeed.SelectedIndex)];
    List<ChapterItem> Chosen => _lstChapters.Items.OfType<ChapterItem>().Where(c => c.Checked).ToList();

    // ───────────────────────────── The book ─────────────────────────────

    void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "A book in text",
            Filter = "Books in text (EPUB, PDF, text)|*.epub;*.pdf;*.txt;*.md|All files|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _ = LoadBookAsync(dlg.FileName);
    }

    /// <summary>Reads the book (a large PDF takes a moment) and lists its chapters, all chosen.</summary>
    public async Task LoadBookAsync(string path)
    {
        if (_cts != null) return; // one book at a time
        _lblStatus.Text = "Reading the book…";
        UseWaitCursor = true;
        _btnBrowse.Enabled = _btnStart.Enabled = false;
        try
        {
            var book = await Task.Run(() => TextBookReader.Read(path));
            _book = book;
            _bookPath = path;
            _made = null;
            _btnOpen.Visible = false;
            _txtFile.Text = path;
            _lstChapters.Items.Clear();
            for (int i = 0; i < book.Chapters.Count; i++) _lstChapters.Items.Add(new ChapterItem(i, book.Chapters[i]));
            _lblStatus.Text = "The book is spoken entirely on this PC.";
            _progress.Value = 0;
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "The book could not be read.";
            MessageBox.Show(this, $"\"{Path.GetFileName(path)}\" could not be read:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
            _btnBrowse.Enabled = _btnStart.Enabled = true;
            UpdateBookInfo();
        }
    }

    void ToggleChapter(int index)
    {
        if (_cts != null || index < 0 || index >= _lstChapters.Items.Count || _lstChapters.Items[index] is not ChapterItem item) return;
        item.Checked = !item.Checked;
        _lstChapters.Invalidate();
        UpdateBookInfo();
    }

    void CheckAll(bool on)
    {
        foreach (var item in _lstChapters.Items.OfType<ChapterItem>()) item.Checked = on;
        _lstChapters.Invalidate();
        UpdateBookInfo();
    }

    void DrawChapter(Graphics g, Rectangle r, int index)
    {
        if (_lstChapters.Items[index] is not ChapterItem c) return;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        int pad = _lstChapters.L(8), box = _lstChapters.L(14), right = _lstChapters.L(150);
        var check = new Rectangle(r.X + pad, r.Y + (r.Height - box) / 2, box, box);
        using (var pen = new Pen(c.Checked ? Theme.Accent : Theme.TextDim)) g.DrawRectangle(pen, check);
        if (c.Checked)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillRectangle(fill, check.X + 3, check.Y + 3, check.Width - 5, check.Height - 5);
        }
        var color = c.Checked ? Theme.Text : Theme.TextDim;
        TextRenderer.DrawText(g, c.Chapter.Title, _lstChapters.Font, new Rectangle(check.Right + pad, r.Y, r.Width - right - check.Right - 2 * pad, r.Height), color, flags);
        TextRenderer.DrawText(g, $"{c.Words:N0} words · {Duration(c.Words)}", _lstChapters.Font, new Rectangle(r.Right - right - pad, r.Y, right, r.Height),
            Theme.TextDim, flags | TextFormatFlags.Right);
    }

    /// <summary>"8 min", "1 h 05 min": how long the words take to say at the chosen speed.</summary>
    string Duration(long words)
    {
        double minutes = words / (WordsPerMinute * Speed);
        return minutes < 60 ? $"{Math.Max(1, (int)Math.Round(minutes))} min" : $"{(int)(minutes / 60)} h {(int)(minutes % 60):00} min";
    }

    void UpdateBookInfo()
    {
        if (_book == null)
        {
            _lblBook.Text = "Choose an EPUB, a PDF or a text file in English, or drop it on this window.";
            _txtFolder.Text = _folder ?? "";
        }
        else
        {
            var chosen = Chosen;
            long words = chosen.Sum(c => (long)c.Words);
            _lblBook.Text = $"{_book.Title}{(_book.Author != null ? " — " + _book.Author : "")}\n" +
                            $"{chosen.Count} of {_book.Chapters.Count} chapters · {words:N0} words · about {Duration(words)} of audio";
            _txtFolder.Text = Narrator.FolderFor(_book, OutputRoot);
        }
        _lstChapters.Invalidate();
        if (_cts == null) _btnStart.Enabled = _book != null && Chosen.Count > 0;
    }

    string OutputRoot => _folder ?? (_bookPath != null ? Path.GetDirectoryName(Path.GetFullPath(_bookPath))! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Where the audiobooks are saved (each in a folder of its own)", UseDescriptionForTitle = true, InitialDirectory = OutputRoot };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _folder = _settings.NarrationFolder = dlg.SelectedPath;
        UpdateBookInfo();
    }

    // ───────────────────────────── Voices ─────────────────────────────

    void FillVoices()
    {
        _cmbVoice.Items.Clear();
        foreach (var v in SpeechVoices.Of(Engine)) _cmbVoice.Items.Add(v.ToString());
        var last = Engine == SpeechEngine.Piper ? _settings.NarrationPiperVoice : _settings.NarrationKokoroVoice;
        int index = SpeechVoices.Of(Engine).ToList().FindIndex(v => v.Id == last);
        _cmbVoice.MaxDropDownItems = 16;
        _cmbVoice.SelectedIndex = Math.Max(0, index);
        UpdateVoiceInfo();
    }

    void UpdateVoiceInfo()
    {
        if (_cmbVoice.SelectedIndex < 0) return;
        long runtime = SpeechRuntime.IsInstalled ? 0 : SpeechRuntime.DownloadBytes, voice = SpeechVoices.MissingBytes(Voice);
        string about = Engine == SpeechEngine.Kokoro
            ? "Kokoro: the most natural voices; a few times faster than listening."
            : "Piper: lighter voices, ten times faster and more.";
        _lblVoice.Text = runtime + voice == 0
            ? $"✓ Voice already on this PC. {about}"
            : $"{about} Downloaded once ({Mb(runtime + voice)}): " +
              string.Join(" and ", new[] { runtime > 0 ? "the speech engine" : null, voice > 0 ? (Engine == SpeechEngine.Kokoro && !KokoroVoice.IsModelInstalled ? "Kokoro and the voice" : "the voice") : null }.Where(s => s != null)) + ".";
    }

    static string Mb(long bytes) => bytes >= 1000 * 1024 * 1024L ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB" : $"{Math.Max(1, bytes / (1024 * 1024))} MB";

    /// <summary>CUDA is asked for: an NVIDIA card, the graphics card wanted, and CUDA chosen for it.</summary>
    bool CudaChosen => _cudaOffered && _chkGpu.Checked && _chkCuda.Checked;

    void UpdateCudaText() => _chkCuda.Text = SpeechRuntime.IsCudaInstalled
        ? "NVIDIA card: use CUDA instead of DirectML"
        : $"NVIDIA card: use CUDA instead of DirectML (downloaded once: {Mb(SpeechRuntime.CudaMissingBytes)})";

    /// <summary>
    /// Downloads the speech engine and the voice if they are not here yet, and CUDA if <paramref name="cuda"/>
    /// (asking first); false when declined.
    /// </summary>
    async Task<bool> EnsureDownloadedAsync(SpeechVoiceInfo voice, bool cuda, CancellationToken ct)
    {
        long runtime = SpeechRuntime.IsInstalled ? 0 : SpeechRuntime.DownloadBytes, model = SpeechVoices.MissingBytes(voice);
        long nvidia = cuda ? SpeechRuntime.CudaMissingBytes : 0;
        if (runtime + model + nvidia == 0) return true;
        var what = new List<string>();
        if (runtime > 0) what.Add($"• the speech engine: ONNX Runtime with DirectML from nuget.org, espeak-ng from GitHub ({Mb(runtime)})");
        if (model > 0) what.Add($"• the voice \"{voice.Name}\" ({voice.Engine}) from Hugging Face ({Mb(model)})");
        if (nvidia > 0) what.Add($"• CUDA for the NVIDIA card: ONNX Runtime for CUDA from nuget.org, CUDA and cuDNN from NVIDIA, under NVIDIA's license ({Mb(nvidia)})");
        if (MessageBox.Show(this,
                $"This will be downloaded once and kept on this PC:\n{string.Join("\n", what)}\n\n" +
                "After the download, books are spoken without an internet connection. Continue?",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return false;
        if (runtime > 0)
            await SpeechRuntime.DownloadAsync(new Progress<long>(bytes =>
            {
                _progress.Value = Math.Min(0.99, bytes / (double)SpeechRuntime.DownloadBytes);
                _lblStatus.Text = $"Downloading the speech engine: {bytes / (1024 * 1024)} / {SpeechRuntime.DownloadBytes / (1024 * 1024)} MB";
            }), ct);
        if (model > 0)
            await SpeechVoices.DownloadAsync(voice, new Progress<long>(bytes =>
            {
                _progress.Value = Math.Min(0.99, bytes / (double)model);
                _lblStatus.Text = bytes >= model - 1024 && voice.Engine == SpeechEngine.Kokoro
                    ? "Preparing Kokoro for the graphics card…"
                    : $"Downloading the voice: {bytes / (1024 * 1024)} / {model / (1024 * 1024)} MB";
            }), ct);
        if (nvidia > 0)
            await SpeechRuntime.DownloadCudaAsync(new Progress<long>(bytes =>
            {
                _progress.Value = Math.Min(0.99, bytes / (double)nvidia);
                _lblStatus.Text = $"Downloading CUDA: {bytes / (1024 * 1024)} / {nvidia / (1024 * 1024)} MB";
            }), ct);
        _progress.Value = 0;
        UpdateVoiceInfo();
        UpdateCudaText();
        return true;
    }

    /// <summary>Says a sample (the first lines chosen of the book, or a stock sentence) in the chosen voice.</summary>
    async Task ListenAsync()
    {
        if (_cts != null) return;
        var voice = Voice;
        double speed = Speed;
        _cts = new CancellationTokenSource();
        SetRunning(true, listening: true);
        try
        {
            if (!await EnsureDownloadedAsync(voice, cuda: false, _cts.Token)) return;
            _lblStatus.Text = $"Loading {voice.Name}…";
            // (The engine is loaded as the book will want it: it cannot be changed afterwards)
            bool cuda = CudaChosen && SpeechRuntime.IsCudaInstalled;
            var sentences = Chosen.SelectMany(c => c.Chapter.Paragraphs).Where(p => p.Length > 60).Take(1)
                .SelectMany(p => SpeechText.Sentences(p)).Take(2).ToList();
            if (sentences.Count == 0) sentences = SpeechText.Sentences(Sample);
            var (audio, rate) = await Task.Run(() =>
            {
                // On the processor: a sample needs no graphics card, and starts sooner
                SpeechRuntime.Load(cuda);
                using var speaker = SpeechVoices.Open(voice, gpu: false);
                var samples = new List<float>();
                foreach (var s in sentences)
                {
                    samples.AddRange(speaker.Speak(SpeechText.Spell(s), speed));
                    samples.AddRange(new float[speaker.SampleRate / 4]);
                }
                return (samples.ToArray(), speaker.SampleRate);
            }, _cts.Token);
            // (Not after the window was asked to close while the voice was loading)
            _cts.Token.ThrowIfCancellationRequested();
            Play(audio, rate);
            _lblStatus.Text = $"{voice.Name} ({voice.Engine}), {speed:0.##}×";
        }
        catch (OperationCanceledException) { _lblStatus.Text = "Cancelled."; }
        catch (Exception ex)
        {
            _lblStatus.Text = "The voice could not be heard.";
            if (!_closeRequested) MessageBox.Show(this, $"The voice could not be loaded:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
            // The window was closed while the voice was loading (or downloading): it closes now
            if (_closeRequested) Close();
        }
    }

    void Play(float[] audio, int rate)
    {
        StopPreview();
        var bytes = new byte[audio.Length * 2];
        for (int i = 0; i < audio.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)Math.Clamp(audio[i] * 32767f, short.MinValue, short.MaxValue));
        _preview = new WaveOutEvent();
        _preview.Init(new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(rate, 16, 1)));
        _preview.Play();
    }

    void StopPreview()
    {
        _preview?.Stop();
        _preview?.Dispose();
        _preview = null;
    }

    // ───────────────────────────── Narration ─────────────────────────────

    void StartOrCancel()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _btnStart.Enabled = false;
            _lblStatus.Text = "Stopping…";
        }
        else _ = RunAsync();
    }

    async Task RunAsync()
    {
        if (_book is not { } book || _bookPath is not { } path) return;
        var chosen = Chosen;
        if (chosen.Count == 0) return;
        var voice = Voice;
        double speed = Speed;
        bool gpu = _chkGpu.Checked, cuda = CudaChosen;
        _settings.NarrationEngine = voice.Engine.ToString();
        if (voice.Engine == SpeechEngine.Piper) _settings.NarrationPiperVoice = voice.Id;
        else _settings.NarrationKokoroVoice = voice.Id;
        _settings.NarrationSpeed = speed;
        _settings.NarrationUseGpu = gpu;
        _settings.NarrationUseCuda = _chkCuda.Checked;

        if (!Narrator.CanEncode)
        {
            MessageBox.Show(this, "This Windows has no MP3 encoder (the \"N\" editions need the Media Feature Pack): the audiobook cannot be saved.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string folder = Narrator.FolderFor(book, OutputRoot);
        // What says this narration from another of the same book: a narration left halfway goes on only if it is the same
        var source = new FileInfo(path);
        string signature = $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{voice.Engine}|{voice.Id}|{speed:0.00}|{string.Join(',', chosen.Select(c => c.Index))}";
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.mp3").Any() && !Directory.Exists(Path.Combine(folder, ".narration")))
        {
            if (MessageBox.Show(this, $"\"{Path.GetFileName(folder)}\" already holds an audiobook.\n\nMake it again? Its audio files and subtitles are replaced.",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        }

        StopPreview();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetRunning(true);
        _log.Clear();
        _progress.Value = 0;
        _made = null;
        _btnOpen.Visible = false;
        try
        {
            if (!await EnsureDownloadedAsync(voice, cuda, ct)) return;
            // (A short text is done before the graphics card and the processor could be timed against each other)
            bool timed = gpu && chosen.Sum(c => (long)c.Words) >= 3000;
            _lblStatus.Text = timed ? $"Loading {voice.Name} and timing the graphics card against the processor…" : $"Loading {voice.Name}…";
            var (speaker, note) = await Task.Run(() =>
            {
                SpeechRuntime.Load(cuda);
                return SpeechTeam.Open(voice, gpu, timed, ct);
            }, ct);
            using (speaker)
            {
                _log.AppendText(note + Environment.NewLine);
                // A program runs one build of the speech engine: the other is for the next time the app is opened
                if (gpu && cuda != SpeechRuntime.UsesCuda)
                    _log.AppendText((cuda ? "CUDA is used from the next time aBookPlayer is opened: the speech engine was already running without it."
                        : "CUDA stays in use until aBookPlayer is closed: the speech engine was already running with it.") + Environment.NewLine);
                // A book made before in this folder (another voice, other chapters): its files go first
                await Task.Run(() => ClearOther(folder, signature), ct);
                Directory.CreateDirectory(folder);
                var clock = Stopwatch.StartNew();
                int lastChapter = 0;
                // (Made here, on the UI thread, so its reports come back to it)
                var report = new Progress<NarrationProgress>(p =>
                    {
                        _progress.Value = p.Fraction;
                        if (p.Chapter != lastChapter)
                        {
                            lastChapter = p.Chapter;
                            _log.AppendText($"{(_log.TextLength > 0 ? Environment.NewLine : "")}── {p.ChapterTitle} ──{Environment.NewLine}");
                        }
                        if (p.Sentence != null) _log.AppendText(p.Sentence + Environment.NewLine);
                        string left = p.Fraction > 0.01 && p.SpeedFactor > 0
                            ? " · " + Remaining(clock.Elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction) + " left" : "";
                        _lblStatus.Text = p.Sentence == null && p.SpeedFactor > 0
                            ? $"Chapter {p.Chapter} of {p.Chapters}: saving…"
                            : $"Chapter {p.Chapter} of {p.Chapters} · {p.Fraction:P0}{(p.SpeedFactor > 0 ? $" · {p.SpeedFactor:0.0}× real time" : "")}{left}";
                    });
                var indexes = chosen.Select(c => c.Index).ToList();
                var made = await Task.Run(() => Narrator.Run(book, indexes, speaker, speed, folder, signature, report, ct), ct);
                _made = made;
                _progress.Value = 1;
                _lblStatus.Text = $"Done in {Remaining(clock.Elapsed.TotalSeconds)}: \"{Path.GetFileName(made)}\".";
                _btnOpen.Visible = true;
            }
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "Stopped: the chapters made are kept, and it goes on from there the next time.";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "The audiobook could not be made.";
            if (!_closeRequested)
                MessageBox.Show(this, $"The audiobook could not be made:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
        if (_closeRequested) Close();
    }

    /// <summary>An audiobook made in this folder in another way (voice, speed, chapters): its files are removed, to be made again.</summary>
    static void ClearOther(string folder, string signature)
    {
        if (!Directory.Exists(folder)) return;
        var job = Path.Combine(folder, ".narration", "job.txt");
        if (File.Exists(job) && File.ReadAllText(job) == signature) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.mp3").Concat(Directory.EnumerateFiles(folder, "*.srt")).ToList()) File.Delete(file);
    }

    static string Remaining(double seconds) =>
        seconds < 90 ? $"{Math.Max(1, (int)seconds)} s" : seconds < 5400 ? $"{(int)Math.Round(seconds / 60)} min" : $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00} min";

    void SetRunning(bool running, bool listening = false)
    {
        _btnBrowse.Enabled = _btnAll.Enabled = _btnNone.Enabled = _cmbEngine.Enabled = _cmbVoice.Enabled = _cmbSpeed.Enabled =
            _chkGpu.Enabled = _btnFolder.Enabled = _btnListen.Enabled = !running;
        _chkCuda.Enabled = !running && _chkGpu.Checked;
        _btnStart.Text = running && !listening ? "Stop" : "Create the audiobook";
        _btnStart.Enabled = !listening && (running || (_book != null && Chosen.Count > 0));
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
            // Asked by the user: only once the work has stopped (what is done is kept). With the app itself closing,
            // the work is just told to stop
            if (e.CloseReason == CloseReason.UserClosing)
            {
                if (!_closeRequested && _btnStart.Text == "Stop" &&
                    MessageBox.Show(this, "Stop making the audiobook?\n\nThe chapters already made are kept: it goes on from there the next time.",
                        Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    e.Cancel = true;
                    base.OnFormClosing(e);
                    return;
                }
                e.Cancel = true;
                _closeRequested = true;
                _lblStatus.Text = "Stopping…";
            }
            _cts.Cancel();
        }
        if (!e.Cancel) StopPreview();
        base.OnFormClosing(e);
    }
}
