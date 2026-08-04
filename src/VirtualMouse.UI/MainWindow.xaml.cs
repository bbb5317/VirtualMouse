using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Input;
using VirtualMouse.Vision;

namespace VirtualMouse.UI;

/// <summary>
/// Staged diagnostic window. Each stage adds exactly one pipeline component.
/// Select a stage, press Open Camera, observe whether the camera shows a live
/// image. The first stage that causes a black screen identifies the culprit.
///
/// Stage 0 — Raw VideoCapture, single thread, no processing.
/// Stage 1 — Wrap in CameraCapture class (single thread, no two-thread design).
/// Stage 2 — Enable two-thread grab/process design inside CameraCapture.
/// Stage 3 — Add MarkerDetector.Detect() on each frame (no overlay drawn).
/// Stage 4 — Draw debug overlay on frame before displaying.
/// Stage 5 — Full pipeline: gesture recognition + mouse injection.
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    // ── Services ───────────────────────────────────────────────────────────
    private readonly CameraEnumerator _cameraEnumerator;
    private readonly TrackingSettings _settings;
    private readonly MarkerDetector _detector;
    private readonly MarkerGrouper _grouper;
    private readonly GestureRecognizer _gestureRecognizer;
    private readonly WindowsMouseController _mouseController;

    // ── State ──────────────────────────────────────────────────────────────
    private int _activeStage = 0;
    private CancellationTokenSource? _cts;
    private VideoCapture? _rawCapture;   // Stage 0
    private CameraCapture? _camCapture;  // Stages 1-5

    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;
    private long _totalFrames;

    private static readonly Button[] _stageButtons = null!; // set in constructor

    private static readonly string[] StageDescriptions =
    {
        "Stage 0 — Raw VideoCapture, single thread, no processing.",
        "Stage 1 — CameraCapture wrapper class, single thread, no two-thread design.",
        "Stage 2 — CameraCapture with two-thread grab/process design.",
        "Stage 3 — Two-thread + MarkerDetector.Detect() called on each frame (no overlay).",
        "Stage 4 — Two-thread + MarkerDetector.Detect() + debug overlay drawn on frame.",
        "Stage 5 — Full pipeline: detection + gesture recognition + mouse injection.",
    };

    public MainWindow(
        CameraEnumerator cameraEnumerator,
        TrackingSettings settings,
        MarkerDetector detector,
        MarkerGrouper grouper,
        GestureRecognizer gestureRecognizer,
        WindowsMouseController mouseController)
    {
        _cameraEnumerator = cameraEnumerator;
        _settings = settings;
        _detector = detector;
        _grouper = grouper;
        _gestureRecognizer = gestureRecognizer;
        _mouseController = mouseController;

        InitializeComponent();
        Closing += (_, _) => CloseCamera();
        RefreshCameraList();
        SetStage(0);
    }

    // ── Camera list ────────────────────────────────────────────────────────

    private void RefreshCameraList()
    {
        CameraComboBox.Items.Clear();
        var devices = _cameraEnumerator.Enumerate();
        if (devices.Count == 0)
        {
            CameraComboBox.Items.Add("No cameras found");
            CameraComboBox.SelectedIndex = 0;
            OpenButton.IsEnabled = false;
            return;
        }
        foreach (var d in devices) CameraComboBox.Items.Add(d);
        var match = devices.FirstOrDefault(d => d.Index == _settings.CameraDeviceIndex);
        CameraComboBox.SelectedItem = match ?? devices[0];
        OpenButton.IsEnabled = true;
    }

    private void RefreshCameras_Click(object sender, RoutedEventArgs e) => RefreshCameraList();

    // ── Stage selection ────────────────────────────────────────────────────

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int stage))
            SetStage(stage);
    }

    private void SetStage(int stage)
    {
        _activeStage = stage;
        StageDescLabel.Text = StageDescriptions[stage];

        // Highlight active button
        foreach (var (btn, i) in new[]
        {
            (S0Btn, 0), (S1Btn, 1), (S2Btn, 2),
            (S3Btn, 3), (S4Btn, 4), (S5Btn, 5)
        })
        {
            btn.Background = new SolidColorBrush(
                i == stage
                    ? Color.FromRgb(26, 90, 160)
                    : Color.FromRgb(60, 60, 60));
        }
    }

    // ── Open / Close ───────────────────────────────────────────────────────

    private void OpenCamera_Click(object sender, RoutedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is not CameraDeviceInfo device || device.Index < 0)
        {
            MessageBox.Show("Select a camera first.", "No Camera",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.CameraDeviceIndex = device.Index;

        bool opened = _activeStage == 0
            ? OpenStage0(device.Index)
            : OpenStages1to5(device.Index);

        if (!opened)
        {
            MessageBox.Show($"Could not open camera \"{device.Name}\".",
                "Camera Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        OpenButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        SetStatusPill($"STAGE {_activeStage} RUNNING", Color.FromRgb(0, 160, 60));
        _totalFrames = 0;
    }

    private void CloseCamera_Click(object sender, RoutedEventArgs e) => CloseCamera();

    private void CloseCamera()
    {
        _cts?.Cancel();
        _cts = null;

        if (_activeStage == 0)
        {
            Thread.Sleep(150);
            _rawCapture?.Release();
            _rawCapture?.Dispose();
            _rawCapture = null;
        }
        else
        {
            _camCapture?.Stop();
            _camCapture = null;
            _mouseController.ReleaseAll();
            _gestureRecognizer.Reset();
            _detector.ResetIdentification();
        }

        Dispatcher.InvokeAsync(() =>
        {
            OpenButton.IsEnabled = true;
            CloseButton.IsEnabled = false;
            SetStatusPill("STOPPED", Color.FromRgb(180, 40, 40));
            BlobCountLabel.Text = "--";
        });
    }

    // ── Stage 0 — raw VideoCapture, single thread ──────────────────────────

    private bool OpenStage0(int index)
    {
        _rawCapture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!_rawCapture.IsOpened()) { _rawCapture.Dispose(); _rawCapture = null; return false; }

        _rawCapture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        _rawCapture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
        _rawCapture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);

        _cts = new CancellationTokenSource();
        Task.Run(() => Stage0Loop(_cts.Token));
        return true;
    }

    private void Stage0Loop(CancellationToken ct)
    {
        using var frame = new Mat();
        while (!ct.IsCancellationRequested)
        {
            if (_rawCapture == null || !_rawCapture.IsOpened()) break;
            if (!_rawCapture.Read(frame) || frame.Empty()) { Thread.Sleep(10); continue; }
            _totalFrames++;
            _frameCount++;
            if (_fpsStopwatch.ElapsedMilliseconds < 100) continue;
            UpdatePreview(frame.Clone(), null);
        }
    }

    // ── Stages 1-5 — via CameraCapture ────────────────────────────────────

    private bool OpenStages1to5(int index)
    {
        // Stage 1: single-thread CameraCapture
        // Stage 2+: two-thread CameraCapture (same class, controlled by _activeStage)
        _camCapture = new CameraCapture(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraCapture>.Instance,
            _settings,
            useTwoThreads: _activeStage >= 2);

        if (!_camCapture.Open()) { _camCapture = null; return false; }

        _cts = new CancellationTokenSource();
        _camCapture.FrameReady += OnFrameReady;
        _camCapture.StartCapture(_cts.Token);
        return true;
    }

    private void OnFrameReady(object? sender, Mat frame)
    {
        using (frame)
        {
            _totalFrames++;
            _frameCount++;

            int blobCount = 0;
            Mat displayFrame = frame;
            bool disposeDisplay = false;

            // Stage 3+: run detection
            if (_activeStage >= 3)
            {
                var blobs = _detector.Detect(frame);
                blobCount = blobs.Count;

                // Stage 4+: draw overlay
                if (_activeStage >= 4)
                {
                    displayFrame = _detector.DrawDebug(frame, blobs);
                    disposeDisplay = true;
                }

                // Stage 5: gesture + mouse
                if (_activeStage >= 5 && !_detector.IsIdentifying)
                {
                    var groups  = _grouper.Group(blobs);
                    var gesture = _gestureRecognizer.Process(groups);
                    _mouseController.Apply(gesture);
                }
            }

            if (_fpsStopwatch.ElapsedMilliseconds >= 100)
                UpdatePreview(displayFrame.Clone(), blobCount > 0 ? blobCount : (int?)null);

            if (disposeDisplay) displayFrame.Dispose();
        }
    }

    // ── UI update ──────────────────────────────────────────────────────────

    private void UpdatePreview(Mat frame, int? blobCount)
    {
        double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
        _frameCount = 0;
        _fpsStopwatch.Restart();

        var bitmap = MatToBitmapSource(frame);
        frame.Dispose();
        bitmap.Freeze();
        long total = _totalFrames;
        string blobs = blobCount.HasValue ? $"{blobCount}" : "--";

        Dispatcher.InvokeAsync(() =>
        {
            PreviewImage.Source = bitmap;
            FpsLabel.Text = $"{fps:F0}";
            FrameCountLabel.Text = $"{total}";
            BlobCountLabel.Text = blobs;
        });
    }

    private void SetStatusPill(string text, Color color)
    {
        StatusPill.Background = new SolidColorBrush(color);
        StatusPillLabel.Text = text;
    }

    private static BitmapSource MatToBitmapSource(Mat mat)
    {
        Mat bgr = mat;
        bool dispose = false;
        if (mat.Channels() == 1)
        {
            bgr = new Mat();
            Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
            dispose = true;
        }
        int w = bgr.Width, h = bgr.Height, stride = (int)bgr.Step();
        var pixels = new byte[stride * h];
        Marshal.Copy(bgr.Data, pixels, 0, pixels.Length);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        if (dispose) bgr.Dispose();
        return bmp;
    }
}
