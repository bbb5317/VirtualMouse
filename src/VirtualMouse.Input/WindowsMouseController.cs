using System.Runtime.InteropServices;
using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Input;

/// <summary>
/// Injects mouse events into the Windows OS using the SendInput API.
/// Handles movement, single-click (tap), click-and-hold (drag), and right-click.
/// </summary>
public class WindowsMouseController
{
    private readonly ILogger<WindowsMouseController> _logger;

    private bool _leftButtonDown;
    private bool _rightButtonDown;

    public WindowsMouseController(ILogger<WindowsMouseController> logger)
    {
        _logger = logger;
    }

    public void Apply(GestureState gesture)
    {
        var inputs = new List<INPUT>();

        // ── Movement ──────────────────────────────────────────────────────
        if (gesture.MouseDelta != (0.0, 0.0))
        {
            inputs.Add(CreateMouseMoveInput(
                (int)Math.Round(gesture.MouseDelta.DeltaX),
                (int)Math.Round(gesture.MouseDelta.DeltaY)));
        }

        // ── Left button ───────────────────────────────────────────────────
        // Single tap: fire DOWN then UP in the same frame
        if (gesture.LeftClickDown && !gesture.LeftButtonHeld)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_LEFTDOWN));
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_LEFTUP));
            _logger.LogDebug("Left click.");
        }

        // Hold (drag): press down and keep held
        if (gesture.LeftButtonHeld && !_leftButtonDown)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_LEFTDOWN));
            _leftButtonDown = true;
            _logger.LogDebug("Left button DOWN (drag start).");
        }
        else if (!gesture.LeftButtonHeld && _leftButtonDown)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_LEFTUP));
            _leftButtonDown = false;
            _logger.LogDebug("Left button UP (drag end).");
        }

        // ── Right button ──────────────────────────────────────────────────
        if (gesture.RightClickDown && !_rightButtonDown)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_RIGHTDOWN));
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_RIGHTUP));
            _logger.LogDebug("Right click.");
        }

        // ── Scroll ────────────────────────────────────────────────────────
        if (gesture.ScrollDelta != 0)
            inputs.Add(CreateScrollInput(gesture.ScrollDelta * 120));

        if (inputs.Count > 0)
        {
            var arr  = inputs.ToArray();
            uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
            if (sent != arr.Length)
                _logger.LogWarning("SendInput: sent {S}/{T}. Error: {E}", sent, arr.Length, Marshal.GetLastWin32Error());
        }
    }

    public void ReleaseAll()
    {
        if (_leftButtonDown)
        {
            var input = new[] { CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_LEFTUP) };
            SendInput(1, input, Marshal.SizeOf<INPUT>());
            _leftButtonDown = false;
        }
        if (_rightButtonDown)
        {
            var input = new[] { CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_RIGHTUP) };
            SendInput(1, input, Marshal.SizeOf<INPUT>());
            _rightButtonDown = false;
        }
    }

    #region P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [Flags]
    private enum MouseEventFlags : uint
    {
        MOUSEEVENTF_MOVE      = 0x0001,
        MOUSEEVENTF_LEFTDOWN  = 0x0002,
        MOUSEEVENTF_LEFTUP    = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008,
        MOUSEEVENTF_RIGHTUP   = 0x0010,
        MOUSEEVENTF_WHEEL     = 0x0800,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public MouseEventFlags dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    private static INPUT CreateMouseMoveInput(int dx, int dy) => new()
    { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MouseEventFlags.MOUSEEVENTF_MOVE } };

    private static INPUT CreateMouseButtonInput(MouseEventFlags flags) => new()
    { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };

    private static INPUT CreateScrollInput(int delta) => new()
    { type = 0, mi = new MOUSEINPUT { mouseData = (uint)delta, dwFlags = MouseEventFlags.MOUSEEVENTF_WHEEL } };

    #endregion
}
