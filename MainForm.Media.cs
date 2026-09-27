namespace aBookPlayer;

// Windows integration: media flyout / media keys / headset buttons (MediaControls) and taskbar thumbnail buttons
public sealed partial class MainForm
{
    static readonly int WM_TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

    MediaControls? _mediaControls;
    TaskbarButtons? _taskbarButtons;

    void SetUpMediaControls()
    {
        // Button presses arrive on a background thread
        _mediaControls = MediaControls.TryCreate(Handle, button => BeginInvoke(() => OnMediaButton(button)));
        UpdateMediaControls();
    }

    (string Source, DateTime Time) _lastMediaPress;

    /// <summary>
    /// A media key pressed with the window in front can arrive twice: as a key and through Windows' media
    /// controls. The first one acts; the other source within half a second is the same press.
    /// </summary>
    bool IsDuplicateMediaPress(string source)
    {
        var now = DateTime.Now;
        bool duplicate = _lastMediaPress.Source != null && _lastMediaPress.Source != source && now - _lastMediaPress.Time < TimeSpan.FromMilliseconds(500);
        if (!duplicate) _lastMediaPress = (source, now);
        return duplicate;
    }

    void OnMediaButton(MediaButton button)
    {
        if (IsDisposed || IsDuplicateMediaPress("windows")) return;
        switch (button)
        {
            case MediaButton.Play when !_player.IsPlaying:
            case MediaButton.Pause when _player.IsPlaying:
                TogglePlay();
                break;
            case MediaButton.Stop: Stop(); break;
            case MediaButton.Next: NextChapter(); break;
            case MediaButton.Previous: PreviousChapter(); break;
            case MediaButton.FastForward: SkipBy(SkipStep); break;
            case MediaButton.Rewind: SkipBy(-SkipStep); break;
        }
    }

    /// <summary>Called on every UI tick; the controls only send what changed.</summary>
    void UpdateMediaControls()
    {
        bool loaded = _player.IsLoaded, playing = _player.IsPlaying, chapters = _chapters.Count > 1;
        var book = CurrentBook;
        _mediaControls?.Update(loaded, playing, chapters, loaded ? book?.Title ?? _lblTitle.Text : null, book?.Author, _coverBytes);
        _taskbarButtons?.Update(playing, loaded, chapters);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_TaskbarButtonCreated)
        {
            // Sent when the taskbar button exists (also again after Explorer restarts)
            _taskbarButtons?.Dispose();
            _taskbarButtons = TaskbarButtons.TryCreate(Handle, PreviousChapter, TogglePlay, NextChapter);
            UpdateMediaControls();
        }
        else if (m.Msg == TaskbarButtons.WM_COMMAND && _taskbarButtons?.HandleCommand(m.WParam) == true)
        {
            return;
        }
        base.WndProc(ref m);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);
}
