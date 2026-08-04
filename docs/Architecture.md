# Virtual Mouse Architecture Design

## 1. Overview

The Virtual Mouse project aims to replace a traditional physical mouse with a vision-based tracking system. By attaching reflective markers to the user's fingers and tracking them with a high-speed global shutter camera (ArduCam-OV9281), the system calculates precise finger movements and translates them into OS-level mouse commands.

## 2. Marker Configuration

Based on the initial design, the user wears reflective markers as follows:

*   **Left Hand**:
    *   Index Finger: 3 markers
    *   Thumb: 1 marker
*   **Right Hand**:
    *   Index Finger: 3 markers
    *   Middle Finger: 3 markers

This asymmetric configuration allows the computer vision algorithm to distinguish between the left and right hands, as well as specific fingers, based on the spatial grouping and count of the blobs detected.

## 3. Technology Stack

*   **Language**: C# 12
*   **Framework**: .NET 8.0 (Windows-focused for POC, cross-platform ready for future)
*   **Computer Vision**: `OpenCvSharp4` (C# wrapper for OpenCV)
*   **Camera Capture**: DirectShow / Media Foundation (UVC driverless access)
*   **OS Interaction**: Windows API (`user32.dll` -> `SendInput`)

## 4. Pipeline Flow

The system operates in a continuous loop:

1.  **Frame Acquisition**: Fetch a monochrome frame from the ArduCam-OV9281.
2.  **Pre-processing**: Apply a strict binary threshold to isolate the highly bright reflective markers from the dark background.
3.  **Blob Detection**: Identify connected components (blobs) in the thresholded image.
4.  **Centroid Calculation**: Calculate the intensity-weighted centroid of each blob to achieve sub-pixel (sub-millimeter) accuracy.
5.  **Grouping & Identification**: Group the centroids based on proximity. Identify the left index (3), left thumb (1), right index (3), and right middle (3).
6.  **Gesture Translation**: 
    *   Calculate the delta movement of the designated "pointer" finger.
    *   Detect pinch gestures (e.g., left thumb and index coming together) for clicks.
7.  **OS Injection**: Send the calculated movement and click states to the Windows OS.

## 5. Sub-Millimeter Precision Strategy

To achieve the requested sub-millimeter precision:

1.  **Hardware**: The global shutter eliminates rolling shutter distortion during fast finger swipes.
2.  **Algorithm**: Instead of simple bounding-box centers, the system will use **Image Moments** (`cv2.moments`) on the raw grayscale image within the bounding box of the thresholded blob. This calculates the center of mass based on pixel intensity, providing sub-pixel coordinates.
3.  **Filtering**: A Kalman filter or 1 Euro filter will be applied to the calculated 2D coordinates to smooth out high-frequency jitter (micro-tremors in the hand or camera noise) without adding significant latency.
