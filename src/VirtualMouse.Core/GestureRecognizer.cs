using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Core;

/// <summary>
/// Translates identified marker groups into a GestureState.
///
/// Gesture model:
///
///   ACTIVATION (Left Hand):
///     - Left thumb + left index spread apart  → mouse ACTIVE
///     - Left thumb + left index pinched close → mouse INACTIVE
///     - Threshold distance is user-calibrated via CalibrateActivation().
///     - Hysteresis prevents rapid toggling at the boundary.
///
///   MOUSE MOVEMENT (Right Hand, active only):
///     - Right index finger centroid delta drives cursor movement.
///
///   LEFT CLICK (Right Hand):
///     - Right index finger quick downward tap → left click.
///     - Right index finger held down beyond HoldThresholdMs → left button hold (drag).
///
///   RIGHT CLICK (Right Hand):
///     - Right middle finger quick downward tap → right click.
/// </summary>
public class GestureRecognizer
{
    private readonly ILogger<GestureRecognizer> _logger;
    private readonly TrackingSettings _settings;

    // ── State ──────────────────────────────────────────────────────────────

    private bool _mouseActive;

    // Movement tracking
    private (double X, double Y)? _prevRightIndexPos;

    // Tap detection for right index (left click)
    private (double X, double Y)? _tapStartPosRightIndex;
    private DateTime? _tapStartTimeRightIndex;
    private bool _leftButtonHeld;

    // Tap detection for right middle (right click)
    private (double X, double Y)? _tapStartPosRightMiddle;
    private DateTime? _tapStartTimeRightMiddle;
    private bool _rightButtonHeld;

    // Activation hysteresis state
    // Reserved for future directional hysteresis logic; assigned but not yet read.
#pragma warning disable CS0414
    private bool _activationArmed;
#pragma warning restore CS0414

    // ── Public State ───────────────────────────────────────────────────────

    /// <summary>Whether the mouse is currently active (left hand spread open).</summary>
    public bool IsMouseActive => _mouseActive;

    /// <summary>
    /// The most recently measured distance between left thumb and left index centroids,
    /// in camera pixels. Exposed for the calibration UI.
    /// </summary>
    public double LastActivationDistance { get; private set; } = 0;

    public GestureRecognizer(ILogger<GestureRecognizer> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    // ── Calibration ────────────────────────────────────────────────────────

    /// <summary>
    /// Records the current left-hand pinch distance as the activation threshold.
    /// Call this when the user has positioned their fingers at the desired trigger point.
    /// </summary>
    public void CalibrateActivation()
    {
        if (LastActivationDistance <= 0)
        {
            _logger.LogWarning("Cannot calibrate: left hand markers not detected.");
            return;
        }
        _settings.ActivationThresholdPixels = LastActivationDistance;
        _logger.LogInformation(
            "Activation threshold calibrated to {Distance:F1}px.", LastActivationDistance);
    }

    // ── Main Processing ────────────────────────────────────────────────────

    public GestureState Process(IEnumerable<MarkerGroup> groups)
    {
        var groupDict = groups.ToDictionary(g => g.Identity);
        var gesture   = new GestureState();
        var now       = DateTime.UtcNow;

        // ── 1. Measure left-hand activation distance ───────────────────────
        UpdateActivationState(groupDict);

        // ── 2. Mouse movement (only when active) ──────────────────────────
        if (_mouseActive)
        {
            if (groupDict.TryGetValue(FingerIdentity.RightIndex, out var rightIndex))
            {
                var currentPos = rightIndex.Centroid;
                if (_prevRightIndexPos.HasValue)
                {
                    var dx = (currentPos.X - _prevRightIndexPos.Value.X) * _settings.MouseSensitivity;
                    var dy = (currentPos.Y - _prevRightIndexPos.Value.Y) * _settings.MouseSensitivity;

                    if (Math.Abs(dx) > _settings.DeadZonePixels || Math.Abs(dy) > _settings.DeadZonePixels)
                        gesture.MouseDelta = (dx, dy);
                }
                _prevRightIndexPos = currentPos;
            }
            else
            {
                _prevRightIndexPos = null;
            }

            // ── 3. Left click: right index tap / hold ──────────────────────
            ProcessTap(
                groupDict, FingerIdentity.RightIndex,
                ref _tapStartPosRightIndex, ref _tapStartTimeRightIndex,
                ref _leftButtonHeld,
                now, out bool leftClickDown, out bool leftHeld);
            gesture.LeftClickDown   = leftClickDown;
            gesture.LeftButtonHeld  = leftHeld;

            // ── 4. Right click: right middle tap ───────────────────────────
            ProcessTap(
                groupDict, FingerIdentity.RightMiddle,
                ref _tapStartPosRightMiddle, ref _tapStartTimeRightMiddle,
                ref _rightButtonHeld,
                now, out bool rightClickDown, out _);
            gesture.RightClickDown = rightClickDown;
        }
        else
        {
            // Mouse deactivated — release any held buttons
            if (_leftButtonHeld)
            {
                gesture.LeftButtonHeld = false;
                _leftButtonHeld = false;
            }
            if (_rightButtonHeld)
            {
                gesture.RightClickDown = false;
                _rightButtonHeld = false;
            }
            _prevRightIndexPos = null;
        }

        return gesture;
    }

    // ── Activation Logic ───────────────────────────────────────────────────

    private void UpdateActivationState(Dictionary<FingerIdentity, MarkerGroup> groups)
    {
        if (!groups.TryGetValue(FingerIdentity.LeftThumb,  out var thumb) ||
            !groups.TryGetValue(FingerIdentity.LeftIndex,  out var index))
        {
            // Left hand not visible — keep current activation state
            LastActivationDistance = 0;
            return;
        }

        var tp = thumb.Centroid;
        var ip = index.Centroid;
        double dist = Math.Sqrt(Math.Pow(tp.X - ip.X, 2) + Math.Pow(tp.Y - ip.Y, 2));
        LastActivationDistance = dist;

        double threshold = _settings.ActivationThresholdPixels;
        if (threshold <= 0) return; // not yet calibrated

        double hysteresis = _settings.ActivationHysteresisPixels;

        if (!_mouseActive && dist > threshold + hysteresis)
        {
            _mouseActive = true;
            _activationArmed = true;
            _logger.LogInformation("Mouse ACTIVATED (dist={D:F1}px, threshold={T:F1}px).", dist, threshold);
        }
        else if (_mouseActive && dist < threshold - hysteresis)
        {
            _mouseActive = false;
            _activationArmed = false;
            _logger.LogInformation("Mouse DEACTIVATED (dist={D:F1}px, threshold={T:F1}px).", dist, threshold);
        }
    }

    // ── Tap / Hold Detection ───────────────────────────────────────────────

    /// <summary>
    /// Detects a downward tap or hold on a given finger.
    /// A tap is a quick downward movement (Y increases in camera coords) followed
    /// by a return upward, all within TapMaxDurationMs.
    /// A hold is a sustained downward displacement beyond HoldThresholdMs.
    /// </summary>
    private void ProcessTap(
        Dictionary<FingerIdentity, MarkerGroup> groups,
        FingerIdentity finger,
        ref (double X, double Y)? tapStartPos,
        ref DateTime? tapStartTime,
        ref bool buttonHeld,
        DateTime now,
        out bool clickFired,
        out bool holdActive)
    {
        clickFired = false;
        holdActive = buttonHeld;

        if (!groups.TryGetValue(finger, out var group))
        {
            // Finger lost — cancel any in-progress tap
            tapStartPos  = null;
            tapStartTime = null;
            if (buttonHeld) { holdActive = false; buttonHeld = false; }
            return;
        }

        var pos = group.Centroid;

        if (tapStartPos == null)
        {
            // Begin tracking this finger's position
            tapStartPos  = pos;
            tapStartTime = now;
            return;
        }

        double dy       = pos.Y - tapStartPos.Value.Y; // positive = moved down (toward keyboard)
        double elapsed  = (now - tapStartTime!.Value).TotalMilliseconds;

        // Detect downward tap: moved down enough within the time window
        if (!buttonHeld && dy >= _settings.TapMinMovementPixels)
        {
            if (elapsed <= _settings.TapMaxDurationMs)
            {
                // Quick tap → single click
                clickFired   = true;
                tapStartPos  = pos;
                tapStartTime = now;
                _logger.LogDebug("{Finger} tap click (dy={DY:F1}px, {MS:F0}ms).", finger, dy, elapsed);
            }
            else if (elapsed >= _settings.HoldThresholdMs)
            {
                // Sustained press → hold
                buttonHeld = true;
                holdActive = true;
                _logger.LogDebug("{Finger} hold start (dy={DY:F1}px, {MS:F0}ms).", finger, dy, elapsed);
            }
        }

        // Detect release from hold: finger moved back up
        if (buttonHeld && dy < _settings.TapMinMovementPixels / 2.0)
        {
            buttonHeld = false;
            holdActive = false;
            tapStartPos  = pos;
            tapStartTime = now;
            _logger.LogDebug("{Finger} hold released.", finger);
        }
    }

    // ── Reset ──────────────────────────────────────────────────────────────

    public void Reset()
    {
        _mouseActive         = false;
        _activationArmed     = false;
        _prevRightIndexPos   = null;
        _tapStartPosRightIndex   = null;
        _tapStartTimeRightIndex  = null;
        _tapStartPosRightMiddle  = null;
        _tapStartTimeRightMiddle = null;
        _leftButtonHeld      = false;
        _rightButtonHeld     = false;
        _logger.LogInformation("GestureRecognizer state reset.");
    }
}
