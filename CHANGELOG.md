# FrameGenLive Changelog

## Latest Release - 2026-11-08

### Major Highlights
- **Self-Contained Autonomous Release**: Zero installation required. Runs directly on Windows 10/11 x64 without needing external .NET SDK or .NET Runtime installed.
- **Multilingual Support (i18n)**: English enabled by default with dynamic one-click switching to Russian in the settings menu (⚙). Settings persist across sessions.
- **Zero-Allocation GPU Rendering Pipeline**: Completely refactored compute shader dispatch and hot capture loops to eliminate per-frame heap allocations (0 GC pauses during gameplay).
- **High-Precision Multi-Rate Pacer**: Hardware-synchronized metronome maintaining stable 144 / 165 / 240+ FPS frame pacing via Windows multimedia timer (`timeBeginPeriod`) and Direct3D 11 Flip Model swap chain.
- **Pure Vector Warping (`--nofade`)**: Eliminates double vision and semi-transparent ghosting by utilizing non-linear optical flow warping with residual confidence thresholds.

### Latency & Performance Improvements
- **Fast Preset (`--fast`)**: Tuned for mouse-driven first-person shooters with minimized queue latency (~20–22 ms buffer) and DXGI latency set to 1.
- **Balanced Preset**: Universal profile for general gaming with adaptive PLL buffer depth (~30 ms).
- **Smooth Preset (`--smooth`)**: Enhanced micro-stutter suppression with temporal vector smoothing (`TSmooth`) for gamepads, racing titles, and unstable frame rates.
- **30 FPS Preset**: Dedicated lock and frame multiplier for 30 FPS console titles and emulators.
- **Direct3D 11 Compute Shaders**: Hierarchical Lucas-Kanade pyramid search executes in ~0.53 ms on modern GPUs, with final interpolation under 0.13 ms.

### Fixes & Ergonomics
- Fixed Windows center cursor lock issue in transparent overlay by intercepting `WM_SETCURSOR` and restoring layered window alpha attributes.
- Resolved 72 FPS / 115 FPS VSync frame throttling in DXGI Flip Model by setting `MaximumFrameLatency = 2` for VSync pipelining.
- Removed parasitic timing reset checks in presentation loop to ensure zero dropped ticks.
- Seamless automatic detection of the engine executable from within the control panel.
