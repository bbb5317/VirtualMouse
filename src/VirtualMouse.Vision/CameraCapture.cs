using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages camera capture using a two-thread design to prevent buffer backlog.
///
/// DESIGN NOTES — what NOT to do with the ArduCam OV9281:
///
///   DO NOT set ConvertRgb = 0.
///     This sends an unsupported format negotiation to the monochrome sensor
///     driver and can leave the sensor in a state that produces black frames
///     until the USB cable is physically unplugged.
///
///   DO NOT set BufferSize = 1.
///     Forcing a 1-frame internal buffer causes the DirectShow graph to drop
///     the stream if the grab thread is even slightly late, and the OV9281
///     driver does not always recover gracefully from a dropped stream.
///
///   DO NOT set Fps after the camera is open.
///     Requesting a framerate change via Set(Fps, x) forces DirectShow to
///     renegotiate the media type mid-stream. The OV9281 driver does not
///     support this and can corrupt the streaming pipeline.
///
///   DO NOT overwrite Video Proc Amp settings (Brightness, Contrast, etc.)
///     from code. These are persistent in the driver registry. The user sets
///     them once via the driver's own settings panel (AMCap → Options →
///     Video Capture Filter). Overwriting them on every Open() risks sending
///     values the driver rejects, which can trigger a sensor state fault.
///
/// SAFE APPROACH:
///   Set only FrameWidth and FrameHeight before the first Read().
///   Let the driver choose its own framerate, buffer size, and pixel format.
///   The driver's Video Proc Amp settings (set via AMCap) persist and apply
///   automatically — no need to set them in code.
///
/// TWO-THREAD DESIGN (prevents slow-motion buffer backlog):
///   GRAB THREAD: calls Read() in a tight loop, always keeping only the
///     latest frame in a slot. This drains the DirectShow buffer continuously.
///   PROCESS THREAD: picks up the latest frame and raises FrameReady.
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

            // Set resolution only — safe on all UVC drivers.
            // Do NOT set Fps, BufferSize, ConvertRgb, or any Video Proc Amp
            // properties here. See class-level documentation for the reasons.
            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);

            var w   = _capture.Get(VideoCaptureProperties.FrameWidth);
            var h   = _capture.Get(VideoCaptureProperties.FrameHeight);
            var fps = _capture.Get(VideoCaptureProperties.Fps);
            _logger.LogInformation("Camera ready: {W}x{H} @ {FPS}fps (driver default)", w, h, fps);
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
