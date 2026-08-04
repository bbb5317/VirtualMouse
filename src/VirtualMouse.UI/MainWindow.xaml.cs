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
/// Select a stage, press Open Camera, observe whether preview shows a live image.
///
/// Stage 0 — VideoCapture.Read() directly in MainWindow. No wrapper. No processing.
///           IDENTICAL to the known-working v0.7.2-diag build.
/// Stage 1 — CameraCapture wrapper class using Read() internally (single thread).
/// Stage 2 — CameraCapture using Grab()+Retrieve() internally (single thread).
/// Stage 3 — Stage 2 + MarkerDetector.Detect() called on each frame.
/// Stage 4 — Stage 3 + DrawDebug overlay drawn on frame before display.
/// Stage 5 — Stage 4 + GestureRecognizer + WindowsMouseController.
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly CameraEnumerator _cameraEnumerator;
    private readonly TrackingSettings _settings;
    private readonly MarkerDetector _detector;
    private readonly MarkerGrouper _grouper;
    private readonly GestureRecognizer _gestureRecognizer;
    private readonly WindowsMouseController _mouseController;

    private int _activeStage = 0;
    private CancellationTokenSource? _cts;

    // Stage 0 only: raw VideoCapture used directly
    private VideoCapture? _rawCapture;

    // Stages 1-5: CameraCapture wrapper
    private CameraCapture? _camCapture;

    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;
    private long _totalFrames;

    private static readonly string[] StageDescriptions =
    {
        "Stage 0 — VideoCapture.Read() directly in MainWindow. No wrapper. No processing. IDENTICAL to the known-working v0.7.2-diag build.",
        "Stage 1 — CameraCapture wrapper class using Read() internally (single thread).",
        "Stage 2 — CameraCapture using Grab()+Retrieve() to drain buffer (single thread).",
        "Stage 3 — Stage 2 + MarkerDetector.Detect() called on each frame (no overlay).",
        "Stage 4 — Stage 3 + DrawDebug overlay drawn on frame before display.",
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
        Closing += (_, _) => CloseCamera_Click(this, new RoutedEventArgs());
        RefreshCameraList();
        SetStage(0);
    }

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

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int s))
            SetStage(s);
    }

    private void SetStage(int stage)
    {
        _activeStage = stage;
        StageDescLabel.Text = StageDescriptions[stage];
        foreach (var (btn, i) in new[] {
            (S0Btn,0),(S1Btn,1),(S2Btn,2),(S3Btn,3),(S4Btn,4),(S5Btn,5) })
        {
            btn.Background = new SolidColorBrush(
                i == stage ? Color.FromRgb(26,90,160) : Color.FromRgb(60,60,60));
        }
    }

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
            MessageBox.Show($"Could not open \"{device.Name}\".",
                "Camera Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        OpenButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        SetPill($"STAGE {_activeStage} RUNNING", Color.FromRgb(0,160,60));
        _totalFrames = 0;
    }

    private void CloseCamera_Click(object sender, RoutedEventArgs e)
    {
        // Disable the button immediately so user cannot click twice
        CloseButton.IsEnabled = false;
        SetPill("STOPPING...", Color.FromRgb(140, 100, 0));

        // Run teardown on a background thread — never block the UI thread
        Task.Run(() =>
        {
            _cts?.Cancel();
            _cts = null;

            if (_activeStage == 0)
            {
                Thread.Sleep(200);
                var rc = _rawCapture;
                _rawCapture = null;
                try { rc?.Release(); } catch { }
                try { rc?.Dispose(); } catch { }
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
                SetPill("STOPPED", Color.FromRgb(180, 40, 40));
                BlobCountLabel.Text = "--";
                MarkerCountLabel.Text = "--";
            });
        });
    }

    // ── Stage 0 — exact working diagnostic code ────────────────────────────

    private bool OpenStage0(int index)
    {
        _rawCapture = new VideoCapture(index, VideoCaptureAPIs.MSMF);
        if (!_rawCapture.IsOpened()) { _rawCapture.Dispose(); _rawCapture = null; return false; }
        _rawCapture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M','J','P','G'));
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
            double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
            _frameCount = 0;
            _fpsStopwatch.Restart();
            var bmp = MatToBitmapSource(frame); bmp.Freeze();
            long tot = _totalFrames;
            Dispatcher.InvokeAsync(() => {
                PreviewImage.Source = bmp;
                FpsLabel.Text = $"{fps:F0}";
                FrameCountLabel.Text = $"{tot}";
            });
        }
    }

    // ── Stages 1-5 — via CameraCapture ────────────────────────────────────

    private bool OpenStages1to5(int index)
    {
        // Stage 1: CameraCapture using Read() internally
        // Stage 2+: CameraCapture using Grab()+Retrieve() internally
        bool useGrabRetrieve = _activeStage >= 2;
        _camCapture = new CameraCapture(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraCapture>.Instance,
            _settings,
            useGrabRetrieve);

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
            int markerCount = 0;
            Mat displayFrame = frame;
            bool disposeDisplay = false;

            if (_activeStage >= 3)
            {
                var blobs = _detector.Detect(frame);
                blobCount = blobs.Count;
                markerCount = _detector.ConfirmedMarkerCount;

                if (_activeStage >= 4)
                {
                    displayFrame = _detector.DrawDebug(frame, blobs);
                    disposeDisplay = true;
                }

                if (_activeStage >= 5 && !_detector.IsIdentifying)
                {
                    var groups  = _grouper.Group(blobs);
                    var gesture = _gestureRecognizer.Process(groups);
                    _mouseController.Apply(gesture);
                }
            }

            if (_fpsStopwatch.ElapsedMilliseconds < 100)
            {
                if (disposeDisplay) displayFrame.Dispose();
                return;
            }

            double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
            _frameCount = 0;
            _fpsStopwatch.Restart();

            var bmp = MatToBitmapSource(displayFrame); bmp.Freeze();
            if (disposeDisplay) displayFrame.Dispose();
            long tot = _totalFrames;
            int bc = blobCount, mc = markerCount;

            Dispatcher.InvokeAsync(() => {
                PreviewImage.Source = bmp;
                FpsLabel.Text = $"{fps:F0}";
                FrameCountLabel.Text = $"{tot}";
                BlobCountLabel.Text = bc > 0 ? $"{bc}" : "--";
                MarkerCountLabel.Text = mc > 0 ? $"{mc}" : "--";
            });
        }
    }

    private void SetPill(string text, Color color)
    {
        StatusPill.Background = new SolidColorBrush(color);
        StatusPillLabel.Text = text;
    }

    private static BitmapSource MatToBitmapSource(Mat mat)
    {
        Mat bgr = mat; bool nd = false;
        if (mat.Channels() == 1)
        {
            bgr = new Mat();
            Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
            nd = true;
        }
        int w = bgr.Width, h = bgr.Height, stride = (int)bgr.Step();
        var px = new byte[stride * h];
        Marshal.Copy(bgr.Data, px, 0, px.Length);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
        if (nd) bgr.Dispose();
        return bmp;
    }
}
