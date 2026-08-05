using System.Runtime.InteropServices;
using System.Text;
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
/// Enumerates cameras by:
///   1. Querying friendly names via Windows Media Foundation COM interfaces
///      (IMFAttributes::GetAllocatedString on each IMFActivate object).
///   2. Probing each MSMF VideoCapture index to confirm it opens.
///   3. Assigning MF name[i] to MSMF index i (same enumeration order).
/// </summary>
public class CameraEnumerator
{
    private readonly ILogger<CameraEnumerator> _logger;
    private const int MaxProbeIndex = 8;

    public CameraEnumerator(ILogger<CameraEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        // Step 1: get friendly names from MF
        var mfNames = new List<string>();
        try
        {
            mfNames = GetMFNames();
            _logger.LogDebug("MF returned {Count} names: {Names}",
                mfNames.Count, string.Join(", ", mfNames));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MF name query failed: {Msg}", ex.Message);
        }

        // Step 2: probe each MSMF index
        var devices = new List<CameraDeviceInfo>();
        for (int i = 0; i < MaxProbeIndex; i++)
        {
            try
            {
                using var cap = new VideoCapture(i, VideoCaptureAPIs.MSMF);
                if (!cap.IsOpened()) break;

                string name = (i < mfNames.Count && !string.IsNullOrWhiteSpace(mfNames[i]))
                    ? mfNames[i]
                    : $"Camera {i}";

                devices.Add(new CameraDeviceInfo(i, name));
                _logger.LogDebug("MSMF index {Index} → {Name}", i, name);
            }
            catch
            {
                break;
            }
        }

        if (devices.Count == 0)
            devices.Add(new CameraDeviceInfo(0, "Camera 0"));

        _logger.LogInformation("Enumerated {Count} camera(s).", devices.Count);
        return devices;
    }

    // ── MF name enumeration via COM ───────────────────────────────────────

    private static List<string> GetMFNames()
    {
        var names = new List<string>();

        // MFStartup
        int hr = MFStartup(MF_VERSION, 0);
        if (hr < 0) return names;

        try
        {
            // Create attribute store
            hr = MFCreateAttributes(out IntPtr pAttrRaw, 1);
            if (hr < 0) return names;

            var pAttr = (IMFAttributes)Marshal.GetObjectForIUnknown(pAttrRaw);
            Marshal.Release(pAttrRaw);

            try
            {
                // Set MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = VideoCapture
                pAttr.SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                              MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

                // Enumerate devices
                hr = MFEnumDeviceSources(
                    Marshal.GetIUnknownForObject(pAttr),
                    out IntPtr ppDevices,
                    out uint count);

                if (hr < 0 || ppDevices == IntPtr.Zero) return names;

                try
                {
                    for (uint i = 0; i < count && i < 16; i++)
                    {
                        IntPtr pActivateRaw = Marshal.ReadIntPtr(ppDevices,
                            (int)(i * IntPtr.Size));
                        try
                        {
                            var activate = (IMFAttributes)
                                Marshal.GetObjectForIUnknown(pActivateRaw);
                            int ghr = activate.GetAllocatedString(
                                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                                out string name, out _);
                            if (ghr < 0) name = string.Empty;
                            names.Add(string.IsNullOrWhiteSpace(name)
                                ? $"Camera {i}" : name);
                        }
                        catch
                        {
                            names.Add($"Camera {i}");
                        }
                        finally
                        {
                            Marshal.Release(pActivateRaw);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ppDevices);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(pAttr);
            }
        }
        finally
        {
            MFShutdown();
        }

        return names;
    }

    // ── MF COM interface ──────────────────────────────────────────────────

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        // GetItem
        [PreserveSig] int GetItem(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            IntPtr pValue);
        // GetItemType
        [PreserveSig] int GetItemType(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out int pType);
        // CompareItem
        [PreserveSig] int CompareItem(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            IntPtr value, out bool pbResult);
        // Compare
        [PreserveSig] int Compare(IMFAttributes pTheirs,
            int matchType, out bool pbResult);
        // GetUINT32
        [PreserveSig] int GetUINT32(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out uint punValue);
        // GetUINT64
        [PreserveSig] int GetUINT64(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out ulong punValue);
        // GetDouble
        [PreserveSig] int GetDouble(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out double pfValue);
        // GetGUID
        [PreserveSig] int GetGUID(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out Guid pguidValue);
        // GetStringLength
        [PreserveSig] int GetStringLength(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out uint pcchLength);
        // GetString
        [PreserveSig] int GetString(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue,
            uint cchBufSize, out uint pcchLength);
        // GetAllocatedString
        [PreserveSig] int GetAllocatedString(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [Out, MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue,
            out uint pcchLength);
        // GetBlobSize — skip
        [PreserveSig] int GetBlobSize(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out uint pcbBlobSize);
        // GetBlob
        [PreserveSig] int GetBlob(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        // GetAllocatedBlob
        [PreserveSig] int GetAllocatedBlob(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            out IntPtr ppBuf, out uint pcbSize);
        // GetUnknown
        [PreserveSig] int GetUnknown(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IntPtr ppv);
        // SetItem
        [PreserveSig] int SetItem(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            IntPtr value);
        // DeleteItem
        [PreserveSig] int DeleteItem(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
        // DeleteAllItems
        [PreserveSig] int DeleteAllItems();
        // SetUINT32
        [PreserveSig] int SetUINT32(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            uint unValue);
        // SetUINT64
        [PreserveSig] int SetUINT64(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            ulong unValue);
        // SetDouble
        [PreserveSig] int SetDouble(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            double fValue);
        // SetGUID
        [PreserveSig] int SetGUID(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
        // SetString
        [PreserveSig] int SetString(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        // SetBlob
        [PreserveSig] int SetBlob(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [In] byte[] pBuf, uint cbBufSize);
        // SetUnknown
        [PreserveSig] int SetUnknown(
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [MarshalAs(UnmanagedType.Interface)] object pUnknown);
        // LockStore / UnlockStore / GetCount / GetItemByIndex / CopyAllItems
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex,
            out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    }



    // ── MF flat P/Invoke ──────────────────────────────────────────────────

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

    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFEnumDeviceSources(
        IntPtr pAttributes,
        out IntPtr pppSourceActivate,
        out uint pcSourceActivate);
}
