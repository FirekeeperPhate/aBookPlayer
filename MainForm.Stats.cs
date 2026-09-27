namespace aBookPlayer;

// Listening statistics: time per day and per book, estimated finish
public sealed partial class MainForm
{
    DateTime? _lastStatsTick;

    /// <summary>Called on every UI tick: adds the real time spent listening to today and to the book.</summary>
    void UpdateListeningStats()
    {
        if (!_player.IsPlaying)
        {
            _lastStatsTick = null;
            return;
        }
        var now = DateTime.Now;
        if (_lastStatsTick is { } last)
        {
            // Capped: a stalled UI (a dialog, the PC waking up) must not count as hours of listening
            double seconds = Math.Min((now - last).TotalSeconds, 1.0);
            ListeningStats.Add(_settings, now, seconds);
            if (CurrentBook is { } book) book.ListenedSeconds += seconds;
        }
        _lastStatsTick = now;
        _settings.SilenceSavedSeconds += _player.TakeSkippedTime().TotalSeconds;
    }

    void ShowStatistics()
    {
        var book = _player.IsLoaded ? CurrentBook : null;
        var remaining = book != null ? (_player.Duration - _player.Position) / Math.Max(0.5, _player.Speed) : (TimeSpan?)null;
        using (var dlg = new StatsForm(_settings, book, remaining)) dlg.ShowDialog(this);
        OpenDeferredFile();
    }

    // ───────────────────────────── Skip silences ─────────────────────────────

    ToolStripMenuItem? _skipSilencesItem;

    ToolStripMenuItem MakeSkipSilencesItem()
    {
        _skipSilencesItem = new ToolStripMenuItem("Skip silences (shorten long pauses)")
        {
            Checked = _settings.SkipSilences, ShortcutKeyDisplayString = "K", ShowShortcutKeys = true,
        };
        _skipSilencesItem.Click += (_, _) => ToggleSkipSilences();
        return _skipSilencesItem;
    }

    void ToggleSkipSilences()
    {
        _settings.SkipSilences = _player.SkipSilences = !_player.SkipSilences;
        if (_skipSilencesItem != null) _skipSilencesItem.Checked = _settings.SkipSilences;
        ShowOsd(_settings.SkipSilences ? "Skipping silences" : "Silences played in full");
        SaveSettings();
    }
}

/// <summary>The numbers behind the statistics window (kept apart so they can be tested).</summary>
static class ListeningStats
{
    /// <summary>Days kept: a bit more than a year.</summary>
    const int KeptDays = 400;

    static string Key(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static void Add(AppSettings settings, DateTime when, double seconds)
    {
        var key = Key(when);
        settings.ListeningDays[key] = settings.ListeningDays.GetValueOrDefault(key) + seconds;
        if (settings.ListeningDays.Count > KeptDays)
            foreach (var old in settings.ListeningDays.Keys.Order(StringComparer.Ordinal).Take(settings.ListeningDays.Count - KeptDays).ToList())
                settings.ListeningDays.Remove(old);
    }

    public static TimeSpan Day(AppSettings settings, DateTime day) =>
        TimeSpan.FromSeconds(settings.ListeningDays.GetValueOrDefault(Key(day)));

    /// <summary>The last <paramref name="days"/> days, today included.</summary>
    public static TimeSpan LastDays(AppSettings settings, DateTime today, int days) =>
        TimeSpan.FromSeconds(Enumerable.Range(0, days).Sum(i => settings.ListeningDays.GetValueOrDefault(Key(today.AddDays(-i)))));

    public static TimeSpan Total(AppSettings settings) => TimeSpan.FromSeconds(settings.ListeningDays.Values.Sum());

    /// <summary>
    /// When the book will be finished at the recent pace (the average of the last 14 days, days off included);
    /// null without enough listening to tell.
    /// </summary>
    public static DateTime? EstimatedFinish(AppSettings settings, DateTime today, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return today.Date;
        var perDay = LastDays(settings, today, 14) / 14;
        if (perDay < TimeSpan.FromMinutes(1)) return null;
        return today.Date.AddDays(Math.Ceiling(remaining / perDay) - 1);
    }
}

/// <summary>Listening statistics: today, this week and month, the last two weeks as a chart, the books listened to most.</summary>
sealed class StatsForm : DarkDialog
{
    readonly AppSettings _settings;

    public StatsForm(AppSettings settings, BookState? book, TimeSpan? remaining) : base("Listening statistics", new Size(560, 520))
    {
        _settings = settings;
        var today = DateTime.Now;

        var text = new System.Text.StringBuilder();
        text.AppendLine($"Today:  {Format(ListeningStats.Day(settings, today))}");
        text.AppendLine($"Last 7 days:  {Format(ListeningStats.LastDays(settings, today, 7))}");
        text.AppendLine($"Last 30 days:  {Format(ListeningStats.LastDays(settings, today, 30))}");
        text.AppendLine($"Since the statistics began:  {Format(ListeningStats.Total(settings))}");
        if (settings.SilenceSavedSeconds >= 60)
            text.AppendLine($"Saved by skipping silences:  {Format(TimeSpan.FromSeconds(settings.SilenceSavedSeconds))}");

        if (book != null && remaining is { } left)
        {
            text.AppendLine();
            text.AppendLine(book.Title ?? "This book");
            text.AppendLine($"    listened for {Format(TimeSpan.FromSeconds(book.ListenedSeconds))}, {Format(left)} to go at this speed");
            text.AppendLine(ListeningStats.EstimatedFinish(settings, today, left) is { } finish
                ? $"    at your recent pace, finished by {finish:dddd d MMMM}"
                : "    listen a few days to get an estimate of when you will finish");
        }

        var summary = new Label { Text = text.ToString(), Location = new Point(20, 16), Size = new Size(520, 200), ForeColor = Theme.Text };
        var chartTitle = new Label { Text = "Last 14 days", Location = new Point(20, 222), AutoSize = true, ForeColor = Theme.TextDim };
        var chart = new Panel { Location = new Point(20, 244), Size = new Size(520, 150), BackColor = Theme.Panel };
        chart.Paint += (_, e) => DrawChart(e.Graphics, chart.ClientRectangle);

        var top = settings.Books.Values.Where(b => b.ListenedSeconds >= 60).OrderByDescending(b => b.ListenedSeconds).Take(3)
            .Select(b => $"{b.Title ?? "?"}  ({Format(TimeSpan.FromSeconds(b.ListenedSeconds))})").ToList();
        var most = new Label
        {
            Text = top.Count > 0 ? "Listened to most:  " + string.Join("   ·   ", top) : "",
            Location = new Point(20, 402), Size = new Size(520, 40), ForeColor = Theme.TextDim, AutoEllipsis = true,
        };
        Controls.AddRange([summary, chartTitle, chart, most]);
        AcceptButton = CancelButton = AddButton("Close", DialogResult.OK);
    }

    void DrawChart(Graphics g, Rectangle r)
    {
        var today = DateTime.Now;
        var days = Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13)).ToList();
        var values = days.Select(d => ListeningStats.Day(_settings, d).TotalMinutes).ToList();
        double max = Math.Max(30, values.Max());
        int pad = LogicalToDeviceUnits(8), labelHeight = LogicalToDeviceUnits(18);
        float slot = (r.Width - 2f * pad) / days.Count, barWidth = slot * 0.6f;
        using var bar = new SolidBrush(Theme.Accent);
        using var todayBar = new SolidBrush(Theme.Bookmark);
        using var font = new Font("Segoe UI", 7.5f);
        for (int i = 0; i < days.Count; i++)
        {
            float height = (float)(values[i] / max * (r.Height - labelHeight - 2 * pad));
            float x = r.X + pad + i * slot + (slot - barWidth) / 2, bottom = r.Bottom - labelHeight - pad / 2f;
            if (height > 0) g.FillRectangle(i == days.Count - 1 ? todayBar : bar, x, bottom - height, barWidth, height);
            TextRenderer.DrawText(g, days[i].ToString("ddd")[..2], font, new Rectangle((int)(r.X + pad + i * slot), (int)bottom + 2, (int)slot, labelHeight),
                Theme.TextDim, TextFormatFlags.HorizontalCenter);
        }
        TextRenderer.DrawText(g, $"{max:0} min", font, new Point(r.X + pad, r.Y + 2), Theme.TextDim);
    }

    static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{(int)Math.Round(t.TotalMinutes)} min";
}
