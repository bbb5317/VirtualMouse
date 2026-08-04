using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages the camera capture session using OpenCvSharp4 (VideoCapture).
/// The ArduCam-OV9281 is UVC-compliant, so it is accessible via DirectShow/MSMF
/// as a standard webcam — no proprietary SDK required.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly TrackingSettings _settings;
    private VideoCapture? _capture;
    private bool _isRunning;

    public event EventHandler<Mat>? FrameReady;

    public bool IsOpen => _capture?.IsOpened() ?? false;

    public CameraCapture(ILogger<CameraCapture> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Opens the camera device and configures resolution and framerate.
    /// </summary>
    /// <returns>True if the camera was opened successfully.</returns>
    public bool Open()
    {
        try
        {
            // Use DirectShow backend on Windows for best compatibility with UVC cameras
            _capture = new VideoCapture(_settings.CameraDeviceIndex, VideoCaptureAPIs.DSHOW);

            if (!_capture.IsOpened())
            {
                _logger.LogError("Failed to open camera at device index {Index}.", _settings.CameraDeviceIndex);
                return false;
            }

            // Configure the OV9281 for maximum resolution and high framerate
            _capture.Set(VideoCaptureProperties.FrameWidth, _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);
            _capture.Set(VideoCaptureProperties.Fps, _settings.TargetFps);

            // The OV9281 is monochrome; request GREY format if supported
            // Note: Some UVC drivers may still return BGR; the MarkerDetector handles both.
            _capture.Set(VideoCaptureProperties.ConvertRgb, 0);

            var actualWidth  = _capture.Get(VideoCaptureProperties.FrameWidth);
            var actualHeight = _capture.Get(VideoCaptureProperties.FrameHeight);
            var actualFps    = _capture.Get(VideoCaptureProperties.Fps);

            _logger.LogInformation(
                "Camera opened: {Width}x{Height} @ {Fps}fps (requested {ReqW}x{ReqH} @ {ReqFps}fps).",
                actualWidth, actualHeight, actualFps,
                _settings.FrameWidth, _settings.FrameHeight, _settings.TargetFps);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while opening camera.");
            return false;
        }
    }

    /// <summary>
    /// Starts the frame capture loop on a background thread.
    /// Raises FrameReady for each captured frame.
    /// </summary>
    public void StartCapture(CancellationToken cancellationToken)
    {
        if (!IsOpen)
            throw new InvalidOperationException("Camera is not open. Call Open() first.");

        _isRunning = true;
        Task.Run(() => CaptureLoop(cancellationToken), cancellationToken);
        _logger.LogInformation("Camera capture loop started.");
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            if (_capture!.Read(frame) && !frame.Empty())
            {
                // Clone the frame before raising the event to avoid data races
                FrameReady?.Invoke(this, frame.Clone());
            }
            else
            {
                _logger.LogWarning("Failed to read frame from camera.");
                Thread.Sleep(10);
            }
        }
        _logger.LogInformation("Camera capture loop stopped.");
    }

    /// <summary>
    /// Stops the capture loop and releases the camera resource.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
    }

    public void Dispose()
    {
        _isRunning = false;
        _capture?.Dispose();
        _capture = null;
    }
}
