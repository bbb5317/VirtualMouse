using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Resets a UVC camera device using the Windows SetupAPI.
///
/// Reset sequence (per cycle):
///   1. Disable device via DIF_PROPERTYCHANGE
///   2. Wait DisableWaitMs (default 800ms)
///   3. Enable device via DIF_PROPERTYCHANGE
///   4. Wait EnableWaitMs (default 3000ms) — OV9281 needs ~2-3s to reinitialise
///
/// For stubborn sensor states (e.g. after repeated open/close without proper
/// teardown), the cycle is repeated up to ResetCycles times (default 2).
/// Each cycle gives the sensor a fresh chance to come up cleanly.
/// </summary>
public class UsbDeviceResetter
{
    private readonly ILogger<UsbDeviceResetter> _logger;

    // Timing — conservative values for the OV9281
    public int DisableWaitMs { get; set; } = 800;
    public int EnableWaitMs  { get; set; } = 3000;
    public int ResetCycles   { get; set; } = 2;

    private static readonly Guid GUID_DEVCLASS_IMAGE =
        new("6bdd1fc6-810f-11d0-bec7-08002be2092f");
    private static readonly Guid GUID_DEVCLASS_CAMERA =
        new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");

    public UsbDeviceResetter(ILogger<UsbDeviceResetter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Performs <see cref="ResetCycles"/> disable/enable cycles on every
    /// camera-class device whose friendly name contains
    /// <paramref name="nameFilter"/> (case-insensitive).
    /// Pass null or empty to reset ALL camera devices.
    /// </summary>
    public void ResetCamera(string? nameFilter = null)
    {
        _logger.LogInformation(
            "Camera reset starting ({Cycles} cycle(s), filter='{F}')...",
            ResetCycles, nameFilter ?? "*");

        for (int cycle = 1; cycle <= ResetCycles; cycle++)
        {
            _logger.LogInformation("Reset cycle {C}/{T}", cycle, ResetCycles);

            bool found = TryResetViaSetupApi(GUID_DEVCLASS_CAMERA, nameFilter)
                      || TryResetViaSetupApi(GUID_DEVCLASS_IMAGE,  nameFilter);

            if (!found && cycle == 1)
            {
                _logger.LogWarning(
                    "SetupAPI found no matching devices. Falling back to pnputil.");
                TryResetViaPnpUtil();
            }

            if (cycle < ResetCycles)
            {
                _logger.LogInformation(
                    "Waiting {Ms}ms before next cycle...", EnableWaitMs);
                Thread.Sleep(EnableWaitMs);
            }
        }

        _logger.LogInformation(
            "Camera reset complete. Waiting {Ms}ms for final re-enumeration...",
            EnableWaitMs);
        Thread.Sleep(EnableWaitMs);
        _logger.LogInformation("Camera should now be ready.");
    }

    // ── SetupAPI ───────────────────────────────────────────────────────────

    private bool TryResetViaSetupApi(Guid classGuid, string? nameFilter)
    {
        IntPtr devInfo = SetupDiGetClassDevs(
            ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);

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
                string name = GetDeviceFriendlyName(devInfo, ref devInfoData);
                if (!string.IsNullOrEmpty(nameFilter) &&
                    !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                _logger.LogInformation("  Resetting device: {Name}", name);
                found = true;

                // Disable
                bool disabled = SetDeviceState(devInfo, ref devInfoData, DICS_DISABLE);
                _logger.LogInformation("  Disable: {R}", disabled ? "OK" : $"failed (err {Marshal.GetLastWin32Error()})");
                Thread.Sleep(DisableWaitMs);

                // Enable
                bool enabled = SetDeviceState(devInfo, ref devInfoData, DICS_ENABLE);
                _logger.LogInformation("  Enable:  {R}", enabled ? "OK" : $"failed (err {Marshal.GetLastWin32Error()})");
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
        var p = new SP_PROPCHANGE_PARAMS
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
                ref p, (uint)Marshal.SizeOf(p)))
            return false;
        return SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfo, ref devInfoData);
    }

    private static string GetDeviceFriendlyName(IntPtr devInfo, ref SP_DEVINFO_DATA devInfoData)
    {
        var sb = new StringBuilder(256);
        uint req = 0;
        SetupDiGetDeviceRegistryProperty(devInfo, ref devInfoData,
            SPDRP_FRIENDLYNAME, out _, sb, (uint)sb.Capacity, ref req);
        return sb.ToString();
    }

    // ── pnputil fallback ───────────────────────────────────────────────────

    private void TryResetViaPnpUtil()
    {
        try
        {
            var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "pnputil",
                    Arguments              = "/restart-device /class Camera",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true
                });
            p?.WaitForExit(8000);
            _logger.LogInformation("pnputil exit: {C}", p?.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("pnputil fallback failed: {M}", ex.Message);
        }
    }

    // ── P/Invoke ───────────────────────────────────────────────────────────

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT      = 0x00000002;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    private const uint DIF_PROPERTYCHANGE = 0x00000012;
    private const uint DICS_ENABLE        = 0x00000001;
    private const uint DICS_DISABLE       = 0x00000002;
    private const uint DICS_FLAG_GLOBAL   = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize; public uint InstallFunction;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange; public uint Scope; public uint HwProfile;
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
