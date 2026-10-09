using System.Reflection;
using System.Windows.Forms;

namespace aBookPlayer.Tests;

public class SubtitleViewTests
{
    /// <summary>A subtitle area with a clock of its own, counting its clicks and the drags it starts.</summary>
    sealed class Mouse
    {
        readonly SubtitleView _view = new();
        long _now = 1000;
        public int Clicks, Drags;

        public Mouse()
        {
            _view.Clock = () => _now;
            _view.Clicked += (_, _) => Clicks++;
            _view.DragStarted += (_, _) => Drags++;
        }

        void Raise(string method, MouseButtons button, int x, int clicks = 1) =>
            typeof(Control).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_view, [new MouseEventArgs(button, clicks, x, 50, 0)]);

        public Mouse Down(int x = 100, int clicks = 1) { Raise("OnMouseDown", MouseButtons.Left, x, clicks); return this; }
        public Mouse Move(int x) { Raise("OnMouseMove", MouseButtons.Left, x); return this; }
        public Mouse Up(int x = 100) { Raise("OnMouseUp", MouseButtons.Left, x); return this; }
        public Mouse After(int milliseconds) { _now += milliseconds; return this; }
    }

    [Fact]
    public void A_quick_click_counts_even_with_the_mouse_still_moving()
    {
        var still = new Mouse().Down().After(60).Up();
        Assert.Equal((1, 0), (still.Clicks, still.Drags));
        // Pressed and let go on the way to the window: the mouse went a dozen pixels on meanwhile
        var moving = new Mouse().Down().After(30).Move(106).After(30).Move(113).After(30).Up(113);
        Assert.Equal((1, 0), (moving.Clicks, moving.Drags));
        // The second press of a double-click is not another click
        var twice = new Mouse().Down().After(60).Up().After(80).Down(clicks: 2).After(60).Up();
        Assert.Equal(1, twice.Clicks);
    }

    [Fact]
    public void The_button_held_and_the_mouse_moved_drags_the_window()
    {
        // Held a moment, then moved a little way
        var held = new Mouse().Down().After(250).Move(110);
        Assert.Equal((0, 1), (held.Clicks, held.Drags));
        Assert.Equal(0, held.Up(110).Clicks);
        // Moved far at once
        var far = new Mouse().Down().After(20).Move(170);
        Assert.Equal((0, 1), (far.Clicks, far.Drags));
        // Held without moving (or hardly): still a click
        var patient = new Mouse().Down().After(600).Move(102).Up(102);
        Assert.Equal((1, 0), (patient.Clicks, patient.Drags));
    }
}
