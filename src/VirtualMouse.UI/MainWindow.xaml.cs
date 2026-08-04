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
    private readonly CameraEnumerator _cameraEnumerator;
    private readonly SettingsService _settingsService;

    // Settings is loaded from disk at startup and saved on every change
    private TrackingSettings _settings;

    private CancellationTokenSource? _cts;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;

    // Suppress slider events while we are programmatically loading settings
    private bool _loadingSettings;

    public MainWindow(
        CameraCapture camera,
        MarkerDetector detector,
        MarkerGrouper grouper,
        GestureRecognizer gestureRecognizer,
        WindowsMouseController mouseController,
        CameraEnumerator cameraEnumerator,
        SettingsService settingsService,
        TrackingSettings settings)
    {
        _camera = camera;
        _detector = detector;
        _grouper = grouper;
        _gestureRecognizer = gestureRecognizer;
        _mouseController = mouseController;
        _cameraEnumerator = cameraEnumerator;
        _settingsService = settingsService;
        _settings = settings;

        InitializeComponent();

        Closing += (_, _) => StopTracking();

        // Show where settings are persisted
        SettingsPathLabel.Text = $"Settings: {SettingsService.GetSettingsPath()}";

        // Populate camera list and restore all saved values
        RefreshCameraList();
        ApplySettingsToUI();
    }

    // ── Camera Enumeration ─────────────────────────────────────────────────

    private void RefreshCameraList()
    {
        CameraComboBox.Items.Clear();

        var devices = _cameraEnumerator.Enumerate();
        if (devices.Count == 0)
        {
            CameraComboBox.Items.Add(new CameraDeviceInfo(-1, "No cameras found"));
            CameraComboBox.SelectedIndex = 0;
            StartButton.IsEnabled = false;
            return;
        }

        foreach (var device in devices)
            CameraComboBox.Items.Add(device);

        // Restore previously selected camera, fall back to first available
        var savedIndex = _settings.CameraDeviceIndex;
        var match = devices.FirstOrDefault(d => d.Index == savedIndex);
        CameraComboBox.SelectedItem = match ?? devices[0];
        StartButton.IsEnabled = true;
    }

    private void RefreshCameras_Click(object sender, RoutedEventArgs e)
    {
        RefreshCameraList();
    }

    private void CameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is CameraDeviceInfo device && device.Index >= 0)
        {
            _settings.CameraDeviceIndex = device.Index;
            _settingsService.Save(_settings);
        }
    }

    // ── Start / Stop ───────────────────────────────────────────────────────

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is not CameraDeviceInfo device || device.Index < 0)
        {
            MessageBox.Show("Please select a valid camera device.", "No Camera Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.CameraDeviceIndex = device.Index;

        if (!_camera.Open())
        {
            MessageBox.Show(
                $"Failed to open camera \"{device.Name}\".\n\nCheck that it is connected and not in use by another application.",
                "Camera Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _cts = new CancellationTokenSource();
        _camera.FrameReady += OnFrameReady;
        _camera.StartCapture(_cts.Token);

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        CameraComboBox.IsEnabled = false;
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
            CameraComboBox.IsEnabled = true;
            StatusLabel.Text = "Stopped";
            StatusLabel.Foreground = (Brush)FindResource("RedBrush");
        });
    }

    // ── Frame Processing ───────────────────────────────────────────────────

    private void OnFrameReady(object? sender, Mat frame)
    {
        using (frame)
        {
            var blobs   = _detector.Detect(frame);
            var groups  = _grouper.Group(blobs);
            var gesture = _gestureRecognizer.Process(groups);

            _mouseController.Apply(gesture);

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

    // ── Settings Persistence ───────────────────────────────────────────────

    /// <summary>
    /// Pushes the current _settings values into all UI controls without triggering saves.
    /// </summary>
    private void ApplySettingsToUI()
    {
        _loadingSettings = true;
        try
        {
            ThresholdSlider.Value  = _settings.BrightnessThreshold;
            SensitivitySlider.Value = _settings.MouseSensitivity;
            PinchSlider.Value      = _settings.PinchThresholdPixels;

            ThresholdLabel.Text   = $"{_settings.BrightnessThreshold}";
            SensitivityLabel.Text = $"{_settings.MouseSensitivity:F1}";
            PinchLabel.Text       = $"{(int)_settings.PinchThresholdPixels}";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all settings to factory defaults?\n\nThis will clear your camera selection and all calibration values.",
            "Reset to Defaults",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _settingsService.Reset();
        _settings = _settingsService.Load(); // returns a fresh default instance

        // Propagate new defaults into the injected singletons
        _settings.CameraDeviceIndex     = new TrackingSettings().CameraDeviceIndex;
        _settings.BrightnessThreshold   = new TrackingSettings().BrightnessThreshold;
        _settings.MouseSensitivity      = new TrackingSettings().MouseSensitivity;
        _settings.PinchThresholdPixels  = new TrackingSettings().PinchThresholdPixels;

        ApplySettingsToUI();
        RefreshCameraList();
    }

    // ── Slider Handlers ────────────────────────────────────────────────────

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null || _loadingSettings) return;
        _settings.BrightnessThreshold = (int)e.NewValue;
        if (ThresholdLabel != null) ThresholdLabel.Text = $"{(int)e.NewValue}";
        _settingsService.Save(_settings);
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null || _loadingSettings) return;
        _settings.MouseSensitivity = e.NewValue;
        if (SensitivityLabel != null) SensitivityLabel.Text = $"{e.NewValue:F1}";
        _settingsService.Save(_settings);
    }

    private void PinchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null || _loadingSettings) return;
        _settings.PinchThresholdPixels = e.NewValue;
        if (PinchLabel != null) PinchLabel.Text = $"{(int)e.NewValue}";
        _settingsService.Save(_settings);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static BitmapSource MatToBitmapSource(Mat mat)
    {
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

        var pixelData = new byte[stride * height];
        Marshal.Copy(bgr.Data, pixelData, 0, pixelData.Length);

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixelData, stride, 0);

        if (needDispose) bgr.Dispose();
        return bitmap;
    }

    private void UpdateFingerStatus(Dictionary<FingerIdentity, MarkerGroup> groups)
    {
        var green  = (Brush)FindResource("GreenBrush");
        var yellow = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 0));
        var red    = (Brush)FindResource("RedBrush");

        SetFingerStatus(LeftThumbStatus,   groups, FingerIdentity.LeftThumb,   green, yellow, red);
        SetFingerStatus(LeftIndexStatus,   groups, FingerIdentity.LeftIndex,   green, yellow, red);
        SetFingerStatus(RightIndexStatus,  groups, FingerIdentity.RightIndex,  green, yellow, red);
        SetFingerStatus(RightMiddleStatus, groups, FingerIdentity.RightMiddle, green, yellow, red);
    }

    /// <summary>
    /// Shows "n/max" when the finger is tracked (green = full, yellow = partial),
    /// or "Lost" in red when no markers are visible for that finger.
    /// </summary>
    private static void SetFingerStatus(
        System.Windows.Controls.TextBlock label,
        Dictionary<FingerIdentity, MarkerGroup> groups,
        FingerIdentity identity,
        Brush green, Brush yellow, Brush red)
    {
        if (groups.TryGetValue(identity, out var group))
        {
            int visible = group.VisibleCount;
            int max     = MarkerGroup.MaxMarkerCount(identity);
            label.Text       = $"{visible}/{max}";
            label.Foreground = visible >= max ? green : yellow;
        }
        else
        {
            label.Text       = "Lost";
            label.Foreground = red;
        }
    }
}
