using System.Runtime.InteropServices;

namespace aBookPlayer;

/// <summary>
/// Keeps Windows from sleeping (and optionally from blanking the screen or starting the screensaver)
/// while a book is playing. The request belongs to the calling thread: always call it from the UI thread.
/// Sleep chosen by the user (Start menu, lid, power button) still happens.
/// </summary>
static class KeepAwake
{
    [Flags]
    enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll")]
    static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    static ExecutionState? _current;

    /// <summary>Applies the request; repeated calls with the same arguments do nothing.</summary>
    public static void Set(bool playing, bool keepScreenOn)
    {
        var state = ExecutionState.Continuous;
        if (playing) state |= ExecutionState.SystemRequired | (keepScreenOn ? ExecutionState.DisplayRequired : 0);
        if (state == _current) return;
        SetThreadExecutionState(state);
        _current = state;
    }
}
