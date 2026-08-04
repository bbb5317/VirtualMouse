# Virtual Mouse

**Virtual Mouse** is a proof-of-concept C# .NET application designed to control the computer mouse cursor using reflective finger markers. By leveraging high-speed computer vision techniques, it tracks markers attached to the user's fingers and translates these movements into precise mouse actions. 

This project utilizes the **ArduCam-OV9281** global shutter USB camera, which is UVC-compliant, to capture high-framerate (up to 120fps) monochrome video, ensuring minimal motion blur and high responsiveness.

## 🚀 Features

* **High-Speed Tracking**: Utilizes the ArduCam-OV9281 global shutter camera to capture fast finger movements without distortion.
* **Sub-Millimeter Precision**: Implements sub-pixel centroid tracking and blob detection algorithms (via OpenCV/OpenCvSharp) to accurately locate bright reflective markers against a dark background.
* **Cross-Platform Potential**: Designed with a modular architecture. While this initial proof-of-concept is built for **Windows**, the underlying logic can be adapted for Mac and Linux.
* **Gesture Recognition**: Translates the relative positions of the markers on the left hand (thumb and index) and right hand (index and middle fingers) into distinct mouse actions (movement, clicks, scrolling).

## 🛠️ Hardware Requirements

* **Camera**: ArduCam-OV9281 1MP Monochrome Global Shutter USB Camera (UVC Compliant).
* **Markers**: Retro-reflective tape or markers placed on the user's fingers:
  * **Left Hand**: 3 markers on the index finger, 1 marker on the thumb.
  * **Right Hand**: 3 markers on the index finger, 3 markers on the middle finger.
* **Lighting**: An infrared (IR) or strong visible light source positioned near the camera lens to illuminate the retro-reflective markers.

## 🏗️ Software Architecture

The application is built using **C# .NET 8.0** and is structured into the following core modules:

1. **`VirtualMouse.Vision`**: Handles the UVC camera stream via MediaFoundation/DirectShow and processes frames using `OpenCvSharp4`. It performs thresholding, blob detection, and sub-pixel centroid calculations to locate the markers.
2. **`VirtualMouse.Core`**: The central processing unit. It takes the raw 2D coordinates of the detected markers, identifies which hand/finger they belong to based on geometric constraints, and calculates the intended gesture or movement vector.
3. **`VirtualMouse.Input`**: Interfaces with the Windows OS (via P/Invoke `SendInput` or similar libraries) to inject synthetic mouse movements and click events.
4. **`VirtualMouse.UI`**: A simple WPF or WinForms interface for camera calibration, threshold adjustment, and real-time visualization of the tracking process.

## 📦 Getting Started

*(Instructions will be added once the initial codebase is scaffolded)*

## 📄 License

This project is licensed under the MIT License.
