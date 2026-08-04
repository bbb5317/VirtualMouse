using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Camera capture with optional two-thread design.
/// useTwoThreads=false → single CaptureLoop (Stage 1 baseline).
/// useTwoThreads=true  → separate grab + process threads (Stage 2+).
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly TrackingSettings _settings;
    private readonly bool _useTwoThreads;

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
        bool useTwoThreads = true)
    {
        _logger = logger;
        _settings = settings;
        _useTwoThreads = useTwoThreads;
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

            // CRITICAL: set MJPEG format FIRST.
            // The OV9281 only outputs MJPEG and YUV2. Without this, OpenCV
            // requests BGR24 which the camera cannot deliver.
            _capture.Set(VideoCaptureProperties.FourCC,
                VideoWriter.FourCC('M', 'J', 'P', 'G'));
            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);

            var w   = _capture.Get(VideoCaptureProperties.FrameWidth);
            var h   = _capture.Get(VideoCaptureProperties.FrameHeight);
            var fps = _capture.Get(VideoCaptureProperties.Fps);
            _logger.LogInformation(
                "Camera ready: {W}x{H} @ {FPS}fps (MJPEG, twoThreads={T})",
                w, h, fps, _useTwoThreads);
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
        _isRunning     = true;
        _frameSerial   = 0;
        _lastProcessed = -1;

        if (_useTwoThreads)
        {
            _grabTask    = Task.Run(() => GrabLoop(cancellationToken),    cancellationToken);
            _processTask = Task.Run(() => ProcessLoop(cancellationToken), cancellationToken);
        }
        else
        {
            _grabTask = Task.Run(() => SingleThreadLoop(cancellationToken), cancellationToken);
        }
    }

    // ── Single-thread loop (Stage 1) ───────────────────────────────────────

    private void SingleThreadLoop(CancellationToken ct)
    {
        using var frame = new Mat();
        while (_isRunning && !ct.IsCancellationRequested)
        {
            if (!_capture!.Read(frame) || frame.Empty()) { Thread.Sleep(10); continue; }
            FrameReady?.Invoke(this, frame.Clone());
        }
        _logger.LogDebug("Single-thread loop exited.");
    }

    // ── Two-thread grab loop (Stage 2+) ────────────────────────────────────

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

    // ── Stop / Dispose ─────────────────────────────────────────────────────

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
