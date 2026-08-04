using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages the camera capture session using OpenCvSharp4 (VideoCapture).
/// The ArduCam-OV9281 is UVC-compliant, accessible via DirectShow on Windows.
///
/// CRITICAL for background subtraction: auto-exposure (AGC) must be disabled.
/// When AGC is active, the camera continuously adjusts gain and exposure to
/// maintain a target brightness. This causes every pixel's value to shift
/// slightly on every frame — even static pixels — making MOG2 classify the
/// entire frame as "changed" and defeating background subtraction entirely.
/// With fixed exposure, static pixels are truly stable and MOG2 works correctly.
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

    public bool Open()
    {
        try
        {
            _capture = new VideoCapture(_settings.CameraDeviceIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                _logger.LogError("Failed to open camera at index {Index}.", _settings.CameraDeviceIndex);
                return false;
            }

            // Resolution and framerate
            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);
            _capture.Set(VideoCaptureProperties.Fps,         _settings.TargetFps);

            // Request raw (no RGB conversion) — OV9281 is monochrome
            _capture.Set(VideoCaptureProperties.ConvertRgb, 0);

            // ── Disable Auto-Exposure (AGC) ────────────────────────────────
            // This is the single most important setting for background subtraction.
            // AutoExposure property: 0.25 = manual, 0.75 = auto (DirectShow convention).
            bool autoExpDisabled = _capture.Set(VideoCaptureProperties.AutoExposure, 0.25);
            _logger.LogInformation("Auto-exposure disable: {Result}", autoExpDisabled ? "OK" : "not supported by driver");

            // Set a fixed exposure value.
            // DirectShow exposure is in log2 seconds: -7 = 1/128s, -6 = 1/64s, -5 = 1/32s.
            // For retroreflective markers under IR/visible illumination, -6 to -7 works well.
            // The user can adjust this via the Exposure slider in the UI.
            if (_settings.ManualExposure != 0)
            {
                bool expSet = _capture.Set(VideoCaptureProperties.Exposure, _settings.ManualExposure);
                _logger.LogInformation("Manual exposure {V}: {Result}", _settings.ManualExposure, expSet ? "OK" : "not supported");
            }

            // Disable auto white balance (irrelevant for monochrome but prevents driver interference)
            _capture.Set(VideoCaptureProperties.AutoWB, 0);

            // Fix gain to prevent AGC from compensating for disabled auto-exposure
            if (_settings.ManualGain >= 0)
            {
                bool gainSet = _capture.Set(VideoCaptureProperties.Gain, _settings.ManualGain);
                _logger.LogInformation("Manual gain {V}: {Result}", _settings.ManualGain, gainSet ? "OK" : "not supported");
            }

            var w   = _capture.Get(VideoCaptureProperties.FrameWidth);
            var h   = _capture.Get(VideoCaptureProperties.FrameHeight);
            var fps = _capture.Get(VideoCaptureProperties.Fps);
            var exp = _capture.Get(VideoCaptureProperties.Exposure);
            var gain = _capture.Get(VideoCaptureProperties.Gain);

            _logger.LogInformation(
                "Camera ready: {W}x{H} @ {FPS}fps | exposure={E} | gain={G}",
                w, h, fps, exp, gain);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while opening camera.");
            return false;
        }
    }

    public void StartCapture(CancellationToken cancellationToken)
    {
        if (!IsOpen) throw new InvalidOperationException("Camera not open.");
        _isRunning = true;
        Task.Run(() => CaptureLoop(cancellationToken), cancellationToken);
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            if (_capture!.Read(frame) && !frame.Empty())
                FrameReady?.Invoke(this, frame.Clone());
            else
                Thread.Sleep(5);
        }
    }

    public void Stop() => _isRunning = false;

    public void Dispose()
    {
        _isRunning = false;
        _capture?.Dispose();
        _capture = null;
    }
}
