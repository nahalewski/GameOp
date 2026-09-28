using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace GameOp.Services;

/// <summary>
/// Lets the Ally's built-in controller drive the UI: D-pad / left stick move focus, A activates,
/// B goes back, LB / RB switch pages.
/// </summary>
public sealed class GamepadNavigator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public XInputGamepad Pad; }

    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int index, out XInputState state);

    private const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, A = 0x1000, B = 0x2000, Lb = 0x100, Rb = 0x200;

    private readonly Window _window;
    private readonly Action<int> _switchPage;
    private readonly Action _back;
    private readonly DispatcherTimer _timer;
    private ushort _last;
    private DateTime _repeatAt;

    public GamepadNavigator(Window window, Action<int> switchPage, Action back)
    {
        _window = window;
        _switchPage = switchPage;
        _back = back;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Poll;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll(object? sender, EventArgs e)
    {
        if (!_window.IsActive || !AppState.Instance.Settings.GamepadNavigation) return;
        ushort buttons = 0;
        for (var i = 0; i < 4; i++)
        {
            try { if (XInputGetState(i, out var s) == 0) { buttons = Stick(s.Pad); break; } }
            catch (DllNotFoundException) { _timer.Stop(); return; }
        }

        var pressed = (ushort)(buttons & ~_last);
        var held = buttons & (Up | Down | Left | Right);
        if (held != 0 && (buttons & ~pressed & held) != 0 && DateTime.Now >= _repeatAt)
        {
            pressed |= (ushort)held; // auto-repeat for held directions
            _repeatAt = DateTime.Now.AddMilliseconds(110);
        }
        else if ((pressed & (Up | Down | Left | Right)) != 0) _repeatAt = DateTime.Now.AddMilliseconds(400);
        _last = buttons;

        if ((pressed & Up) != 0) Move(FocusNavigationDirection.Up);
        if ((pressed & Down) != 0) Move(FocusNavigationDirection.Down);
        if ((pressed & Left) != 0) Move(FocusNavigationDirection.Left);
        if ((pressed & Right) != 0) Move(FocusNavigationDirection.Right);
        if ((pressed & A) != 0) Activate();
        if ((pressed & B) != 0) _back();
        if ((pressed & Lb) != 0) _switchPage(-1);
        if ((pressed & Rb) != 0) _switchPage(+1);
    }

    private static ushort Stick(XInputGamepad p)
    {
        const int dz = 16000;
        var b = p.Buttons;
        if (p.LY > dz) b |= Up; else if (p.LY < -dz) b |= Down;
        if (p.LX < -dz) b |= Left; else if (p.LX > dz) b |= Right;
        return b;
    }

    private void Move(FocusNavigationDirection dir)
    {
        if (Keyboard.FocusedElement is not UIElement el)
        {
            _window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }
        // Lists handle up/down themselves; route arrows through as key presses.
        if (el is ListBoxItem or ComboBox or Slider)
        {
            var key = dir switch
            {
                FocusNavigationDirection.Up => Key.Up, FocusNavigationDirection.Down => Key.Down,
                FocusNavigationDirection.Left => Key.Left, _ => Key.Right
            };
            var source = PresentationSource.FromVisual(el);
            if (source is not null)
            {
                el.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                return;
            }
        }
        el.MoveFocus(new TraversalRequest(dir));
        (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
    }

    private static void Activate()
    {
        switch (Keyboard.FocusedElement)
        {
            case ToggleButton t:
                t.IsChecked = !(t.IsChecked ?? false);
                break;
            case ButtonBase btn when UIElementAutomationPeer.CreatePeerForElement(btn)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv:
                inv.Invoke();
                break;
            case ListBoxItem item:
                item.IsSelected = true;
                break;
            case ComboBox cb:
                cb.IsDropDownOpen = !cb.IsDropDownOpen;
                break;
        }
    }
}
