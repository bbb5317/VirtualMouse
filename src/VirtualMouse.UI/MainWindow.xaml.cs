using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Vision;

namespace VirtualMouse.UI;

/// <summary>
/// DIAGNOSTIC MODE — pure camera viewer.
/// Opens the camera and displays raw frames. No detection, no processing,
/// no gesture recognition, no mouse injection. Nothing.
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly CameraEnumerator _cameraEnumerator;
    private readonly TrackingSettings _settings;

    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;
    private long _totalFrames;

    public MainWindow(CameraEnumerator cameraEnumerator, TrackingSettings settings)
    {
        _cameraEnumerator = cameraEnumerator;
        _settings = settings;
        InitializeComponent();
        Closing += (_, _) => StopCamera();
        RefreshCameraList();
    }

    private void RefreshCameraList()
    {
        CameraComboBox.Items.Clear();
        var devices = _cameraEnumerator.Enumerate();
        if (devices.Count == 0)
        {
            CameraComboBox.Items.Add("No cameras found");
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

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is not CameraDeviceInfo device || device.Index < 0)
        {
            MessageBox.Show("Select a camera first.", "No Camera", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Open the camera — resolution only, nothing else
        // ArduCam documentation explicitly recommends CAP_MSMF (Windows Media Foundation)
        // over CAP_DSHOW for this camera. DSHOW causes low FPS and black frames.
        _capture = new VideoCapture(device.Index, VideoCaptureAPIs.MSMF);
        if (!_capture.IsOpened())
        {
            MessageBox.Show($"Could not open camera index {device.Index}.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _capture.Dispose();
            _capture = null;
            return;
        }

        // Request MJPEG format — the OV9281 natively outputs MJPEG and YUV2.
        // Without this, OpenCV's DirectShow backend requests BGR24 by default,
        // which the camera cannot deliver. The failed format negotiation leaves
        // the camera firmware in a stuck state that produces black frames until
        // the USB cable is physically unplugged.
        // Setting FourCC to MJPG forces DirectShow to negotiate MJPEG, which
        // the camera supports. OpenCV decodes it to BGR internally.
        _capture.Set(VideoCaptureProperties.FourCC,
            VideoWriter.FourCC('M', 'J', 'P', 'G'));
        _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
        _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);

        _totalFrames = 0;
        _cts = new CancellationTokenSource();
        Task.Run(() => CaptureLoop(_cts.Token));

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        CameraComboBox.IsEnabled = false;
        StatusLabel.Text = "Running";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0, 204, 68));
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopCamera();

    private void StopCamera()
    {
        _cts?.Cancel();
        _cts = null;

        // Give the capture loop a moment to exit before disposing
        Thread.Sleep(200);

        _capture?.Release();
        _capture?.Dispose();
        _capture = null;

        Dispatcher.InvokeAsync(() =>
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            CameraComboBox.IsEnabled = true;
            StatusLabel.Text = "Stopped";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(204, 51, 51));
        });
    }

    private void CaptureLoop(CancellationToken ct)
    {
        using var frame = new Mat();

        while (!ct.IsCancellationRequested)
        {
            if (_capture == null || !_capture.IsOpened()) break;

            if (!_capture.Read(frame) || frame.Empty())
            {
                Thread.Sleep(10);
                continue;
            }

            _totalFrames++;
            _frameCount++;

            // Update UI at ~10fps to avoid overwhelming the dispatcher
            if (_fpsStopwatch.ElapsedMilliseconds < 100) continue;

            double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
            _frameCount = 0;
            _fpsStopwatch.Restart();

            var bitmap = MatToBitmapSource(frame);
            bitmap.Freeze();
            long total = _totalFrames;

            Dispatcher.InvokeAsync(() =>
            {
                PreviewImage.Source = bitmap;
                FpsLabel.Text = $"{fps:F0}";
                FrameCountLabel.Text = $"{total}";
            });
        }
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
