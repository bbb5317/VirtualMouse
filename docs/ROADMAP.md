# Virtual Mouse — Development Roadmap

## Phase 1: Proof of Concept (Current)

The current phase establishes the foundational tracking and input pipeline on Windows using C# .NET 8.0.

**Goals:**
- [x] Repository and project structure created
- [x] Core models: `MarkerBlob`, `MarkerGroup`, `GestureState`
- [x] Camera capture via UVC/DirectShow (OpenCvSharp4)
- [x] Binary threshold + intensity-weighted centroid blob detection
- [x] Proximity-based marker grouper with finger identity assignment
- [x] Basic gesture recognizer: cursor movement + left pinch click
- [x] Windows OS mouse injection via `SendInput` P/Invoke
- [x] WPF UI with live preview and calibration sliders
- [x] Unit tests for core grouping logic

## Phase 2: Gesture Expansion

**Goals:**
- [ ] Right-click gesture (right middle finger tap or double-tap)
- [ ] Scroll gesture (two-finger vertical movement)
- [ ] Double-click detection (rapid pinch-release-pinch)
- [ ] Click-and-drag (sustained pinch while moving)
- [ ] Gesture debouncing and hold-time thresholds to prevent accidental clicks
- [ ] Kalman filter or 1 Euro filter for smooth cursor movement

## Phase 3: Calibration & Persistence

**Goals:**
- [ ] Save/load calibration settings to `assets/config/calibration.json`
- [ ] Camera-to-screen coordinate mapping (perspective calibration)
- [ ] Automatic brightness threshold detection via histogram analysis
- [ ] Marker assignment memory (persist identity across frames using Kalman prediction)

## Phase 4: Robustness & Performance

**Goals:**
- [ ] Handle partial occlusion (one or two markers of a finger temporarily hidden)
- [ ] Multi-threaded pipeline: capture thread → processing thread → UI thread
- [ ] Performance profiling and optimization for consistent 120fps processing
- [ ] Logging and diagnostics export

## Phase 5: Cross-Platform

**Goals:**
- [ ] Abstract OS input injection behind `IMouseController` interface
- [ ] Linux implementation using `xdotool` or `uinput`
- [ ] macOS implementation using `CGEventPost`
- [ ] Replace WPF UI with Avalonia UI for cross-platform compatibility
- [ ] Package as a self-contained executable for each platform
