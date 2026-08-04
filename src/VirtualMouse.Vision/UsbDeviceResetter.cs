using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Resets a UVC camera device using the Windows SetupAPI.
///
/// The reset sequence is: Disable device → wait 500ms → Enable device → wait 1500ms.
/// This forces the USB stack to fully power-cycle the device's data endpoint,
/// clearing any stale sensor state left over from a previous session.
///
/// This is equivalent to right-clicking the device in Device Manager and
/// choosing "Disable device" then "Enable device", but done programmatically.
///
/// Requires the application to run with administrator privileges, OR the
/// device must be in the "Cameras" or "Imaging devices" class which Windows
/// allows non-admin processes to restart on Windows 10 21H2+ via
/// CM_Disable_DevNode / CM_Enable_DevNode.
///
/// Falls back to pnputil.exe if the SetupAPI call fails (pnputil requires
/// admin but is more broadly compatible).
/// </summary>
public class UsbDeviceResetter
{
    private readonly ILogger<UsbDeviceResetter> _logger;

    // SetupAPI GUID for the "Image" device class (cameras / imaging devices)
    private static readonly Guid GUID_DEVCLASS_IMAGE =
        new("6bdd1fc6-810f-11d0-bec7-08002be2092f");

    // SetupAPI GUID for the "Camera" device class (Windows 10 1803+)
    private static readonly Guid GUID_DEVCLASS_CAMERA =
        new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");

    public UsbDeviceResetter(ILogger<UsbDeviceResetter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds all devices in the Camera/Image class whose friendly name contains
    /// <paramref name="deviceNameFragment"/> (case-insensitive), then disables
    /// and re-enables each one to force a clean reset.
    /// </summary>
    /// <param name="deviceNameFragment">
    /// Partial name to match, e.g. "Arducam" or "OV9281".
    /// Pass null or empty to reset ALL camera-class devices.
    /// </param>
    public void ResetCamera(string? deviceNameFragment = null)
    {
        _logger.LogInformation("Resetting camera device (filter: '{F}')...", deviceNameFragment ?? "*");

        bool any = TryResetViaSetupApi(GUID_DEVCLASS_CAMERA, deviceNameFragment)
                || TryResetViaSetupApi(GUID_DEVCLASS_IMAGE,  deviceNameFragment);

        if (!any)
        {
            _logger.LogWarning(
                "SetupAPI reset found no matching devices. Falling back to pnputil.");
            TryResetViaPnpUtil(deviceNameFragment);
        }

        // Wait for the device to fully re-enumerate and the driver to initialise
        _logger.LogInformation("Waiting for camera to re-enumerate...");
        Thread.Sleep(2000);
        _logger.LogInformation("Camera reset complete.");
    }

    // ── SetupAPI approach ──────────────────────────────────────────────────

    private bool TryResetViaSetupApi(Guid classGuid, string? nameFilter)
    {
        IntPtr devInfo = SetupDiGetClassDevs(
            ref classGuid,
            null,
            IntPtr.Zero,
            DIGCF_PRESENT);

        if (devInfo == INVALID_HANDLE_VALUE)
        {
            _logger.LogDebug("SetupDiGetClassDevs failed: {E}", Marshal.GetLastWin32Error());
            return false;
        }

        bool found = false;
        try
        {
            var devInfoData = new SP_DEVINFO_DATA();
            devInfoData.cbSize = (uint)Marshal.SizeOf(devInfoData);

            for (uint i = 0; SetupDiEnumDeviceInfo(devInfo, i, ref devInfoData); i++)
            {
                string friendlyName = GetDeviceFriendlyName(devInfo, ref devInfoData);

                if (!string.IsNullOrEmpty(nameFilter) &&
                    !friendlyName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                _logger.LogInformation("Resetting: {Name}", friendlyName);
                found = true;

                // Disable
                if (SetDeviceState(devInfo, ref devInfoData, DICS_DISABLE))
                {
                    _logger.LogDebug("  Disabled OK.");
                    Thread.Sleep(500);
                }
                else
                {
                    _logger.LogWarning("  Disable failed (error {E}). Trying enable anyway.",
                        Marshal.GetLastWin32Error());
                }

                // Enable
                if (SetDeviceState(devInfo, ref devInfoData, DICS_ENABLE))
                    _logger.LogDebug("  Enabled OK.");
                else
                    _logger.LogWarning("  Enable failed (error {E}).", Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return found;
    }

    private bool SetDeviceState(IntPtr devInfo, ref SP_DEVINFO_DATA devInfoData, uint stateChange)
    {
        var propChangeParams = new SP_PROPCHANGE_PARAMS
        {
            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
            {
                cbSize          = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                InstallFunction = DIF_PROPERTYCHANGE
            },
            StateChange = stateChange,
            Scope       = DICS_FLAG_GLOBAL,
            HwProfile   = 0
        };

        if (!SetupDiSetClassInstallParams(devInfo, ref devInfoData,
                ref propChangeParams, (uint)Marshal.SizeOf(propChangeParams)))
            return false;

        return SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfo, ref devInfoData);
    }

    private static string GetDeviceFriendlyName(IntPtr devInfo, ref SP_DEVINFO_DATA devInfoData)
    {
        var sb = new StringBuilder(256);
        uint reqSize = 0;
        SetupDiGetDeviceRegistryProperty(
            devInfo, ref devInfoData,
            SPDRP_FRIENDLYNAME,
            out _, sb, (uint)sb.Capacity, ref reqSize);
        return sb.ToString();
    }

    // ── pnputil fallback ───────────────────────────────────────────────────

    private void TryResetViaPnpUtil(string? nameFilter)
    {
        try
        {
            // pnputil /restart-device requires the hardware ID.
            // We use /enum-devices to find the hardware ID, then restart it.
            // This requires admin privileges.
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "pnputil",
                Arguments = "/restart-device \"USB\\VID_*\" /class Camera",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            });
            proc?.WaitForExit(5000);
            _logger.LogInformation("pnputil exit code: {C}", proc?.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("pnputil fallback failed: {Msg}", ex.Message);
        }
    }

    // ── P/Invoke declarations ──────────────────────────────────────────────

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT       = 0x00000002;
    private const uint SPDRP_FRIENDLYNAME  = 0x0000000C;
    private const uint DIF_PROPERTYCHANGE  = 0x00000012;
    private const uint DICS_ENABLE         = 0x00000001;
    private const uint DICS_DISABLE        = 0x00000002;
    private const uint DICS_FLAG_GLOBAL    = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint  cbSize;
        public Guid  ClassGuid;
        public uint  DevInst;
        public nint  Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property, out uint PropertyRegDataType,
        StringBuilder PropertyBuffer, uint PropertyBufferSize, ref uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_PROPCHANGE_PARAMS ClassInstallParams, uint ClassInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
}
