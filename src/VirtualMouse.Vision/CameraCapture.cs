using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages camera capture using a two-thread design to prevent buffer backlog.
///
/// GRAB THREAD: drains the DirectShow buffer at full camera speed,
///   keeping only the latest frame in a slot.
/// PROCESS THREAD: picks up the latest frame and raises FrameReady.
///
/// Note: if the camera shows a black screen, unplug and replug the USB cable
/// before starting tracking. No software reset is performed.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly TrackingSettings _settings;

    private VideoCapture? _capture;
    private volatile bool _isRunning;
    private Task? _grabTask;
    private Task? _processTask;

    private Mat? _latestFrame;
    private readonly object _frameLock = new();
    private long _frameSerial;
    private long _lastProcessed;

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

            // Minimise internal buffer to 1 frame to reduce latency
            _capture.Set(VideoCaptureProperties.BufferSize,  1);
            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);
            _capture.Set(VideoCaptureProperties.Fps,         _settings.TargetFps);
            _capture.Set(VideoCaptureProperties.ConvertRgb,  0);

            TrySet(VideoCaptureProperties.Brightness,        _settings.CamBrightness);
            TrySet(VideoCaptureProperties.Contrast,          _settings.CamContrast);
            TrySet(VideoCaptureProperties.Saturation,        _settings.CamSaturation);
            TrySet(VideoCaptureProperties.Sharpness,         _settings.CamSharpness);
            TrySet(VideoCaptureProperties.Gamma,             _settings.CamGamma);
            TrySet(VideoCaptureProperties.WhiteBalanceBlueU, _settings.CamWhiteBalance);
            TrySet((VideoCaptureProperties)8,                _settings.CamBacklightComp);
            TrySet(VideoCaptureProperties.Gain,              _settings.CamGain);

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
        _isRunning     = true;
        _frameSerial   = 0;
        _lastProcessed = -1;
        _grabTask    = Task.Run(() => GrabLoop(cancellationToken),    cancellationToken);
        _processTask = Task.Run(() => ProcessLoop(cancellationToken), cancellationToken);
    }

    private void GrabLoop(CancellationToken ct)
    {
        using var frame = new Mat();
        while (_isRunning && !ct.IsCancellationRequested)
        {
            if (!_capture!.Read(frame) || frame.Empty()) { Thread.Sleep(1); continue; }
            var cloned = frame.Clone();
            Mat? old;
            lock (_frameLock)
            {
                old = _latestFrame;
                _latestFrame = cloned;
                Interlocked.Increment(ref _frameSerial);
            }
            old?.Dispose();
        }
        _logger.LogDebug("Grab thread exited.");
    }

    private void ProcessLoop(CancellationToken ct)
    {
        while (_isRunning && !ct.IsCancellationRequested)
        {
            long serial = Interlocked.Read(ref _frameSerial);
            if (serial == _lastProcessed) { Thread.Sleep(1); continue; }
            Mat? toProcess;
            lock (_frameLock)
            {
                toProcess = _latestFrame?.Clone();
                _lastProcessed = serial;
            }
            if (toProcess != null) FrameReady?.Invoke(this, toProcess);
        }
        _logger.LogDebug("Process thread exited.");
    }

    public void Stop()
    {
        _isRunning = false;
        try
        {
            Task.WhenAll(
                _grabTask    ?? Task.CompletedTask,
                _processTask ?? Task.CompletedTask)
                .Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* timeout acceptable */ }

        lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        _logger.LogInformation("Camera stopped and released.");
    }

    public void Dispose() => Stop();
}
