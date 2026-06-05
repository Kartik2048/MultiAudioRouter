# Walkthrough - MultiAudioRouter WPF Application

The **MultiAudioRouter** is a modern, premium Windows desktop application built with WPF and .NET 8.0. It leverages the **NAudio** framework to capture system playback audio in real-time and routes it simultaneously to multiple selected audio outputs.

## UI Design & Aesthetics

The interface is styled using a modern, cohesive dark theme layout. Key details:
- **Color Palette**: Deep dark background (`#121214`), cards background (`#1E1E24`), borders (`#2D2D34`), accented with Indigo (`#6366F1`), Success Green (`#10B981`), and Danger Red (`#EF4444`).
- **Interactive Checklist**: Displays all active system rendering audio devices with status details (sample rate, channel count, default device tags) using standard and custom vectors.
- **Dynamic Control**: A large visual action button changes styling and text based on state ("Start Routing" -> "Stop Routing").
- **Live Output Level Indicator**: Includes a real-time peak audio volume meter showing decibel activity in a smooth visual bar at 30 fps using a low-overhead background capture and dispatcher polling design.

## Technical Architecture

The core routing logic in [MainWindow.xaml.cs](file:///C:/Users/karti/Documents/MultiAudioRouter/MultiAudioRouter/MainWindow.xaml.cs) works as follows:

1. **Loopback Capture**:
   - Uses `WasapiLoopbackCapture` targetting the default system rendering device.
   - Captures playback audio blocks in the system's mix format (typically IEEE 32-bit float PCM).

2. **Multi-device Routing engine**:
   - Replicates audio blocks to a list of active `AudioRoute` instances.
   - Each route writes the block to a `BufferedWaveProvider`.
   - To prevent latency build-up, `DiscardOnBufferOverflow = true` is set on the buffers.

3. **Dynamic Resampling**:
   - If a target device's native mix format matches the capture format, the buffer is fed directly.
   - If they differ (e.g., mismatching sample rates like 44.1kHz vs 48kHz, or bit depths), the audio is processed through NAudio's `MediaFoundationResampler` to dynamically match the target device's mix format.

4. **Safety & Loop Protection**:
   - Compares target device IDs with the default playback device ID.
   - Prompts the user with a confirmation dialog if they attempt to route the loopback back into the default device itself, shielding the system from severe audio loopback echoes.

5. **Live Route Changes**:
   - The checklist handles dynamic addition and removal of device routing endpoints *on-the-fly* without interrupting or restarting the capture recording session.

## Verification Results

The application builds cleanly under .NET 8.0:
- **Warning Count**: 0
- **Error Count**: 0

### Build Command
```bash
dotnet build
```

```text
  Determining projects to restore...
  All projects are up-to-date for restore.
  MultiAudioRouter -> C:\Users\karti\Documents\MultiAudioRouter\MultiAudioRouter\bin\Debug\net8.0-windows\MultiAudioRouter.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```
