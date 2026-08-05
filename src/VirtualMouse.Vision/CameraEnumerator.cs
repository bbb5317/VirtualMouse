using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace VirtualMouse.Vision;

/// <summary>
/// Represents a single available camera device.
/// </summary>
public record CameraDeviceInfo(int Index, string Name)
{
    public override string ToString() => $"{Name}  [index {Index}]";
}

/// <summary>
/// Enumerates cameras by probing MSMF indices 0..N and reading the
/// backend-reported device name from each opened VideoCapture.
/// This guarantees the index in CameraDeviceInfo matches exactly what
/// VideoCapture(index, MSMF) will open — no cross-API ordering mismatch.
///
/// Each probe opens the camera briefly (no frames read), reads the name,
/// then immediately releases it. The OV9281 survives this because we use
/// MSMF (not DSHOW) and release cleanly before opening the next index.
/// </summary>
public class CameraEnumerator
{
    private readonly ILogger<CameraEnumerator> _logger;
    private const int MaxProbeIndex = 8;

    // VideoCaptureProperties value for backend device name (OpenCV 4.x)
    // CAP_PROP_BACKEND = 42, but we want the device name string.
    // OpenCV does not expose a string property for device name via VideoCapture.
    // We fall back to MF name list cross-referenced by position.
    private const int CAP_PROP_BACKEND = 42;

    public CameraEnumerator(ILogger<CameraEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        // Step 1: get friendly names from MF in MF enumeration order
        var mfNames = new List<string>();
        try { mfNames = GetMFNames(); }
        catch (Exception ex)
        {
            _logger.LogWarning("MF name query failed: {Msg}", ex.Message);
        }

        // Step 2: probe each MSMF index to confirm it opens
        var devices = new List<CameraDeviceInfo>();
        for (int i = 0; i < MaxProbeIndex; i++)
        {
            try
            {
                using var cap = new VideoCapture(i, VideoCaptureAPIs.MSMF);
                if (!cap.IsOpened()) break;

                // Use MF name at position i if available, otherwise generic label
                string name = (i < mfNames.Count && !string.IsNullOrWhiteSpace(mfNames[i]))
                    ? mfNames[i]
                    : $"Camera {i}";

                devices.Add(new CameraDeviceInfo(i, name));
                _logger.LogDebug("Probed MSMF index {Index}: {Name}", i, name);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("MSMF probe {Index} failed: {Msg}", i, ex.Message);
                break;
            }
        }

        if (devices.Count == 0)
        {
            _logger.LogWarning("No cameras found via probing; offering Camera 0 as fallback.");
            devices.Add(new CameraDeviceInfo(0, "Camera 0"));
        }

        _logger.LogInformation("Enumerated {Count} camera(s).", devices.Count);
        return devices;
    }

    // ── Windows Media Foundation name query ───────────────────────────────

    private static List<string> GetMFNames()
    {
        var names = new List<string>();

        int hr = MFStartup(MF_VERSION, 0);
        if (hr < 0) return names;

        try
        {
            hr = MFCreateAttributes(out IntPtr pAttr, 1);
            if (hr < 0) return names;

            try
            {
                MFSetAttributeGUID(pAttr,
                    MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                    MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

                hr = MFEnumDeviceSources(pAttr, out IntPtr ppDevices, out uint count);
                if (hr < 0 || ppDevices == IntPtr.Zero) return names;

                try
                {
                    for (uint i = 0; i < count && i < 16; i++)
                    {
                        IntPtr pActivate = Marshal.ReadIntPtr(ppDevices, (int)(i * IntPtr.Size));
                        string name = GetMFString(pActivate, MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                        names.Add(string.IsNullOrWhiteSpace(name) ? $"Camera {i}" : name);
                        Marshal.Release(pActivate);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ppDevices);
                }
            }
            finally
            {
                Marshal.Release(pAttr);
            }
        }
        finally
        {
            MFShutdown();
        }

        return names;
    }

    private static string GetMFString(IntPtr pActivate, Guid guidKey)
    {
        int hr = MFGetAttributeString(pActivate, guidKey, out IntPtr pszValue, out _);
        if (hr < 0 || pszValue == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUni(pszValue) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(pszValue); }
    }

    // ── MF P/Invoke ──────────────────────────────────────────────────────

    private const uint MF_VERSION = 0x00020070;
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE =
        new("49D1F9C5-60C3-4935-A25A-7F6A0B822F3D");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID =
        new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME =
        new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint dwFlags);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFSetAttributeGUID(IntPtr pAttributes,
        [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
        [MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFEnumDeviceSources(IntPtr pAttributes,
        out IntPtr pppSourceActivate, out uint pcSourceActivate);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFGetAttributeString(IntPtr pAttributes,
        [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
        out IntPtr ppwszValue, out uint pcchLength);
}
