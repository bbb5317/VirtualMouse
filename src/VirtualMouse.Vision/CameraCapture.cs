using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages the camera capture session using OpenCvSharp4 (VideoCapture / DirectShow).
///
/// Camera settings are applied from TrackingSettings, matching the values
/// configured in the Video Proc Amp panel of the ArduCam OV9281 driver:
///   Brightness: -64, Contrast: 64, Sharpness: 3, Gamma: 72,
///   White Balance: 4650, Backlight Comp: 1, Gain: 0.
/// AGC (auto-exposure) is left at the driver default (enabled) so the
/// camera can adapt its exposure to the ambient lighting conditions.
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

            // ── Resolution and framerate ───────────────────────────────────
            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);
            _capture.Set(VideoCaptureProperties.Fps,         _settings.TargetFps);
            _capture.Set(VideoCaptureProperties.ConvertRgb,  0); // raw mono

            // ── Video Proc Amp settings (from user's driver panel) ─────────
            // These map to DirectShow IAMVideoProcAmp properties.
            // OpenCV exposes a subset via VideoCaptureProperties.
            TrySet(VideoCaptureProperties.Brightness,    _settings.CamBrightness);
            TrySet(VideoCaptureProperties.Contrast,      _settings.CamContrast);
            TrySet(VideoCaptureProperties.Saturation,    _settings.CamSaturation);
            TrySet(VideoCaptureProperties.Sharpness,     _settings.CamSharpness);
            TrySet(VideoCaptureProperties.Gamma,         _settings.CamGamma);
            TrySet(VideoCaptureProperties.WhiteBalanceBlueU, _settings.CamWhiteBalance);
            TrySet(VideoCaptureProperties.BacklightFlicker, _settings.CamBacklightComp);
            TrySet(VideoCaptureProperties.Gain,          _settings.CamGain);

            // ── Leave AGC / auto-exposure at driver default ────────────────
            // Do NOT disable auto-exposure. The OV9281 needs AGC to keep
            // the markers visible under varying ambient lighting.
            // Motion detection will use shape + motion-calibration instead.

            var w   = _capture.Get(VideoCaptureProperties.FrameWidth);
            var h   = _capture.Get(VideoCaptureProperties.FrameHeight);
            var fps = _capture.Get(VideoCaptureProperties.Fps);
            _logger.LogInformation("Camera ready: {W}x{H} @ {FPS}fps", w, h, fps);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while opening camera.");
            return false;
        }
    }

    private void TrySet(VideoCaptureProperties prop, double value)
    {
        bool ok = _capture!.Set(prop, value);
        _logger.LogDebug("Set {Prop}={Value}: {Result}", prop, value, ok ? "OK" : "not supported");
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
