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
/// Enumerates cameras by:
///   1. Reading friendly names from the Windows Registry
///      (HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{camera GUID})
///   2. Probing each MSMF VideoCapture index 0,1,2... to confirm it opens
///   3. Matching names to indices by position
/// No COM, no P/Invoke — just built-in .NET Registry API.
/// </summary>
public class CameraEnumerator
{
    private readonly ILogger<CameraEnumerator> _logger;
    private const int MaxProbeIndex = 8;

    // Windows camera device interface GUID
    private const string CameraInterfaceGuid = "{e5323777-f976-4f5b-9b55-b94699c46e44}";

    public CameraEnumerator(ILogger<CameraEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        // Step 1: get friendly names from registry
        var regNames = GetRegistryNames();
        _logger.LogDebug("Registry returned {Count} camera names: {Names}",
            regNames.Count, string.Join(", ", regNames));

        // Step 2: probe each MSMF index to confirm it opens
        var devices = new List<CameraDeviceInfo>();
        for (int i = 0; i < MaxProbeIndex; i++)
        {
            try
            {
                using var cap = new VideoCapture(i, VideoCaptureAPIs.MSMF);
                if (!cap.IsOpened()) break;

                string name = (i < regNames.Count && !string.IsNullOrWhiteSpace(regNames[i]))
                    ? regNames[i]
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

    private List<string> GetRegistryNames()
    {
        var names = new List<string>();
        try
        {
            // Camera device interface class key
            string keyPath = $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{CameraInterfaceGuid}";
            using var classKey = Registry.LocalMachine.OpenSubKey(keyPath);
            if (classKey == null) return names;

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                try
                {
                    using var deviceKey = classKey.OpenSubKey(subKeyName);
                    if (deviceKey == null) continue;

                    // The friendly name is under the ##?# subkey → Device Parameters → FriendlyName
                    // or directly as "DeviceDesc" in the parent
                    string? friendlyName = null;

                    // Try ##?# subkey first
                    foreach (string sub in deviceKey.GetSubKeyNames())
                    {
                        using var sub2 = deviceKey.OpenSubKey(sub);
                        if (sub2 == null) continue;

                        // Check DeviceDesc or FriendlyName at this level
                        friendlyName = sub2.GetValue("DeviceDesc") as string
                                    ?? sub2.GetValue("FriendlyName") as string;

                        if (!string.IsNullOrWhiteSpace(friendlyName)) break;

                        // Try Device Parameters subkey
                        using var dp = sub2.OpenSubKey("Device Parameters");
                        if (dp != null)
                        {
                            friendlyName = dp.GetValue("FriendlyName") as string;
                            if (!string.IsNullOrWhiteSpace(friendlyName)) break;
                        }
                    }

                    // Also check directly on the device key
                    if (string.IsNullOrWhiteSpace(friendlyName))
                        friendlyName = deviceKey.GetValue("DeviceDesc") as string;

                    if (!string.IsNullOrWhiteSpace(friendlyName))
                    {
                        // DeviceDesc sometimes has ";Camera Name" format — strip prefix
                        int semi = friendlyName!.LastIndexOf(';');
                        if (semi >= 0) friendlyName = friendlyName[(semi + 1)..].Trim();

                        if (!string.IsNullOrWhiteSpace(friendlyName))
                            names.Add(friendlyName);
                    }
                }
                catch { /* skip bad key */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Registry camera name query failed: {Msg}", ex.Message);
        }
        return names;
    }
}
