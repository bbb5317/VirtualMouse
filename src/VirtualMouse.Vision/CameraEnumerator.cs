using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
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
/// Enumerates cameras by matching device IDs between the Registry and MSMF.
///
/// Strategy:
///   1. Read the Registry camera interface key to get (SymbolicLink → FriendlyName) pairs.
///   2. Use MFEnumDeviceSources to get the MSMF list of (SymbolicLink, position=MSMF index).
///   3. Match by SymbolicLink to assign the correct name to each MSMF index.
///
/// This is position-independent — it does not matter what order the two APIs
/// enumerate devices in, because we match on the unique device path string.
/// </summary>
public class CameraEnumerator
{
    private readonly ILogger<CameraEnumerator> _logger;

    // Windows camera device interface GUID (same as KSCATEGORY_VIDEO_CAMERA)
    private const string CameraInterfaceGuid = "{e5323777-f976-4f5b-9b55-b94699c46e44}";

    // MF attribute GUIDs
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE =
        new("49D1F9C5-60C3-4935-A25A-7F6A0B822F3D");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID =
        new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME =
        new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");
    private static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK =
        new("58D3F031-7D4C-4B1B-A6A2-099E3A3E3E6A");

    public CameraEnumerator(ILogger<CameraEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        // Step 1: Registry → Dictionary<symbolicLink (normalised), friendlyName>
        var regMap = GetRegistryMap();
        _logger.LogDebug("Registry map: {Map}",
            string.Join("; ", regMap.Select(kv => $"{kv.Key}={kv.Value}")));

        // Step 2: MSMF → List<(mfSymLink, mfFriendlyName)> in MSMF enumeration order
        //         The position in this list IS the MSMF VideoCapture index.
        var mfList = GetMFList();
        _logger.LogDebug("MF list ({Count}): {List}", mfList.Count,
            string.Join("; ", mfList.Select((t, i) => $"{i}:{t.Name}|{t.SymLink}")));

        var devices = new List<CameraDeviceInfo>();

        for (int mfIndex = 0; mfIndex < mfList.Count; mfIndex++)
        {
            var (mfName, mfSymLink) = mfList[mfIndex];

            // Try to find a better name from the Registry by matching symbolic link
            string name = mfName; // start with MF's own name

            if (!string.IsNullOrEmpty(mfSymLink))
            {
                // Normalise: lower-case, strip leading \\?\
                string normMf = NormaliseSymLink(mfSymLink);
                foreach (var kv in regMap)
                {
                    if (normMf.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Contains(normMf, StringComparison.OrdinalIgnoreCase))
                    {
                        name = kv.Value;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(name))
                name = $"Camera {mfIndex}";

            devices.Add(new CameraDeviceInfo(mfIndex, name));
            _logger.LogInformation("Camera index {Index}: {Name}", mfIndex, name);
        }

        if (devices.Count == 0)
            devices.Add(new CameraDeviceInfo(0, "Camera 0"));

        return devices;
    }

    // ── Registry: symbolic link → friendly name ───────────────────────────

    private Dictionary<string, string> GetRegistryMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string keyPath = $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{CameraInterfaceGuid}";
            using var classKey = Registry.LocalMachine.OpenSubKey(keyPath);
            if (classKey == null) return map;

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                try
                {
                    using var deviceKey = classKey.OpenSubKey(subKeyName);
                    if (deviceKey == null) continue;

                    // subKeyName IS the symbolic link (e.g. ##?#USB#VID_0C45&PID_6366#...)
                    string symLink = NormaliseSymLink(subKeyName);

                    // Find friendly name in sub-keys
                    string? friendlyName = null;
                    foreach (string sub in deviceKey.GetSubKeyNames())
                    {
                        using var sub2 = deviceKey.OpenSubKey(sub);
                        if (sub2 == null) continue;

                        friendlyName = sub2.GetValue("FriendlyName") as string
                                    ?? sub2.GetValue("DeviceDesc") as string;
                        if (!string.IsNullOrWhiteSpace(friendlyName)) break;

                        using var dp = sub2.OpenSubKey("Device Parameters");
                        if (dp != null)
                        {
                            friendlyName = dp.GetValue("FriendlyName") as string;
                            if (!string.IsNullOrWhiteSpace(friendlyName)) break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(friendlyName))
                        friendlyName = deviceKey.GetValue("DeviceDesc") as string;

                    if (!string.IsNullOrWhiteSpace(friendlyName))
                    {
                        // Strip ";Name" prefix format
                        int semi = friendlyName!.LastIndexOf(';');
                        if (semi >= 0) friendlyName = friendlyName[(semi + 1)..].Trim();

                        if (!string.IsNullOrWhiteSpace(friendlyName))
                            map[symLink] = friendlyName;
                    }
                }
                catch { /* skip bad key */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Registry query failed: {Msg}", ex.Message);
        }
        return map;
    }

    // ── MSMF: list of (friendlyName, symbolicLink) in MSMF index order ────

    private List<(string Name, string SymLink)> GetMFList()
    {
        var list = new List<(string, string)>();
        try
        {
            int hr = MFStartup(MF_VERSION, 0);
            if (hr < 0) return list;
            try
            {
                hr = MFCreateAttributes(out IntPtr pAttrPtr, 1);
                if (hr < 0) return list;

                var pAttr = (IMFAttributes)Marshal.GetObjectForIUnknown(pAttrPtr);
                Marshal.Release(pAttrPtr);

                pAttr.SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                              MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

                hr = MFEnumDeviceSources(
                    Marshal.GetIUnknownForObject(pAttr),
                    out IntPtr ppDevices, out uint count);

                Marshal.ReleaseComObject(pAttr);

                if (hr < 0 || ppDevices == IntPtr.Zero) return list;

                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr pActivatePtr = Marshal.ReadIntPtr(ppDevices,
                            (int)(i * IntPtr.Size));
                        try
                        {
                            var activate = (IMFAttributes)
                                Marshal.GetObjectForIUnknown(pActivatePtr);

                            activate.GetAllocatedString(
                                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                                out string mfName, out _);

                            activate.GetAllocatedString(
                                MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                                out string symLink, out _);

                            list.Add((mfName ?? string.Empty, symLink ?? string.Empty));
                            Marshal.ReleaseComObject(activate);
                        }
                        catch { list.Add((string.Empty, string.Empty)); }
                        finally { Marshal.Release(pActivatePtr); }
                    }
                }
                finally { Marshal.FreeCoTaskMem(ppDevices); }
            }
            finally { MFShutdown(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MF enumeration failed: {Msg}", ex.Message);
        }
        return list;
    }

    private static string NormaliseSymLink(string s) =>
        s.Replace(@"\\?\", "").Replace(@"##?\", "").Replace(@"##?#", "")
         .Replace('#', '\\').ToLowerInvariant().Trim('\\');

    // ── MF COM interface ──────────────────────────────────────────────────

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem([MarshalAs(UnmanagedType.LPStruct)] Guid g, IntPtr v);
        [PreserveSig] int GetItemType([MarshalAs(UnmanagedType.LPStruct)] Guid g, out int t);
        [PreserveSig] int CompareItem([MarshalAs(UnmanagedType.LPStruct)] Guid g, IntPtr v, out bool r);
        [PreserveSig] int Compare(IMFAttributes p, int m, out bool r);
        [PreserveSig] int GetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid g, out uint v);
        [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid g, out ulong v);
        [PreserveSig] int GetDouble([MarshalAs(UnmanagedType.LPStruct)] Guid g, out double v);
        [PreserveSig] int GetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid g, out Guid v);
        [PreserveSig] int GetStringLength([MarshalAs(UnmanagedType.LPStruct)] Guid g, out uint c);
        [PreserveSig] int GetString([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder b, uint s, out uint c);
        [PreserveSig] int GetAllocatedString([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [Out, MarshalAs(UnmanagedType.LPWStr)] out string v, out uint c);
        [PreserveSig] int GetBlobSize([MarshalAs(UnmanagedType.LPStruct)] Guid g, out uint s);
        [PreserveSig] int GetBlob([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [Out] byte[] b, uint s, out uint c);
        [PreserveSig] int GetAllocatedBlob([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            out IntPtr b, out uint s);
        [PreserveSig] int GetUnknown([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [MarshalAs(UnmanagedType.LPStruct)] Guid r, out IntPtr v);
        [PreserveSig] int SetItem([MarshalAs(UnmanagedType.LPStruct)] Guid g, IntPtr v);
        [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.LPStruct)] Guid g);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid g, uint v);
        [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid g, ulong v);
        [PreserveSig] int SetDouble([MarshalAs(UnmanagedType.LPStruct)] Guid g, double v);
        [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [MarshalAs(UnmanagedType.LPStruct)] Guid v);
        [PreserveSig] int SetString([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [MarshalAs(UnmanagedType.LPWStr)] string v);
        [PreserveSig] int SetBlob([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [In] byte[] b, uint s);
        [PreserveSig] int SetUnknown([MarshalAs(UnmanagedType.LPStruct)] Guid g,
            [MarshalAs(UnmanagedType.Interface)] object v);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetItemByIndex(uint i, out Guid g, IntPtr v);
        [PreserveSig] int CopyAllItems(IMFAttributes d);
    }

    private const uint MF_VERSION = 0x00020070;

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint dwFlags);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);
    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFEnumDeviceSources(IntPtr pAttributes,
        out IntPtr pppSourceActivate, out uint pcSourceActivate);
}
