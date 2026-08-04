using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace VirtualMouse.Vision;

/// <summary>
/// Represents a single available camera device on the system.
/// </summary>
public record CameraDeviceInfo(int Index, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates available camera devices by querying the Windows DirectShow
/// device enumeration API (ICreateDevEnum / CLSID_VideoInputDeviceCategory)
/// to retrieve the real friendly name of each camera, then cross-references
/// with OpenCV to confirm the device is actually openable.
/// </summary>
public class CameraEnumerator
{
    private readonly ILogger<CameraEnumerator> _logger;
    private const int MaxProbeIndex = 10;

    public CameraEnumerator(ILogger<CameraEnumerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns all available camera devices with their real friendly names.
    /// Falls back to "Camera N" labels if the DirectShow query fails.
    /// </summary>
    public IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        // First try to get real names from DirectShow
        var names = TryGetDirectShowNames();

        var devices = new List<CameraDeviceInfo>();

        if (names.Count > 0)
        {
            // We have real names — verify each is openable via OpenCV
            for (int i = 0; i < names.Count && i < MaxProbeIndex; i++)
            {
                try
                {
                    using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                    if (cap.IsOpened())
                    {
                        devices.Add(new CameraDeviceInfo(i, names[i]));
                        _logger.LogDebug("Camera {Index}: {Name}", i, names[i]);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Index {Index} open check failed: {Msg}", i, ex.Message);
                }
            }
        }
        else
        {
            // Fallback: probe indices and use generic labels
            _logger.LogWarning("DirectShow name query failed; falling back to index probing.");
            for (int i = 0; i < MaxProbeIndex; i++)
            {
                try
                {
                    using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                    if (cap.IsOpened())
                        devices.Add(new CameraDeviceInfo(i, $"Camera {i} (Index {i})"));
                }
                catch { /* skip */ }
            }
        }

        _logger.LogInformation("Enumerated {Count} camera(s).", devices.Count);
        return devices;
    }

    // ── DirectShow device name query ───────────────────────────────────────

    private List<string> TryGetDirectShowNames()
    {
        var names = new List<string>();
        try
        {
            // CoCreateInstance(CLSID_SystemDeviceEnum)
            var sysDevEnumType = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum)
                ?? throw new InvalidOperationException("CLSID_SystemDeviceEnum not found.");

            var sysDevEnum = (ICreateDevEnum)Activator.CreateInstance(sysDevEnumType)!;

            sysDevEnum.CreateClassEnumerator(
                ref CLSID_VideoInputDeviceCategory,
                out IEnumMoniker enumMoniker,
                0);

            if (enumMoniker == null) return names;

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                monikers[0].GetDisplayName(null!, null!, out string displayName);

                // Get the friendly name from the property bag
                monikers[0].BindToStorage(null!, null!, ref IID_IPropertyBag, out object ppvObj);
                if (ppvObj is IPropertyBag bag)
                {
                    bag.Read("FriendlyName", out object val, null!);
                    if (val is string friendlyName && !string.IsNullOrWhiteSpace(friendlyName))
                    {
                        names.Add(friendlyName);
                        continue;
                    }
                }

                // Fallback to display name if FriendlyName not available
                names.Add(displayName ?? $"Camera {names.Count}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("DirectShow enumeration exception: {Msg}", ex.Message);
        }
        return names;
    }

    // ── COM GUIDs ──────────────────────────────────────────────────────────

    private static readonly Guid CLSID_SystemDeviceEnum =
        new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");

    private static Guid CLSID_VideoInputDeviceCategory =
        new("860BB310-5D01-11D0-BD3B-00A0C911CE86");

    private static Guid IID_IPropertyBag =
        new("55272A00-42CB-11CE-8135-00AA004BB851");

    // ── COM Interfaces ─────────────────────────────────────────────────────

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(
            [In] ref Guid clsidDeviceClass,
            [Out] out IEnumMoniker ppEnumMoniker,
            [In] int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [Out, MarshalAs(UnmanagedType.Struct)] out object pVar,
            [In] IErrorLog pErrorLog);

        [PreserveSig]
        int Write(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object pVar);
    }

    [ComImport, Guid("A9931136-05C0-11D3-A6F2-00A0C9255AC1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IErrorLog
    {
        void AddError(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [In] System.Runtime.InteropServices.ComTypes.EXCEPINFO pExcepInfo);
    }
}
