using System.Runtime.InteropServices;
using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Input;

/// <summary>
/// Injects mouse events into the Windows OS using the SendInput API.
/// This is the lowest-latency method for programmatic mouse control on Windows,
/// bypassing the higher-level WM_MOUSEMOVE messages.
/// </summary>
public class WindowsMouseController
{
    private readonly ILogger<WindowsMouseController> _logger;

    // Track button state to avoid sending redundant up/down events
    private bool _leftButtonDown;
    private bool _rightButtonDown;

    public WindowsMouseController(ILogger<WindowsMouseController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Applies the given GestureState to the OS mouse.
    /// </summary>
    public void Apply(GestureState gesture)
    {
        var inputs = new List<INPUT>();

        // --- Mouse Movement ---
        if (gesture.MouseDelta != (0.0, 0.0))
        {
            inputs.Add(CreateMouseMoveInput(
                (int)Math.Round(gesture.MouseDelta.DeltaX),
                (int)Math.Round(gesture.MouseDelta.DeltaY)));
        }

        // --- Left Button ---
        if (gesture.LeftClickDown && !_leftButtonDown)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_LEFTDOWN));
            _leftButtonDown = true;
            _logger.LogDebug("Left button DOWN.");
        }
        else if (!gesture.LeftClickDown && _leftButtonDown)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_LEFTUP));
            _leftButtonDown = false;
            _logger.LogDebug("Left button UP.");
        }

        // --- Right Button ---
        if (gesture.RightClickDown && !_rightButtonDown)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_RIGHTDOWN));
            _rightButtonDown = true;
        }
        else if (!gesture.RightClickDown && _rightButtonDown)
        {
            inputs.Add(CreateMouseButtonInput(MouseEventFlags.MOUSEEVENTF_RIGHTUP));
            _rightButtonDown = false;
        }

        // --- Scroll ---
        if (gesture.ScrollDelta != 0)
        {
            inputs.Add(CreateScrollInput(gesture.ScrollDelta * 120)); // 120 = WHEEL_DELTA
        }

        if (inputs.Count > 0)
        {
            var inputArray = inputs.ToArray();
            uint sent = SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf<INPUT>());
            if (sent != inputArray.Length)
            {
                _logger.LogWarning("SendInput sent {Sent}/{Total} inputs. Last error: {Error}",
                    sent, inputArray.Length, Marshal.GetLastWin32Error());
            }
        }
    }

    /// <summary>
    /// Releases all held buttons (safety cleanup on shutdown).
    /// </summary>
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

    #region P/Invoke Definitions

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [Flags]
    private enum MouseEventFlags : uint
    {
        MOUSEEVENTF_MOVE       = 0x0001,
        MOUSEEVENTF_LEFTDOWN   = 0x0002,
        MOUSEEVENTF_LEFTUP     = 0x0004,
        MOUSEEVENTF_RIGHTDOWN  = 0x0008,
        MOUSEEVENTF_RIGHTUP    = 0x0010,
        MOUSEEVENTF_WHEEL      = 0x0800,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public MouseEventFlags dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type; // 0 = INPUT_MOUSE
        public MOUSEINPUT mi;
    }

    private static INPUT CreateMouseMoveInput(int dx, int dy) => new()
    {
        type = 0,
        mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MouseEventFlags.MOUSEEVENTF_MOVE }
    };

    private static INPUT CreateMouseButtonInput(MouseEventFlags flags) => new()
    {
        type = 0,
        mi = new MOUSEINPUT { dwFlags = flags }
    };

    private static INPUT CreateScrollInput(int delta) => new()
    {
        type = 0,
        mi = new MOUSEINPUT { mouseData = (uint)delta, dwFlags = MouseEventFlags.MOUSEEVENTF_WHEEL }
    };

    #endregion
}
