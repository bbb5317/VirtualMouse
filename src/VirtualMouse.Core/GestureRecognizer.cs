using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Core;

/// <summary>
/// Translates a set of identified marker groups into a GestureState.
/// This is the core business logic of the Virtual Mouse project.
/// </summary>
public class GestureRecognizer
{
    private readonly ILogger<GestureRecognizer> _logger;
    private readonly TrackingSettings _settings;

    // Previous frame positions for delta calculation
    private (double X, double Y)? _prevRightIndexPos;
    private (double X, double Y)? _prevLeftThumbPos;
    private (double X, double Y)? _prevLeftIndexPos;

    public GestureRecognizer(ILogger<GestureRecognizer> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Processes the current set of identified marker groups and returns the resulting gesture.
    /// </summary>
    public GestureState Process(IEnumerable<MarkerGroup> groups)
    {
        var groupDict = groups.ToDictionary(g => g.Identity);
        var gesture = new GestureState();

        // --- Mouse Movement: driven by Right Index Finger ---
        if (groupDict.TryGetValue(FingerIdentity.RightIndex, out var rightIndex))
        {
            var currentPos = rightIndex.Centroid;
            if (_prevRightIndexPos.HasValue)
            {
                var rawDeltaX = (currentPos.X - _prevRightIndexPos.Value.X) * _settings.MouseSensitivity;
                var rawDeltaY = (currentPos.Y - _prevRightIndexPos.Value.Y) * _settings.MouseSensitivity;

                // Dead-zone: ignore sub-threshold jitter
                if (Math.Abs(rawDeltaX) > _settings.DeadZonePixels || Math.Abs(rawDeltaY) > _settings.DeadZonePixels)
                {
                    gesture.MouseDelta = (rawDeltaX, rawDeltaY);
                }
            }
            _prevRightIndexPos = currentPos;
        }
        else
        {
            _prevRightIndexPos = null;
        }

        // --- Left Click: Left Thumb pinches toward Left Index ---
        if (groupDict.TryGetValue(FingerIdentity.LeftThumb, out var leftThumb) &&
            groupDict.TryGetValue(FingerIdentity.LeftIndex, out var leftIndex))
        {
            var thumbPos = leftThumb.Centroid;
            var indexPos = leftIndex.Centroid;
            var distance = Math.Sqrt(
                Math.Pow(thumbPos.X - indexPos.X, 2) +
                Math.Pow(thumbPos.Y - indexPos.Y, 2));

            gesture.LeftClickDown = distance < _settings.PinchThresholdPixels;
            _logger.LogDebug("Left pinch distance: {Distance:F2}px (threshold: {Threshold}px)", distance, _settings.PinchThresholdPixels);
        }

        // --- Right Click: Right Middle finger taps (future gesture) ---
        // TODO: Implement right-click gesture using RightMiddle finger

        return gesture;
    }

    /// <summary>
    /// Resets all tracking state (e.g., when the camera is paused or markers are lost).
    /// </summary>
    public void Reset()
    {
        _prevRightIndexPos = null;
        _prevLeftThumbPos = null;
        _prevLeftIndexPos = null;
        _logger.LogInformation("GestureRecognizer state reset.");
    }
}
