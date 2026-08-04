using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages camera capture with automatic device reset and frame verification.
///
/// On every Open():
///   1. Reset the device via SetupAPI (disable → wait → enable → wait).
///   2. Open VideoCapture.
///   3. Read up to 30 frames to verify the sensor is producing live output.
///      If all frames are black (mean brightness < 1.0), the sensor has not
///      recovered — perform another reset cycle and retry (up to 3 attempts).
///   4. Start the two-thread grab/process loop.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly TrackingSettings _settings;
    private readonly UsbDeviceResetter _resetter;

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

    public CameraCapture(
        ILogger<CameraCapture> logger,
        TrackingSettings settings,
        UsbDeviceResetter resetter)
    {
        _logger = logger;
        _settings = settings;
        _resetter = resetter;
    }

    public bool Open()
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _logger.LogInformation("Camera open attempt {A}/{M}...", attempt, maxAttempts);

            // Reset the device before every attempt
            _resetter.ResetCamera(_settings.CameraResetNameFilter);

            if (!TryOpenCapture())
            {
                _logger.LogWarning("VideoCapture.Open() failed on attempt {A}.", attempt);
                continue;
            }

            // Verify the sensor is producing live frames
            if (VerifyLiveFrames())
            {
                _logger.LogInformation("Camera verified live on attempt {A}.", attempt);
                return true;
            }

            _logger.LogWarning(
                "Camera opened but produces black frames on attempt {A}. " +
                "Releasing and retrying...", attempt);
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }

        _logger.LogError("Camera failed to produce live frames after {M} attempts.", maxAttempts);
        return false;
    }

    private bool TryOpenCapture()
    {
        try
        {
            _capture = new VideoCapture(_settings.CameraDeviceIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened()) return false;

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
            _logger.LogInformation("VideoCapture opened: {W}x{H} @ {FPS}fps", w, h, fps);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception opening VideoCapture.");
            return false;
        }
    }

    /// <summary>
    /// Reads up to 30 frames and checks whether any of them have a mean
    /// brightness above 1.0. A camera stuck in a black-screen state will
    /// produce frames with mean brightness of exactly 0.
    /// </summary>
    private bool VerifyLiveFrames()
    {
        _logger.LogInformation("Verifying camera output (reading up to 30 frames)...");
        using var frame = new Mat();
        for (int i = 0; i < 30; i++)
        {
            if (!_capture!.Read(frame) || frame.Empty()) continue;
            double mean = Cv2.Mean(frame).Val0;
            _logger.LogDebug("  Frame {I}: mean brightness = {M:F2}", i, mean);
            if (mean > 1.0)
            {
                _logger.LogInformation("  Live frame confirmed at frame {I} (mean={M:F2}).", i, mean);
                return true;
            }
        }
        _logger.LogWarning("  All 30 frames were black (mean ≤ 1.0). Sensor not recovered.");
        return false;
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
