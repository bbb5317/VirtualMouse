using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Core.Models;
using VirtualMouse.Input;
using VirtualMouse.Vision;

namespace VirtualMouse.UI;

public partial class MainWindow : System.Windows.Window
{
    private readonly CameraCapture _camera;
    private readonly MarkerDetector _detector;
    private readonly MarkerGrouper _grouper;
    private readonly GestureRecognizer _gestureRecognizer;
    private readonly WindowsMouseController _mouseController;
    private readonly TrackingSettings _settings;

    private CancellationTokenSource? _cts;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;

    public MainWindow(
        CameraCapture camera,
        MarkerDetector detector,
        MarkerGrouper grouper,
        GestureRecognizer gestureRecognizer,
        WindowsMouseController mouseController,
        TrackingSettings settings)
    {
        InitializeComponent();
        _camera = camera;
        _detector = detector;
        _grouper = grouper;
        _gestureRecognizer = gestureRecognizer;
        _mouseController = mouseController;
        _settings = settings;

        Closing += (_, _) => StopTracking();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(CameraIndexBox.Text, out int idx))
            _settings.CameraDeviceIndex = idx;

        if (!_camera.Open())
        {
            MessageBox.Show("Failed to open camera. Check device index and connection.", "Camera Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _cts = new CancellationTokenSource();
        _camera.FrameReady += OnFrameReady;
        _camera.StartCapture(_cts.Token);

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusLabel.Text = "Running";
        StatusLabel.Foreground = (Brush)FindResource("GreenBrush");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopTracking();

    private void StopTracking()
    {
        _cts?.Cancel();
        _camera.FrameReady -= OnFrameReady;
        _camera.Stop();
        _mouseController.ReleaseAll();
        _gestureRecognizer.Reset();

        Dispatcher.InvokeAsync(() =>
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusLabel.Text = "Stopped";
            StatusLabel.Foreground = (Brush)FindResource("RedBrush");
        });
    }

    private void OnFrameReady(object? sender, Mat frame)
    {
        using (frame)
        {
            // --- Vision Pipeline ---
            var blobs   = _detector.Detect(frame);
            var groups  = _grouper.Group(blobs);
            var gesture = _gestureRecognizer.Process(groups);

            // --- Input Injection ---
            _mouseController.Apply(gesture);

            // --- UI Update (throttled to ~10 updates/sec to avoid overwhelming WPF) ---
            _frameCount++;
            if (_fpsStopwatch.ElapsedMilliseconds >= 100)
            {
                double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
                _frameCount = 0;
                _fpsStopwatch.Restart();

                using var debugFrame = _detector.DrawDebug(frame, blobs);
                var bitmap = MatToBitmapSource(debugFrame);
                bitmap.Freeze();

                var groupDict = groups.ToDictionary(g => g.Identity);

                Dispatcher.InvokeAsync(() =>
                {
                    CameraPreviewImage.Source = bitmap;
                    FpsLabel.Text = $"{fps:F0}";
                    BlobCountLabel.Text = $"{blobs.Count}";
                    UpdateFingerStatus(groupDict);
                });
            }
        }
    }

    /// <summary>
    /// Converts an OpenCV BGR Mat to a WPF-compatible BitmapSource without using
    /// OpenCvSharp.Extensions.BitmapSourceConverter (which is not present in net8.0).
    /// Uses WriteableBitmap with direct pixel copy for maximum performance.
    /// </summary>
    private static BitmapSource MatToBitmapSource(Mat mat)
    {
        // Ensure the frame is BGR (3-channel) for WPF Bgr24 pixel format
        Mat bgr = mat;
        bool needDispose = false;
        if (mat.Channels() == 1)
        {
            bgr = new Mat();
            Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
            needDispose = true;
        }

        int width  = bgr.Width;
        int height = bgr.Height;
        int stride = (int)bgr.Step();

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        bitmap.Lock();
        try
        {
            // Copy pixel data directly from the Mat's unmanaged buffer to the WriteableBitmap
            unsafe
            {
                Buffer.MemoryCopy(
                    bgr.DataPointer.ToPointer(),
                    bitmap.BackBuffer.ToPointer(),
                    (long)bitmap.BackBufferStride * height,
                    (long)stride * height);
            }
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.Unlock();
            if (needDispose) bgr.Dispose();
        }

        return bitmap;
    }

    private void UpdateFingerStatus(Dictionary<FingerIdentity, MarkerGroup> groups)
    {
        var green = (Brush)FindResource("GreenBrush");
        var red   = (Brush)FindResource("RedBrush");

        LeftThumbStatus.Text       = groups.ContainsKey(FingerIdentity.LeftThumb)    ? "OK" : "Lost";
        LeftThumbStatus.Foreground = groups.ContainsKey(FingerIdentity.LeftThumb)    ? green : red;

        LeftIndexStatus.Text       = groups.ContainsKey(FingerIdentity.LeftIndex)    ? "OK" : "Lost";
        LeftIndexStatus.Foreground = groups.ContainsKey(FingerIdentity.LeftIndex)    ? green : red;

        RightIndexStatus.Text       = groups.ContainsKey(FingerIdentity.RightIndex)  ? "OK" : "Lost";
        RightIndexStatus.Foreground = groups.ContainsKey(FingerIdentity.RightIndex)  ? green : red;

        RightMiddleStatus.Text       = groups.ContainsKey(FingerIdentity.RightMiddle) ? "OK" : "Lost";
        RightMiddleStatus.Foreground = groups.ContainsKey(FingerIdentity.RightMiddle) ? green : red;
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.BrightnessThreshold = (int)e.NewValue;
        if (ThresholdLabel != null) ThresholdLabel.Text = $"{(int)e.NewValue}";
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.MouseSensitivity = e.NewValue;
        if (SensitivityLabel != null) SensitivityLabel.Text = $"{e.NewValue:F1}";
    }

    private void PinchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.PinchThresholdPixels = e.NewValue;
        if (PinchLabel != null) PinchLabel.Text = $"{(int)e.NewValue}";
    }
}
