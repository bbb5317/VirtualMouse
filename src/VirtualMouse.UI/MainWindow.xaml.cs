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
    private TrackingSettings _settings;

    private CancellationTokenSource? _cts;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;
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

        SettingsPathLabel.Text = $"Settings: {SettingsService.GetSettingsPath()}";
        RefreshCameraList();
        ApplySettingsToUI();
    }

    // ── Camera ─────────────────────────────────────────────────────────────

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
        foreach (var d in devices) CameraComboBox.Items.Add(d);
        var match = devices.FirstOrDefault(d => d.Index == _settings.CameraDeviceIndex);
        CameraComboBox.SelectedItem = match ?? devices[0];
        StartButton.IsEnabled = true;
    }

    private void RefreshCameras_Click(object sender, RoutedEventArgs e) => RefreshCameraList();

    private void CameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is CameraDeviceInfo d && d.Index >= 0)
        {
            _settings.CameraDeviceIndex = d.Index;
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
            MessageBox.Show($"Failed to open \"{device.Name}\".\nCheck it is connected and not in use.",
                "Camera Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _detector.ResetIdentification();
        _cts = new CancellationTokenSource();
        _camera.FrameReady += OnFrameReady;
        _camera.StartCapture(_cts.Token);

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        CameraComboBox.IsEnabled = false;
        StatusLabel.Text = "Running";
        StatusLabel.Foreground = (Brush)FindResource("GreenBrush");
        IdentifyPill.Visibility = Visibility.Visible;
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
            UpdateActivationPill(false);
            IdentifyPill.Visibility = Visibility.Collapsed;
        });
    }

    // ── Frame Processing ───────────────────────────────────────────────────

    private void OnFrameReady(object? sender, Mat frame)
    {
        using (frame)
        {
            var blobs   = _detector.Detect(frame);
            var groups  = _grouper.Group(blobs);

            // Only inject mouse events once identification has settled
            if (!_detector.IsIdentifying)
            {
                var gesture = _gestureRecognizer.Process(groups);
                _mouseController.Apply(gesture);
            }

            _frameCount++;
            if (_fpsStopwatch.ElapsedMilliseconds >= 100)
            {
                double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
                _frameCount = 0;
                _fpsStopwatch.Restart();

                using var debugFrame = _detector.DrawDebug(frame, blobs);
                var bitmap = MatToBitmapSource(debugFrame);
                bitmap.Freeze();

                var groupDict    = groups.ToDictionary(g => g.Identity);
                double pinchDist = _gestureRecognizer.LastActivationDistance;
                bool mouseActive = _gestureRecognizer.IsMouseActive;
                bool identifying = _detector.IsIdentifying;
                int confirmed    = _detector.ConfirmedMarkerCount;
                int passes       = _detector.SlowPassCount;

                Dispatcher.InvokeAsync(() =>
                {
                    CameraPreviewImage.Source = bitmap;
                    FpsLabel.Text = $"{fps:F0}";
                    BlobCountLabel.Text = $"{blobs.Count}";
                    MarkerCountLabel.Text = $"{confirmed}";
                    PinchDistLabel.Text = pinchDist > 0 ? $"{pinchDist:F0}px" : "--";
                    CalibDistLabel.Text = pinchDist > 0 ? $"{pinchDist:F0} px" : "-- px";
                    UpdateActivationPill(mouseActive);
                    UpdateIdentifyPill(identifying, confirmed);
                    IdentifyStatusLabel.Text = $"Confirmed markers: {confirmed}";
                    IdentifyPassLabel.Text   = $"Identification passes: {passes}";
                    UpdateFingerStatus(groupDict);
                });
            }
        }
    }

    // ── Re-identify ────────────────────────────────────────────────────────

    private void ReIdentify_Click(object sender, RoutedEventArgs e)
    {
        _detector.ResetIdentification();
        _gestureRecognizer.Reset();
        IdentifyPill.Visibility = Visibility.Visible;
    }

    // ── Activation Calibration ─────────────────────────────────────────────

    private void CalibrateActivation_Click(object sender, RoutedEventArgs e)
    {
        double dist = _gestureRecognizer.LastActivationDistance;
        if (dist <= 0)
        {
            MessageBox.Show(
                "Left hand markers are not currently detected.\n\nStart tracking and position your left thumb and index finger before calibrating.",
                "Calibration Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _gestureRecognizer.CalibrateActivation();
        _settings.ActivationThresholdPixels = dist;
        _settingsService.Save(_settings);
        CalibThreshLabel.Text = $"{dist:F0} px";
        MessageBox.Show(
            $"Activation threshold set to {dist:F0} px.\n\nSpread your fingers beyond this distance to activate the mouse.",
            "Calibration Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Settings ───────────────────────────────────────────────────────────

    private void ApplySettingsToUI()
    {
        _loadingSettings = true;
        try
        {
            ThresholdSlider.Value         = _settings.BrightnessThreshold;
            SensitivitySlider.Value       = _settings.MouseSensitivity;
            HysteresisSlider.Value        = _settings.ActivationHysteresisPixels;
            IdentifyIntervalSlider.Value  = _settings.IdentifyInterval;
            MoveThreshSlider.Value        = _settings.IdentifyMovementThresholdPx;

            ThresholdLabel.Text        = $"{_settings.BrightnessThreshold}";
            SensitivityLabel.Text      = $"{_settings.MouseSensitivity:F1}";
            HysteresisLabel.Text       = $"{(int)_settings.ActivationHysteresisPixels}";
            IdentifyIntervalLabel.Text = $"{_settings.IdentifyInterval}";
            MoveThreshLabel.Text       = $"{_settings.IdentifyMovementThresholdPx:F0}";

            CalibThreshLabel.Text = _settings.ActivationThresholdPixels > 0
                ? $"{_settings.ActivationThresholdPixels:F0} px"
                : "Not set";
        }
        finally { _loadingSettings = false; }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Reset all settings to factory defaults?\n\nThis will clear your camera selection, calibration, and all slider values.",
                "Reset to Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _settingsService.Reset();
        _settings = new TrackingSettings();
        _detector.ResetIdentification();
        _gestureRecognizer.Reset();
        ApplySettingsToUI();
        RefreshCameraList();
        CalibThreshLabel.Text = "Not set";
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

    private void HysteresisSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null || _loadingSettings) return;
        _settings.ActivationHysteresisPixels = e.NewValue;
        if (HysteresisLabel != null) HysteresisLabel.Text = $"{(int)e.NewValue}";
        _settingsService.Save(_settings);
    }

    private void IdentifyIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null || _loadingSettings) return;
        _settings.IdentifyInterval = (int)e.NewValue;
        if (IdentifyIntervalLabel != null) IdentifyIntervalLabel.Text = $"{(int)e.NewValue}";
        _settingsService.Save(_settings);
    }

    private void MoveThreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings == null || _loadingSettings) return;
        _settings.IdentifyMovementThresholdPx = e.NewValue;
        if (MoveThreshLabel != null) MoveThreshLabel.Text = $"{e.NewValue:F0}";
        _settingsService.Save(_settings);
    }

    // ── UI Helpers ─────────────────────────────────────────────────────────

    private void UpdateActivationPill(bool active)
    {
        ActivationPill.Background = active
            ? (Brush)FindResource("GreenBrush")
            : (Brush)FindResource("RedBrush");
        ActivationLabel.Text = active ? "MOUSE ON" : "MOUSE OFF";
    }

    private void UpdateIdentifyPill(bool identifying, int confirmed)
    {
        if (identifying)
        {
            IdentifyPill.Background = new SolidColorBrush(Color.FromRgb(136, 85, 0));
            IdentifyLabel.Text = "Identifying...";
            IdentifyPill.Visibility = Visibility.Visible;
        }
        else
        {
            IdentifyPill.Background = (Brush)FindResource("GreenBrush");
            IdentifyLabel.Text = $"{confirmed} markers";
            IdentifyPill.Visibility = Visibility.Visible;
        }
    }

    private void UpdateFingerStatus(Dictionary<FingerIdentity, MarkerGroup> groups)
    {
        var green  = (Brush)FindResource("GreenBrush");
        var yellow = new SolidColorBrush(Color.FromRgb(255, 200, 0));
        var red    = (Brush)FindResource("RedBrush");
        SetFingerStatus(LeftThumbStatus,   groups, FingerIdentity.LeftThumb,   green, yellow, red);
        SetFingerStatus(LeftIndexStatus,   groups, FingerIdentity.LeftIndex,   green, yellow, red);
        SetFingerStatus(RightIndexStatus,  groups, FingerIdentity.RightIndex,  green, yellow, red);
        SetFingerStatus(RightMiddleStatus, groups, FingerIdentity.RightMiddle, green, yellow, red);
    }

    private static void SetFingerStatus(
        System.Windows.Controls.TextBlock label,
        Dictionary<FingerIdentity, MarkerGroup> groups,
        FingerIdentity identity,
        Brush green, Brush yellow, Brush red)
    {
        if (groups.TryGetValue(identity, out var group))
        {
            int v = group.VisibleCount, m = MarkerGroup.MaxMarkerCount(identity);
            label.Text       = $"{v}/{m}";
            label.Foreground = v >= m ? green : yellow;
        }
        else
        {
            label.Text       = "Lost";
            label.Foreground = red;
        }
    }

    private static BitmapSource MatToBitmapSource(Mat mat)
    {
        Mat bgr = mat; bool nd = false;
        if (mat.Channels() == 1) { bgr = new Mat(); Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR); nd = true; }
        int w = bgr.Width, h = bgr.Height, s = (int)bgr.Step();
        var px = new byte[s * h];
        Marshal.Copy(bgr.Data, px, 0, px.Length);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, s, 0);
        if (nd) bgr.Dispose();
        return bmp;
    }
}
