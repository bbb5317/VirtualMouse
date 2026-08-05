using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Represents a single available camera device.
/// </summary>
public record CameraDeviceInfo(int Index, string Name)
{
    public override string ToString() => $"{Name}  (index {Index})";
}

/// <summary>
/// Enumerates cameras using Windows Media Foundation (MFEnumDeviceSources).
/// The indices returned here match exactly the indices used by
/// VideoCapture(index, VideoCaptureAPIs.MSMF), so there is no mismatch.
///
/// Falls back to DirectShow ICreateDevEnum if MF enumeration fails,
/// with a warning that indices may not match.
/// </summary>
public class CameraEnumerator
{
    private readonly ILogger<CameraEnumerator> _logger;
    private const int MaxDevices = 16;

    public CameraEnumerator(ILogger<CameraEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        var devices = new List<CameraDeviceInfo>();

        try
        {
            var mfDevices = EnumerateMF();
            for (int i = 0; i < mfDevices.Count; i++)
            {
                devices.Add(new CameraDeviceInfo(i, mfDevices[i]));
                _logger.LogDebug("MF Camera {Index}: {Name}", i, mfDevices[i]);
            }
            _logger.LogInformation("MF enumerated {Count} camera(s).", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MF enumeration failed ({Msg}); falling back to DirectShow.", ex.Message);
            devices.Clear();

            try
            {
                var dsDevices = EnumerateDirectShow();
                for (int i = 0; i < dsDevices.Count; i++)
                    devices.Add(new CameraDeviceInfo(i, dsDevices[i]));
                _logger.LogInformation("DS enumerated {Count} camera(s).", devices.Count);
            }
            catch (Exception ex2)
            {
                _logger.LogWarning("DirectShow enumeration also failed ({Msg}).", ex2.Message);
            }
        }

        if (devices.Count == 0)
        {
            _logger.LogWarning("No cameras found; offering Camera 0 as fallback.");
            devices.Add(new CameraDeviceInfo(0, "Camera 0"));
        }

        return devices;
    }

    // ── Windows Media Foundation enumeration ──────────────────────────────

    private static List<string> EnumerateMF()
    {
        var names = new List<string>();

        // MFStartup
        int hr = MFStartup(MF_VERSION, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            // Create attribute store: MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = VideoCapture
            hr = MFCreateAttributes(out IntPtr pAttr, 1);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            try
            {
                hr = MFSetAttributeGUID(pAttr,
                    MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                    MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                hr = MFEnumDeviceSources(pAttr, out IntPtr ppDevices, out uint count);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                try
                {
                    for (uint i = 0; i < count && i < MaxDevices; i++)
                    {
                        // ppDevices is an array of IMFActivate* pointers
                        IntPtr pActivate = Marshal.ReadIntPtr(ppDevices, (int)(i * IntPtr.Size));
                        string name = GetMFString(pActivate,
                            MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                        names.Add(string.IsNullOrWhiteSpace(name) ? $"Camera {i}" : name);
                        Marshal.Release(pActivate);
                    }
                }
                finally
                {
                    if (ppDevices != IntPtr.Zero)
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
        // IMFAttributes::GetAllocatedString
        int hr = MFGetAttributeString(pActivate, guidKey,
            out IntPtr pszValue, out uint cchLength);
        if (hr < 0 || pszValue == IntPtr.Zero) return string.Empty;
        try
        {
            return Marshal.PtrToStringUni(pszValue) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pszValue);
        }
    }

    // ── DirectShow fallback ───────────────────────────────────────────────

    private static List<string> EnumerateDirectShow()
    {
        var names = new List<string>();

        var sysDevEnumType = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum)
            ?? throw new InvalidOperationException("CLSID_SystemDeviceEnum not found.");

        var sysDevEnum = (ICreateDevEnum)Activator.CreateInstance(sysDevEnumType)!;
        sysDevEnum.CreateClassEnumerator(
            ref CLSID_VideoInputDeviceCategory,
            out System.Runtime.InteropServices.ComTypes.IEnumMoniker enumMoniker, 0);

        if (enumMoniker == null) return names;

        var monikers = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
        while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
        {
            try
            {
                monikers[0].BindToStorage(null!, null!, ref IID_IPropertyBag, out object ppvObj);
                if (ppvObj is IPropertyBag bag)
                {
                    bag.Read("FriendlyName", out object val, null!);
                    if (val is string fn && !string.IsNullOrWhiteSpace(fn))
                    {
                        names.Add(fn);
                        continue;
                    }
                }
            }
            catch { /* skip bad moniker */ }
            names.Add($"Camera {names.Count}");
        }
        return names;
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

    // ── DirectShow COM GUIDs / interfaces ────────────────────────────────

    private static readonly Guid CLSID_SystemDeviceEnum =
        new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static Guid CLSID_VideoInputDeviceCategory =
        new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static Guid IID_IPropertyBag =
        new("55272A00-42CB-11CE-8135-00AA004BB851");

    [System.Runtime.InteropServices.ComImport,
     Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
     System.Runtime.InteropServices.InterfaceType(
         System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [System.Runtime.InteropServices.PreserveSig]
        int CreateClassEnumerator(
            [In] ref Guid clsidDeviceClass,
            [Out] out System.Runtime.InteropServices.ComTypes.IEnumMoniker ppEnumMoniker,
            [In] int dwFlags);
    }

    [System.Runtime.InteropServices.ComImport,
     Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
     System.Runtime.InteropServices.InterfaceType(
         System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [System.Runtime.InteropServices.PreserveSig]
        int Read(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [Out, MarshalAs(UnmanagedType.Struct)] out object pVar,
            [In] IErrorLog pErrorLog);

        [System.Runtime.InteropServices.PreserveSig]
        int Write(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object pVar);
    }

    [System.Runtime.InteropServices.ComImport,
     Guid("A9931136-05C0-11D3-A6F2-00A0C9255AC1"),
     System.Runtime.InteropServices.InterfaceType(
         System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IErrorLog
    {
        void AddError(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [In] System.Runtime.InteropServices.ComTypes.EXCEPINFO pExcepInfo);
    }
}
