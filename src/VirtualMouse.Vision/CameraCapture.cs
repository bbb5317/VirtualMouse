using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Camera capture using Windows Media Foundation (MSMF) with Grab/Retrieve
/// buffer drain to always deliver the latest frame.
///
/// Why MSMF: ArduCam documentation explicitly recommends CAP_MSMF over
/// CAP_DSHOW for their UVC cameras. DSHOW causes black frames and very low
/// FPS on the OV9281. AMCap uses Media Foundation internally.
///
/// Why Grab/Retrieve: The OV9281 at 120fps fills the buffer faster than the
/// pipeline can process. Calling Read() reads stale buffered frames in order,
/// producing a slow-motion effect. Grab() drains the buffer without decoding
/// (very fast), then Retrieve() decodes only the latest frame.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly TrackingSettings _settings;

    private VideoCapture? _capture;
    private volatile bool _isRunning;
    private Task? _captureTask;

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
            _capture = new VideoCapture(_settings.CameraDeviceIndex, VideoCaptureAPIs.MSMF);
            if (!_capture.IsOpened())
            {
                _logger.LogError("Failed to open camera index {I} via MSMF.", _settings.CameraDeviceIndex);
                return false;
            }

            _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);

            var w   = _capture.Get(VideoCaptureProperties.FrameWidth);
            var h   = _capture.Get(VideoCaptureProperties.FrameHeight);
            var fps = _capture.Get(VideoCaptureProperties.Fps);
            _logger.LogInformation("Camera ready: {W}x{H} @ {FPS}fps (MSMF/MJPEG)", w, h, fps);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception opening camera.");
            return false;
        }
    }

    public void StartCapture(CancellationToken cancellationToken)
    {
        if (!IsOpen) throw new InvalidOperationException("Camera not open.");
        _isRunning = true;
        _captureTask = Task.Run(() => CaptureLoop(cancellationToken), cancellationToken);
    }

    private void CaptureLoop(CancellationToken ct)
    {
        using var frame = new Mat();

        while (_isRunning && !ct.IsCancellationRequested)
        {
            if (_capture == null || !_capture.IsOpened()) break;

            // Drain the MSMF buffer: call Grab() repeatedly until it returns false
            // (buffer empty). Each Grab() call is cheap — no pixel decode.
            // We count how many frames we grabbed; if zero, the camera hasn't
            // produced a new frame yet so we yield for 1ms and try again.
            int grabCount = 0;
            while (_capture.Grab())
                grabCount++;

            if (grabCount == 0) { Thread.Sleep(1); continue; }

            // Retrieve the last grabbed frame (the most recent one)
            if (!_capture.Retrieve(frame) || frame.Empty()) continue;

            FrameReady?.Invoke(this, frame.Clone());
        }

        _logger.LogDebug("Capture loop exited.");
    }

    public void Stop()
    {
        _isRunning = false;
        try { _captureTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch { }
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        _logger.LogInformation("Camera stopped.");
    }

    public void Dispose() => Stop();
}
