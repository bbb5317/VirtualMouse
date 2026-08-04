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
/// Opens the camera and displays raw frames. No detection, no processing.
///
/// Teardown sequence (critical for OV9281 / MSMF):
///   1. Set _stopRequested = true  → CaptureLoop exits its while-loop naturally
///   2. Wait for _captureTask to complete (Read() has returned, loop has exited)
///   3. ONLY THEN call Release() + Dispose()
///   This guarantees no race between Read() and Release().
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly CameraEnumerator _cameraEnumerator;
    private readonly TrackingSettings _settings;

    private VideoCapture? _capture;
    private Task? _captureTask;
    private volatile bool _stopRequested;

    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;
    private long _totalFrames;

    public MainWindow(CameraEnumerator cameraEnumerator, TrackingSettings settings)
    {
        _cameraEnumerator = cameraEnumerator;
        _settings = settings;
        InitializeComponent();
        Closing += (_, _) => StopButton_Click(this, new RoutedEventArgs());
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
            MessageBox.Show("Select a camera first.", "No Camera",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _capture = new VideoCapture(device.Index, VideoCaptureAPIs.MSMF);
        if (!_capture.IsOpened())
        {
            MessageBox.Show($"Could not open camera index {device.Index}.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _capture.Dispose();
            _capture = null;
            return;
        }

        _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M','J','P','G'));
        _capture.Set(VideoCaptureProperties.FrameWidth,  _settings.FrameWidth);
        _capture.Set(VideoCaptureProperties.FrameHeight, _settings.FrameHeight);

        _stopRequested = false;
        _totalFrames   = 0;
        _captureTask   = Task.Run(CaptureLoop);

        StartButton.IsEnabled    = false;
        StopButton.IsEnabled     = true;
        CameraComboBox.IsEnabled = false;
        StatusLabel.Text         = "Running";
        StatusLabel.Foreground   = new SolidColorBrush(Color.FromRgb(0, 204, 68));
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled   = false;
        StatusLabel.Text       = "Stopping...";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(200, 140, 0));

        // Capture references before clearing fields
        var cap  = _capture;
        var task = _captureTask;
        _capture     = null;
        _captureTask = null;

        Task.Run(() =>
        {
            // Step 1: signal the loop to stop
            _stopRequested = true;

            // Step 2: wait for CaptureLoop to fully exit
            // Read() will return false/empty once we set _stopRequested and
            // the loop checks the flag. We wait up to 2 seconds.
            task?.Wait(TimeSpan.FromSeconds(2));

            // Step 3: NOW it is safe to release — Read() has returned
            try { cap?.Release(); } catch { }
            try { cap?.Dispose(); } catch { }

            Dispatcher.InvokeAsync(() =>
            {
                StartButton.IsEnabled    = true;
                StopButton.IsEnabled     = false;
                CameraComboBox.IsEnabled = true;
                StatusLabel.Text         = "Stopped";
                StatusLabel.Foreground   = new SolidColorBrush(Color.FromRgb(204, 51, 51));
            });
        });
    }

    private void ResetUsb_Click(object sender, RoutedEventArgs e)
    {
        // Use pnputil to restart the camera device without unplugging.
        // This resets the USB port state that our app may have left dirty.
        StatusLabel.Text       = "Resetting USB...";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(200, 140, 0));

        Task.Run(() =>
        {
            try
            {
                // Find the hardware ID of the ArduCam / UVC device and restart it
                var psi = new ProcessStartInfo("pnputil",
                    "/restart-device \"USB\\VID_0C45*\" /subtree")
                {
                    UseShellExecute        = true,
                    Verb                   = "runas",   // requires admin
                    CreateNoWindow         = true,
                    WindowStyle            = ProcessWindowStyle.Hidden
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() =>
                    MessageBox.Show($"USB reset failed:\n{ex.Message}\n\nTry running as Administrator.",
                        "Reset Failed", MessageBoxButton.OK, MessageBoxImage.Warning));
            }

            Thread.Sleep(2000); // wait for re-enumeration

            Dispatcher.InvokeAsync(() =>
            {
                StatusLabel.Text       = "Stopped";
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(204, 51, 51));
                RefreshCameraList();
            });
        });
    }

    private void CaptureLoop()
    {
        using var frame = new Mat();

        while (!_stopRequested)
        {
            var cap = _capture;
            if (cap == null || !cap.IsOpened()) break;

            if (!cap.Read(frame) || frame.Empty())
            {
                Thread.Sleep(10);
                continue;
            }

            _totalFrames++;
            _frameCount++;

            if (_fpsStopwatch.ElapsedMilliseconds < 100) continue;

            double fps = _frameCount / _fpsStopwatch.Elapsed.TotalSeconds;
            _frameCount = 0;
            _fpsStopwatch.Restart();

            var bitmap = MatToBitmapSource(frame);
            bitmap.Freeze();
            long total = _totalFrames;

            Dispatcher.InvokeAsync(() =>
            {
                PreviewImage.Source    = bitmap;
                FpsLabel.Text          = $"{fps:F0}";
                FrameCountLabel.Text   = $"{total}";
            });
        }
    }

    private static BitmapSource MatToBitmapSource(Mat mat)
    {
        Mat bgr    = mat;
        bool nd    = false;
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
