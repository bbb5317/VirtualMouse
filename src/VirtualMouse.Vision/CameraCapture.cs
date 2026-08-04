using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Manages camera capture with a two-thread design to prevent buffer backlog.
///
/// THE PROBLEM:
/// DirectShow (and most UVC drivers) maintain an internal ring buffer of
/// decoded frames. The OV9281 at 120fps produces a new frame every ~8ms.
/// If the application reads frames slower than that (because each frame
/// goes through detection, grouping, gesture logic, and WPF rendering),
/// frames pile up in the buffer. The application then reads them in order,
/// playing back stale frames — this is the "slow motion" effect.
///
/// THE FIX — two-thread design:
///
///   GRAB THREAD (runs as fast as the camera produces frames):
///     Calls VideoCapture.Read() in a tight loop, discarding every frame
///     except the most recent. This drains the DirectShow buffer continuously
///     so it never fills up. The latest frame is stored in a volatile slot.
///
///   PROCESS THREAD (runs at the pipeline's natural speed):
///     Picks up the latest frame from the slot, clones it, and raises
///     FrameReady. If no new frame has arrived since the last pick-up,
///     it waits briefly and tries again. The pipeline always sees the
///     current frame, never a stale one.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly TrackingSettings _settings;
    private VideoCapture? _capture;
    private bool _isRunning;

    // Latest-frame slot — grab thread writes, process thread reads
    private Mat? _latestFrame;
    private readonly object _frameLock = new();
    private long _frameSerial;      // incremented by grab thread on each new frame
    private long _lastProcessed;    // last serial seen by process thread

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

            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);
            _capture.Set(VideoCaptureProperties.Fps,         _settings.TargetFps);
            _capture.Set(VideoCaptureProperties.ConvertRgb,  0);

            // Minimise the internal DirectShow buffer to 1 frame so the grab
            // thread always gets the freshest frame with minimal latency.
            _capture.Set(VideoCaptureProperties.BufferSize, 1);

            TrySet(VideoCaptureProperties.Brightness,        _settings.CamBrightness);
            TrySet(VideoCaptureProperties.Contrast,          _settings.CamContrast);
            TrySet(VideoCaptureProperties.Saturation,        _settings.CamSaturation);
            TrySet(VideoCaptureProperties.Sharpness,         _settings.CamSharpness);
            TrySet(VideoCaptureProperties.Gamma,             _settings.CamGamma);
            TrySet(VideoCaptureProperties.WhiteBalanceBlueU, _settings.CamWhiteBalance);
            TrySet((VideoCaptureProperties)8,                _settings.CamBacklightComp); // BacklightComp
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
        _isRunning = true;
        _frameSerial = 0;
        _lastProcessed = -1;

        // Grab thread — drains the camera buffer at full speed
        Task.Run(() => GrabLoop(cancellationToken), cancellationToken);

        // Process thread — picks up the latest frame and raises FrameReady
        Task.Run(() => ProcessLoop(cancellationToken), cancellationToken);
    }

    // ── Grab thread ────────────────────────────────────────────────────────

    private void GrabLoop(CancellationToken ct)
    {
        using var frame = new Mat();
        while (_isRunning && !ct.IsCancellationRequested)
        {
            if (!_capture!.Read(frame) || frame.Empty())
            {
                Thread.Sleep(1);
                continue;
            }

            // Swap the latest frame slot (clone once, discard old)
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
        _logger.LogDebug("Grab thread stopped.");
    }

    // ── Process thread ─────────────────────────────────────────────────────

    private void ProcessLoop(CancellationToken ct)
    {
        while (_isRunning && !ct.IsCancellationRequested)
        {
            long serial = Interlocked.Read(ref _frameSerial);
            if (serial == _lastProcessed)
            {
                // No new frame yet — yield briefly
                Thread.Sleep(1);
                continue;
            }

            Mat? toProcess;
            lock (_frameLock)
            {
                toProcess = _latestFrame?.Clone();
                _lastProcessed = serial;
            }

            if (toProcess != null)
                FrameReady?.Invoke(this, toProcess);
        }
        _logger.LogDebug("Process thread stopped.");
    }

    public void Stop() => _isRunning = false;

    public void Dispose()
    {
        _isRunning = false;
        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
        _capture?.Dispose();
        _capture = null;
    }
}
