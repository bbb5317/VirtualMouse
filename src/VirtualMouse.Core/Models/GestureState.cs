namespace VirtualMouse.Core.Models;

/// <summary>
/// Represents the current gesture state derived from marker positions.
/// This is the output of the GestureRecognizer and the input to the InputController.
/// </summary>
public class GestureState
{
    /// <summary>
    /// Relative mouse movement in screen pixels to apply this frame.
    /// </summary>
    public (double DeltaX, double DeltaY) MouseDelta { get; set; }

    /// <summary>
    /// Whether a left-click event should be triggered this frame.
    /// </summary>
    public bool LeftClickDown { get; set; }

    /// <summary>
    /// Whether a right-click event should be triggered this frame.
    /// </summary>
    public bool RightClickDown { get; set; }

    /// <summary>
    /// Whether the left mouse button is being held (drag mode).
    /// </summary>
    public bool LeftButtonHeld { get; set; }

    /// <summary>
    /// Scroll delta (positive = scroll up, negative = scroll down).
    /// </summary>
    public int ScrollDelta { get; set; }

    /// <summary>
    /// True if no gesture is currently active (idle state).
    /// </summary>
    public bool IsIdle => MouseDelta == (0, 0) && !LeftClickDown && !RightClickDown && !LeftButtonHeld && ScrollDelta == 0;
}
