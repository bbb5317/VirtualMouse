using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace VirtualMouse.Vision;

/// <summary>
/// Represents a single available camera device on the system.
/// </summary>
public record CameraDeviceInfo(int Index, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates available camera devices by probing OpenCV VideoCapture indices.
/// Works with any UVC-compliant camera (including the ArduCam-OV9281) on Windows.
/// </summary>
public class CameraEnumerator
{
    private readonly ILogger<CameraEnumerator> _logger;

    // Maximum device index to probe. 10 covers virtually all consumer setups.
    private const int MaxProbeIndex = 10;

    public CameraEnumerator(ILogger<CameraEnumerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probes camera indices 0..MaxProbeIndex and returns all that open successfully.
    /// Each device is opened briefly to confirm it exists, then immediately released.
    /// </summary>
    public IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        var devices = new List<CameraDeviceInfo>();

        for (int i = 0; i < MaxProbeIndex; i++)
        {
            try
            {
                using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                if (cap.IsOpened())
                {
                    // Try to get a friendly name via the backend description.
                    // OpenCV does not expose the device friendly name directly,
                    // so we use a descriptive label with the index.
                    var name = $"Camera {i} (Device Index {i})";
                    devices.Add(new CameraDeviceInfo(i, name));
                    _logger.LogDebug("Found camera at index {Index}.", i);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Index {Index} probe failed: {Message}", i, ex.Message);
            }
        }

        _logger.LogInformation("Camera enumeration complete: {Count} device(s) found.", devices.Count);
        return devices;
    }
}
