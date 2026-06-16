# MultiAudioRouter WPF Application

The **MultiAudioRouter** is a modern, premium Windows desktop application built with WPF and .NET 8.0. It leverages the **NAudio** framework to capture system playback audio in real-time and routes it simultaneously to multiple selected audio outputs.

## UI Design & Aesthetics

The interface is styled using a modern, cohesive dark theme layout. Key details:
- **Color Palette**: Deep dark background (`#121214`), card backgrounds (`#1E1E24`), borders (`#2D2D34`), accented with Indigo (`#6366F1`), Success Green (`#10B981`), and Danger Red (`#EF4444`).
- **Interactive Checklist**: Displays all active system rendering audio devices with status details (sample rate, channel count, default device tags).
- **Dynamic Control**: A large visual action button changes styling and text based on state ("Start Routing" -> "Stop Routing").
- **Live Output Level Indicator**: Includes a real-time peak audio volume meter showing decibel activity in a smooth visual bar at 30 fps using a low-overhead background capture and dispatcher polling design.

## Technical Architecture

The core routing logic in [MainWindow.xaml.cs] works as follows:

1. **Loopback Capture**:
   - Uses `WasapiLoopbackCapture` targeting the default system rendering device.
   - Captures playback audio blocks in the system's mix format (typically IEEE 32-bit float PCM).

2. **Latency-Controlled Routing Engine**:
   - Replicates audio blocks to a list of active `AudioRoute` instances.
   - Each route writes the block to a `BufferedWaveProvider` with low latency (`WasapiOut` buffer set to **30ms**).
   - **Active Latency Control**: To prevent buffer buildup from clock drift, network latency, or scheduling jitter, the route monitors `Buffer.BufferedBytes`. If it exceeds **100ms**, it discards the oldest samples to bring it back to **60ms** (double the player size, ensuring smooth stutter-free playback without lag accumulation).

3. **Dynamic Resampling & Channel Mapping**:
   - Bypasses resampling if formats match. Otherwise, dynamically resampler uses `WdlResamplingSampleProvider` at the end of the pipeline.
   - Supports channel isolation modes (Stereo, LeftOnly, RightOnly) and crossovers (LowPass, HighPass, FullRange).

4. **Safety & Loop Protection**:
   - Prompts the user with a confirmation dialog if they attempt to route the loopback back into the default device itself, shielding the system from feedback loops.

5. **Acoustic Calibration Loop (Auto-Sync)**:
   - Plays a **logarithmic chirp sweep** (500Hz to 8kHz, 250ms duration) to both devices.
   - **Continuous Keep-Alive**: Plays a silent 50Hz hum during calibration to keep WASAPI pipelines active and prevent silent buffers from freezing.
   - Uses **matched filtering (cross-correlation)** on microphone capture (`WasapiCapture` with 300ms warm-up) to identify reference and target peaks, obtaining sub-millisecond measurement precision.
   - **Iterative Control Loop**: Runs up to 3 passes to actively adjust relative delays on-the-fly until the tracking error converges below 2.0ms.
   - **Guided Sync Warnings**: If the reference speaker (default device) is faster, the computed delay is set in the checklist and a warning guide pops up explaining how the user can route it to activate the sync delay.

---

## Verification Results

The application builds cleanly under .NET 8.0:
- **Warning Count**: 0
- **Error Count**: 0

### Build & Run Commands
To build the application:
```powershell
C:\Users\karti\.dotnet\dotnet.exe build
```

To run all unit tests:
```powershell
C:\Users\karti\.dotnet\dotnet.exe test
```

To run the application:
```powershell
C:\Users\karti\.dotnet\dotnet.exe run --project MultiAudioRouter
```
