# Setup Guide — Virtual Mouse (Windows)

## Prerequisites

Before building and running the Virtual Mouse application, ensure the following are installed on your Windows machine.

| Requirement | Version | Notes |
|---|---|---|
| .NET SDK | 8.0 or later | [Download from Microsoft](https://dotnet.microsoft.com/download) |
| Visual Studio | 2022 (v17.8+) | Community edition is free; install the **.NET desktop development** workload |
| ArduCam-OV9281 | Any UVC variant | The camera must be **UVC-compliant** (works as a standard webcam) |

## Camera Setup

The ArduCam-OV9281 USB camera used in this project is **UVC-compliant**, meaning it requires **no proprietary drivers** on Windows 10/11. Simply plug it in via USB and Windows will automatically install the standard UVC driver.

> **Important**: Verify the camera appears in **Device Manager** under **Cameras** or **Imaging Devices** (not as an unknown device). If it appears as an unknown device, your specific board variant may use the proprietary Arducam driver — follow the [Arducam Windows Driver Installation guide](https://docs.arducam.com/USB-Industrial-Camera/Quick-Start-Guide/Windows-Driver-Installation/).

## Marker Setup

To achieve sub-millimeter tracking precision, use **retro-reflective tape** (e.g., 3M Scotchlite) cut into small squares (~8×8mm). Apply them to your fingers as follows:

| Hand | Finger | Marker Count | Purpose |
|---|---|---|---|
| Left | Thumb | 1 | Pinch gesture (left click) |
| Left | Index | 3 | Pinch gesture reference |
| Right | Index | 3 | Primary cursor movement |
| Right | Middle | 3 | Secondary gesture (right click — future) |

**Lighting**: Position a ring light or IR LED array close to the camera lens. Retro-reflective markers return light directly back to the source, so a co-axial light source will produce extremely bright blobs against a dark background — ideal for thresholding.

## Building the Solution

1. Open a terminal in the repository root.
2. Navigate to the solution directory:
   ```
   cd src
   ```
3. Restore NuGet packages:
   ```
   dotnet restore VirtualMouse.sln
   ```
4. Build the solution:
   ```
   dotnet build VirtualMouse.sln -c Release
   ```

## Running the Application

```
dotnet run --project VirtualMouse.UI/VirtualMouse.UI.csproj -c Release
```

Or open `src/VirtualMouse.sln` in Visual Studio and press **F5**.

## Calibration

On first launch, use the **Calibration** panel on the right side of the UI:

1. **Brightness Threshold**: Increase this value until only the reflective markers appear as white blobs in the preview. Aim for clean, isolated blobs with no background noise.
2. **Mouse Sensitivity**: Adjust how many screen pixels the cursor moves per pixel of finger movement in the camera frame.
3. **Pinch Click Threshold**: The maximum distance (in camera pixels) between the left thumb and left index centroids that registers as a left click.

## Running Tests

```
cd tests
dotnet test VirtualMouse.Tests/VirtualMouse.Tests.csproj
```
