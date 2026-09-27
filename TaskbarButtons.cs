using System.Runtime.InteropServices;

namespace aBookPlayer;

/// <summary>
/// Previous chapter / Play-Pause / Next chapter buttons in the window's taskbar thumbnail (hover over the
/// taskbar icon). Clicks arrive as WM_COMMAND: the form passes them to <see cref="HandleCommand"/>.
/// </summary>
sealed class TaskbarButtons : IDisposable
{
    public const int WM_COMMAND = 0x0111;
    const int THBN_CLICKED = 0x1800;
    const uint THB_ICON = 0x2, THB_TOOLTIP = 0x4, THB_FLAGS = 0x8;
    const uint THBF_ENABLED = 0, THBF_DISABLED = 0x1;
    const int IdPrevious = 1, IdPlayPause = 2, IdNext = 3;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr tab, IntPtr hwnd);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr insertBefore);
        void SetTabActive(IntPtr tab, IntPtr hwnd, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] buttons);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    class TaskbarList;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ThumbButton
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Tip;
        public uint Flags;
    }

    readonly ITaskbarList3 _taskbar;
    readonly IntPtr _hwnd;
    readonly Icon _previous, _play, _pause, _next;
    readonly Action _onPrevious, _onPlayPause, _onNext;
    (bool Playing, bool Loaded, bool Chapters)? _shown;

    TaskbarButtons(IntPtr hwnd, ITaskbarList3 taskbar, Action onPrevious, Action onPlayPause, Action onNext)
    {
        _hwnd = hwnd;
        _taskbar = taskbar;
        _onPrevious = onPrevious;
        _onPlayPause = onPlayPause;
        _onNext = onNext;
        _previous = GlyphIcon(Glyphs.Previous);
        _play = GlyphIcon(Glyphs.Play);
        _pause = GlyphIcon(Glyphs.Pause);
        _next = GlyphIcon(Glyphs.Next);
    }

    /// <summary>Adds the buttons; call it when Windows reports the taskbar button was created.</summary>
    public static TaskbarButtons? TryCreate(IntPtr hwnd, Action onPrevious, Action onPlayPause, Action onNext)
    {
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarList();
            taskbar.HrInit();
            var buttons = new TaskbarButtons(hwnd, taskbar, onPrevious, onPlayPause, onNext);
            taskbar.ThumbBarAddButtons(hwnd, 3, buttons.Build(false, false, false));
            buttons._shown = (false, false, false);
            return buttons;
        }
        catch
        {
            return null;
        }
    }

    public void Update(bool playing, bool loaded, bool hasChapters)
    {
        if (_shown == (playing, loaded, hasChapters)) return;
        try
        {
            _taskbar.ThumbBarUpdateButtons(_hwnd, 3, Build(playing, loaded, hasChapters));
            _shown = (playing, loaded, hasChapters);
        }
        catch { /* cosmetic */ }
    }

    ThumbButton[] Build(bool playing, bool loaded, bool hasChapters) =>
    [
        Button(IdPrevious, _previous, "Previous chapter", loaded && hasChapters),
        Button(IdPlayPause, playing ? _pause : _play, playing ? "Pause" : "Play", loaded),
        Button(IdNext, _next, "Next chapter", loaded && hasChapters),
    ];

    static ThumbButton Button(int id, Icon icon, string tip, bool enabled) => new()
    {
        Mask = THB_ICON | THB_TOOLTIP | THB_FLAGS, Id = (uint)id, Icon = icon.Handle, Tip = tip,
        Flags = enabled ? THBF_ENABLED : THBF_DISABLED,
    };

    /// <summary>True if the message was a click on one of the buttons.</summary>
    public bool HandleCommand(IntPtr wParam)
    {
        long w = wParam.ToInt64();
        if (((w >> 16) & 0xFFFF) != THBN_CLICKED) return false;
        switch ((int)(w & 0xFFFF))
        {
            case IdPrevious: _onPrevious(); return true;
            case IdPlayPause: _onPlayPause(); return true;
            case IdNext: _onNext(); return true;
        }
        return false;
    }

    /// <summary>The app's own glyphs, white on transparent, at the small-icon size.</summary>
    static Icon GlyphIcon(string glyph)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font(Theme.IconFontName, size * 0.6f, GraphicsUnit.Pixel))
        {
            // GDI+ (not TextRenderer/GDI) keeps the alpha channel of the transparent bitmap
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, Brushes.White, new RectangleF(0, 0, size, size), format);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        foreach (var icon in new[] { _previous, _play, _pause, _next })
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
