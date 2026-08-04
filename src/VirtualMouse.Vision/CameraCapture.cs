using OpenCvSharp;
using VirtualMouse.Core;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Camera capture with selectable read strategy for diagnostic staging.
///
/// useGrabRetrieve=false (Stage 1): uses Read() — same as the known-working diagnostic.
/// useGrabRetrieve=true  (Stage 2+): uses Grab()+Retrieve() to drain the buffer.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    private readonly ILogger<CameraCapture> _logger;
    private readonly TrackingSettings _settings;
    private readonly bool _useGrabRetrieve;

    private VideoCapture? _capture;
    private volatile bool _isRunning;
    private Task? _captureTask;

    public event EventHandler<Mat>? FrameReady;
    public bool IsOpen => _capture?.IsOpened() ?? false;

    public CameraCapture(
        ILogger<CameraCapture> logger,
        TrackingSettings settings,
        bool useGrabRetrieve = false)
    {
        _logger = logger;
        _settings = settings;
        _useGrabRetrieve = useGrabRetrieve;
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
            _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M','J','P','G'));
            _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);
            _logger.LogInformation("Camera open (MSMF, {Mode})", _useGrabRetrieve ? "Grab+Retrieve" : "Read");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception opening camera.");
            return false;
        }
    }

    public void StartCapture(CancellationToken ct)
    {
        if (!IsOpen) throw new InvalidOperationException("Camera not open.");
        _isRunning = true;
        _captureTask = _useGrabRetrieve
            ? Task.Run(() => GrabRetrieveLoop(ct), ct)
            : Task.Run(() => ReadLoop(ct), ct);
    }

    // ── Read loop (Stage 1) — identical to working diagnostic ─────────────

    private void ReadLoop(CancellationToken ct)
    {
        using var frame = new Mat();
        while (_isRunning && !ct.IsCancellationRequested)
        {
            if (_capture == null || !_capture.IsOpened()) break;
            if (!_capture.Read(frame) || frame.Empty()) { Thread.Sleep(10); continue; }
            FrameReady?.Invoke(this, frame.Clone());
        }
        _logger.LogDebug("Read loop exited.");
    }

    // ── Grab+Retrieve loop (Stage 2+) ─────────────────────────────────────

    private void GrabRetrieveLoop(CancellationToken ct)
    {
        using var frame = new Mat();
        while (_isRunning && !ct.IsCancellationRequested)
        {
            if (_capture == null || !_capture.IsOpened()) break;

            // Drain all queued frames — each Grab() is cheap (no decode)
            int grabCount = 0;
            while (_capture.Grab()) grabCount++;

            if (grabCount == 0) { Thread.Sleep(1); continue; }

            // Decode only the latest grabbed frame
            if (!_capture.Retrieve(frame) || frame.Empty()) continue;

            FrameReady?.Invoke(this, frame.Clone());
        }
        _logger.LogDebug("Grab+Retrieve loop exited.");
    }

    public void Stop()
    {
        _isRunning = false;

        // Release the VideoCapture FIRST so that any blocking Read()/Grab() call
        // on the background thread returns immediately with an error.
        // Only then wait for the loop task to exit.
        var cap = _capture;
        _capture = null;
        try { cap?.Release(); } catch { }
        try { cap?.Dispose(); } catch { }

        try { _captureTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { }

        _logger.LogInformation("Camera stopped.");
    }

    public void Dispose() => Stop();
}
