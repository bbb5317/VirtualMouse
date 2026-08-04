using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages camera capture using a two-thread design to prevent buffer backlog.
///
/// KEY FIX — MJPEG format negotiation:
///   The OV9281 natively outputs MJPEG and YUV2 only. OpenCV's DirectShow
///   backend requests BGR24 by default. When DirectShow cannot negotiate
///   BGR24, the camera firmware enters a stuck state producing black frames
///   that persists until the USB cable is physically unplugged.
///   Setting FourCC = MJPG before resolution forces DirectShow to negotiate
///   MJPEG, which the camera supports. OpenCV decodes it to BGR internally.
///
/// TWO-THREAD DESIGN (prevents slow-motion buffer backlog):
///   GRAB THREAD: calls Read() in a tight loop, always keeping only the
///     latest frame in a slot. This drains the DirectShow buffer continuously
///     so stale frames never accumulate.
///   PROCESS THREAD: picks up the latest frame and raises FrameReady.
///     Always sees the current frame, never a stale one.
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

            // CRITICAL: set MJPEG format FIRST, before resolution.
            // The OV9281 only outputs MJPEG and YUV2. Without this, OpenCV
            // requests BGR24 which the camera cannot deliver, causing a
            // format negotiation failure that corrupts the sensor state.
            _capture.Set(VideoCaptureProperties.FourCC,
                VideoWriter.FourCC('M', 'J', 'P', 'G'));

            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);

            var w   = _capture.Get(VideoCaptureProperties.FrameWidth);
            var h   = _capture.Get(VideoCaptureProperties.FrameHeight);
            var fps = _capture.Get(VideoCaptureProperties.Fps);
            _logger.LogInformation("Camera ready: {W}x{H} @ {FPS}fps (MJPEG)", w, h, fps);
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
