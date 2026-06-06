using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiAudioRouter
{
    public partial class MainWindow : Window
    {
        // Device selection wrapper class
        public class DeviceItem : System.ComponentModel.INotifyPropertyChanged
        {
            private int delayMs;
            private int measuredLatencyMs;

            public string Id { get; set; }
            public string Name { get; set; }
            public string StatusText { get; set; }
            public MMDevice Device { get; set; }
            public bool IsSelected { get; set; }
            public bool IsDefault { get; set; }

            public int DelayMs
            {
                get => delayMs;
                set
                {
                    if (delayMs != value)
                    {
                        delayMs = value;
                        OnPropertyChanged(nameof(DelayMs));
                        DelayChangedCallback?.Invoke(Id, delayMs);
                    }
                }
            }

            public int MeasuredLatencyMs
            {
                get => measuredLatencyMs;
                set
                {
                    if (measuredLatencyMs != value)
                    {
                        measuredLatencyMs = value;
                        OnPropertyChanged(nameof(MeasuredLatencyMs));
                        OnPropertyChanged(nameof(MeasuredLatencyText));
                    }
                }
            }

            public string MeasuredLatencyText => $"Measured Latency: {measuredLatencyMs}ms";

            public Action<string, int> DelayChangedCallback { get; set; }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
            }
        }

        // Active routing player structure
        private class AudioRoute : IDisposable
        {
            public MMDevice TargetDevice { get; }
            public WasapiOut Player { get; }
            public BufferedWaveProvider Buffer { get; }
            public IWaveProvider Resampler { get; }
            public DelayWaveProvider DelayProvider { get; }

            public AudioRoute(MMDevice targetDevice, WaveFormat captureFormat)
            {
                TargetDevice = targetDevice;

                // Create input buffer with 500ms capacity to prevent stuttering while avoiding infinite lag buildup
                Buffer = new BufferedWaveProvider(captureFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromMilliseconds(500)
                };

                var targetFormat = targetDevice.AudioClient.MixFormat;

                // Check if capture sample rate matches target device mix sample rate exactly
                // If they match, bypass resampling entirely and let WASAPI handle channels and bit depth
                bool sampleRatesMatch = captureFormat.SampleRate == targetFormat.SampleRate;
                IWaveProvider finalProvider;

                if (sampleRatesMatch)
                {
                    Resampler = null;
                    finalProvider = Buffer;
                }
                else
                {
                    // Dynamically resample to target device's native mix format
                    var resampler = new MediaFoundationResampler(Buffer, targetFormat)
                    {
                        ResamplerQuality = 60
                    };
                    Resampler = resampler;
                    finalProvider = resampler;
                }

                // Wrap finalProvider with the delay line
                DelayProvider = new DelayWaveProvider(finalProvider);

                Player = new WasapiOut(targetDevice, AudioClientShareMode.Shared, true, 100);
                Player.Init(DelayProvider);
                Player.Play();
            }

            public void SetDelay(int milliseconds)
            {
                DelayProvider?.SetDelay(milliseconds);
            }

            public void AddSamples(byte[] data, int offset, int count)
            {
                Buffer.AddSamples(data, offset, count);
            }

            public void Dispose()
            {
                try { Player?.Stop(); } catch { }
                Player?.Dispose();

                if (Resampler is IDisposable d)
                {
                    d.Dispose();
                }
            }
        }

        // Custom delay line WaveProvider using a circular buffer
        private class DelayWaveProvider : IWaveProvider
        {
            private readonly IWaveProvider sourceProvider;
            private readonly int bytesPerMillisecond;
            private int delayBytes;
            private byte[] delayBuffer;
            private int writePos;
            private int readPos;
            private readonly object lockObject = new object();

            public WaveFormat WaveFormat => sourceProvider.WaveFormat;

            public DelayWaveProvider(IWaveProvider sourceProvider)
            {
                this.sourceProvider = sourceProvider;
                this.bytesPerMillisecond = sourceProvider.WaveFormat.AverageBytesPerSecond / 1000;

                // Circular buffer capacity for up to 1100 milliseconds of audio delay
                int bufferCapacity = bytesPerMillisecond * 1100;
                this.delayBuffer = new byte[bufferCapacity];
            }

            public void SetDelay(int milliseconds)
            {
                lock (lockObject)
                {
                    int targetDelayBytes = milliseconds * bytesPerMillisecond;
                    if (targetDelayBytes != delayBytes)
                    {
                        // Shift read pointer to adjust delay
                        int newReadPos = writePos - targetDelayBytes;
                        while (newReadPos < 0)
                        {
                            newReadPos += delayBuffer.Length;
                        }
                        readPos = newReadPos % delayBuffer.Length;
                        delayBytes = targetDelayBytes;
                    }
                }
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                // Pull source data
                byte[] tempBuffer = new byte[count];
                int bytesRead = sourceProvider.Read(tempBuffer, 0, count);

                lock (lockObject)
                {
                    // Copy new source data into circular delay line buffer
                    for (int i = 0; i < bytesRead; i++)
                    {
                        delayBuffer[writePos] = tempBuffer[i];
                        writePos = (writePos + 1) % delayBuffer.Length;
                    }

                    if (delayBytes == 0)
                    {
                        // 0ms delay: bypass circular reading
                        Array.Copy(tempBuffer, 0, buffer, offset, bytesRead);
                        readPos = writePos;
                        return bytesRead;
                    }

                    // Copy delayed data from circular buffer
                    for (int i = 0; i < bytesRead; i++)
                    {
                        buffer[offset + i] = delayBuffer[readPos];
                        readPos = (readPos + 1) % delayBuffer.Length;
                    }

                    return bytesRead;
                }
            }
        }

        private List<DeviceItem> devicesList = new List<DeviceItem>();
        private WasapiLoopbackCapture capture;
        private readonly Dictionary<string, AudioRoute> activeRoutes = new Dictionary<string, AudioRoute>();
        private readonly object routesLock = new object();
        private bool isRouting = false;

        private float currentPeakVolume = 0f;
        private readonly DispatcherTimer uiUpdateTimer;

        public MainWindow()
        {
            InitializeComponent();

            // Set up volume UI level updating at 30 fps
            uiUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            uiUpdateTimer.Start();

            // Populate initial list of active rendering devices
            RefreshDevicesList();
        }

        private void RefreshDevicesList()
        {
            var selectedIds = devicesList.Where(d => d.IsSelected).Select(d => d.Id).ToHashSet();
            var previousDelays = devicesList.ToDictionary(d => d.Id, d => d.DelayMs);
            devicesList.Clear();

            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                    foreach (var device in devices)
                    {
                        bool isDefault = device.ID == defaultDevice?.ID;
                        string status = $"{device.AudioClient.MixFormat.SampleRate}Hz | {device.AudioClient.MixFormat.Channels}ch";
                        if (isDefault)
                        {
                            status += " (Default Out)";
                        }

                        int prevDelay = previousDelays.TryGetValue(device.ID, out int dVal) ? dVal : 0;
                        var item = new DeviceItem
                        {
                            Id = device.ID,
                            Name = device.FriendlyName,
                            StatusText = status,
                            Device = device,
                            IsSelected = selectedIds.Contains(device.ID),
                            IsDefault = isDefault,
                            DelayMs = prevDelay
                        };
                        item.DelayChangedCallback = OnDeviceDelayChanged;
                        devicesList.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to enumerate audio devices: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            LstDevices.ItemsSource = null;
            LstDevices.ItemsSource = devicesList;
        }

        private void OnDeviceDelayChanged(string deviceId, int delayMs)
        {
            string devName = devicesList.FirstOrDefault(d => d.Id == deviceId)?.Name ?? deviceId;
            System.Console.WriteLine($"[Delay Adjustment] Set delay for '{devName}' to {delayMs}ms");
            lock (routesLock)
            {
                if (activeRoutes.TryGetValue(deviceId, out var route))
                {
                    route.SetDelay(delayMs);
                }
            }
        }

        private byte[] GeneratePingSamples(WaveFormat format, out int sampleCount)
        {
            int sampleRate = format.SampleRate;
            int channels = format.Channels;
            double durationSeconds = 0.050; // 50ms
            sampleCount = (int)(sampleRate * durationSeconds);
            int totalBytes = sampleCount * channels * (format.BitsPerSample / 8);
            byte[] rawData = new byte[totalBytes];

            double frequency = 1000.0; // 1kHz
            float amplitude = 0.8f;
            int bytesPerSample = format.BitsPerSample / 8;

            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / sampleRate;
                float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * time));

                // Apply a 5ms linear fade envelope to prevent click artifacts
                double fadeDuration = 0.005; // 5ms
                if (time < fadeDuration)
                {
                    value *= (float)(time / fadeDuration);
                }
                else if (time > durationSeconds - fadeDuration)
                {
                    value *= (float)((durationSeconds - time) / fadeDuration);
                }

                // Write to all channels
                for (int channel = 0; channel < channels; channel++)
                {
                    int offset = (i * channels + channel) * bytesPerSample;
                    if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
                    {
                        byte[] bytes = BitConverter.GetBytes(value);
                        Array.Copy(bytes, 0, rawData, offset, 4);
                    }
                    else if (format.BitsPerSample == 16)
                    {
                        short shortVal = (short)(value * 32767);
                        byte[] bytes = BitConverter.GetBytes(shortVal);
                        Array.Copy(bytes, 0, rawData, offset, 2);
                    }
                    else if (format.BitsPerSample == 24)
                    {
                        int intVal = (int)(value * 8388607);
                        byte[] bytes = BitConverter.GetBytes(intVal);
                        Array.Copy(bytes, 0, rawData, offset, 3);
                    }
                }
            }

            return rawData;
        }

        private void PlayPingToDevice(MMDevice device, byte[] pingBytes, WaveFormat format)
        {
            using (var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 50))
            using (var ms = new System.IO.MemoryStream(pingBytes))
            using (var rawStream = new RawSourceWaveStream(ms, format))
            {
                player.Init(rawStream);
                player.Play();
                System.Threading.Thread.Sleep(100);
                player.Stop();
            }
        }

        private double MeasurePingDelay(MMDevice renderDevice)
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                return -1;
            }

            int targetDeviceNumber = 0;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var commMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    string micFriendlyName = commMic.FriendlyName;
                    
                    for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                    {
                        var caps = WaveInEvent.GetCapabilities(i);
                        string prodName = caps.ProductName ?? "";
                        if (micFriendlyName.Contains(prodName) || prodName.Contains(micFriendlyName))
                        {
                            targetDeviceNumber = i;
                            System.Console.WriteLine($"[AcousticMeasurer] Matched default communications mic '{micFriendlyName}' to waveIn index {i}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AcousticMeasurer] Error resolving default communication mic: {ex.Message}");
            }

            var stopwatch = new System.Diagnostics.Stopwatch();
            double detectedTimeMs = -1;
            bool pingDetected = false;

            try
            {
                using (var waveIn = new WaveInEvent())
                {
                    waveIn.DeviceNumber = targetDeviceNumber;
                    waveIn.WaveFormat = new WaveFormat(44100, 16, 1);
                    waveIn.BufferMilliseconds = 10;

                    double maxBaseline = 0;

                    waveIn.DataAvailable += (s, e) =>
                    {
                        float peak = 0;
                        int sampleCount = e.BytesRecorded / 2;
                        for (int i = 0; i < sampleCount; i++)
                        {
                            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                            float absSample = Math.Abs(sample) / 32768f;
                            if (absSample > peak) peak = absSample;
                        }

                        if (!stopwatch.IsRunning)
                        {
                            if (peak > maxBaseline) maxBaseline = peak;
                            System.Console.WriteLine($"[AcousticMeasurer] Noise Baseline Peak: {peak:F4} (MaxBaseline: {maxBaseline:F4})");
                        }
                        else if (!pingDetected)
                        {
                            // Highly sensitive threshold (0.005) with 2x baseline multiplier
                            double threshold = Math.Max(0.005, maxBaseline * 2.0);
                            System.Console.WriteLine($"[AcousticMeasurer] Ping Capture Peak: {peak:F4} (Threshold: {threshold:F4})");
                            
                            if (peak > threshold)
                            {
                                detectedTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                                pingDetected = true;
                                stopwatch.Stop();
                                System.Console.WriteLine($"[AcousticMeasurer] Spike Detected at {detectedTimeMs:F2} ms!");
                            }
                        }
                    };

                    waveIn.StartRecording();

                    // Warm up stream and collect background noise floor (400ms)
                    System.Threading.Thread.Sleep(400);

                    int pSampleCount;
                    byte[] pingBytes = GeneratePingSamples(renderDevice.AudioClient.MixFormat, out pSampleCount);

                    stopwatch.Start();
                    PlayPingToDevice(renderDevice, pingBytes, renderDevice.AudioClient.MixFormat);

                    // Wait for spike (extended 2.5s timeout)
                    int timeoutCount = 0;
                    while (!pingDetected && timeoutCount < 250) // 250 * 10ms = 2500ms
                    {
                        System.Threading.Thread.Sleep(10);
                        timeoutCount++;
                    }

                    waveIn.StopRecording();
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AcousticMeasurer] Error during record/play cycle: {ex.Message}");
                return -1;
            }

            return detectedTimeMs;
        }

        private void BtnAutoSync_Click(object sender, RoutedEventArgs e)
        {
            var routedItem = LstDevices.SelectedItem as DeviceItem;
            if (routedItem == null)
            {
                MessageBox.Show("Please select (click on) a routed output device in the list to calibrate it against the Master device.", "Select Device", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string routedDeviceId = routedItem.Id;
            string routedDeviceName = routedItem.Name;

            BtnStartStop.IsEnabled = false;
            TxtCaptureDevice.Text = "Calibration: Preparing...";

            System.Console.WriteLine($"[Auto-Sync] Initiating calibration between Master and '{routedDeviceName}'...");

            var thread = new System.Threading.Thread(() =>
            {
                bool wasRouting = false;
                Dispatcher.Invoke(() =>
                {
                    wasRouting = isRouting;
                    if (isRouting) StopRouting();
                    TxtCaptureDevice.Text = "Calibration: Measuring Master latency...";
                });

                try
                {
                    using (var enumerator = new MMDeviceEnumerator())
                    {
                        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                        var routedDevice = enumerator.GetDevice(routedDeviceId);

                        System.Console.WriteLine($"[Auto-Sync] Running measurement on STA Thread.");
                        System.Console.WriteLine($"[Auto-Sync] Master: {defaultDevice.FriendlyName}");
                        System.Console.WriteLine($"[Auto-Sync] Routed: {routedDevice.FriendlyName}");

                        // 1. Measure default playback output (Master)
                        double latencyMaster = MeasurePingDelay(defaultDevice);
                        if (latencyMaster < 0)
                        {
                            System.Console.WriteLine("[Auto-Sync] Error: Calibration failed on Master device.");
                            Dispatcher.Invoke(() =>
                            {
                                TxtCaptureDevice.Text = "Calibration: Master failed";
                                BtnStartStop.IsEnabled = true;
                                MessageBox.Show("Acoustic calibration failed on the default Master output.\n\nEnsure speakers are unmuted, microphone is unmuted, and volume is sufficient.", "Calibration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                if (wasRouting) StartRouting();
                            });
                            return;
                        }

                        Dispatcher.Invoke(() =>
                        {
                            TxtCaptureDevice.Text = "Calibration: Measuring Routed latency...";
                        });

                        // 2. Measure selected target playback output (Routed)
                        double latencyRouted = MeasurePingDelay(routedDevice);
                        if (latencyRouted < 0)
                        {
                            System.Console.WriteLine($"[Auto-Sync] Error: Calibration failed on routed device: {routedDeviceName}");
                            Dispatcher.Invoke(() =>
                            {
                                TxtCaptureDevice.Text = "Calibration: Routed failed";
                                BtnStartStop.IsEnabled = true;
                                MessageBox.Show($"Acoustic calibration failed on routed device '{routedDeviceName}'.\n\nEnsure device is connected, selected, and volume is sufficient.", "Calibration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                if (wasRouting) StartRouting();
                            });
                            return;
                        }

                        // 3. Compute delta and update UI
                        int lMaster = (int)latencyMaster;
                        int lRouted = (int)latencyRouted;
                        int delta = Math.Abs(lMaster - lRouted);

                        System.Console.WriteLine($"[Auto-Sync] Complete. Master latency: {lMaster}ms | Routed latency: {lRouted}ms | Delta: {delta}ms");

                        Dispatcher.Invoke(() =>
                        {
                            // Update UI labels
                            var masterItem = devicesList.FirstOrDefault(d => d.IsDefault);
                            if (masterItem != null)
                            {
                                masterItem.MeasuredLatencyMs = lMaster;
                            }
                            
                            // Re-fetch routedItem reference in case list was refreshed
                            var currentRoutedItem = devicesList.FirstOrDefault(d => d.Id == routedDeviceId);
                            if (currentRoutedItem != null)
                            {
                                currentRoutedItem.MeasuredLatencyMs = lRouted;
                            }

                            // Apply delay offset to the faster device
                            if (lMaster < lRouted)
                            {
                                System.Console.WriteLine($"[Auto-Sync] Master is faster. Applying {delta}ms delay to Master.");
                                if (masterItem != null) masterItem.DelayMs = delta;
                                if (currentRoutedItem != null) currentRoutedItem.DelayMs = 0;
                            }
                            else
                            {
                                System.Console.WriteLine($"[Auto-Sync] Routed device is faster. Applying {delta}ms delay to Routed.");
                                if (currentRoutedItem != null) currentRoutedItem.DelayMs = delta;
                                if (masterItem != null) masterItem.DelayMs = 0;
                            }

                            BtnStartStop.IsEnabled = true;
                            TxtCaptureDevice.Text = $"Calibration: Done (Delta: {delta}ms)";
                            
                            MessageBox.Show($"Acoustic Calibration Complete!\n\n" +
                                            $"Master Device Latency: {lMaster}ms\n" +
                                            $"Routed Device Latency: {lRouted}ms\n\n" +
                                            $"Calculated Offset: {delta}ms applied to the faster output.",
                                            "Sync Successful", MessageBoxButton.OK, MessageBoxImage.Information);

                            if (wasRouting) StartRouting();
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Auto-Sync] Critical thread error: {ex.Message}\n{ex.StackTrace}");
                    Dispatcher.Invoke(() =>
                    {
                        TxtCaptureDevice.Text = "Calibration: Error";
                        BtnStartStop.IsEnabled = true;
                        MessageBox.Show($"An unexpected error occurred during calibration:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        if (wasRouting) StartRouting();
                    });
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshDevicesList();
        }

        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (isRouting)
            {
                StopRouting();
            }
            else
            {
                StartRouting();
            }
        }

        private void StartRouting()
        {
            try
            {
                var selectedDevices = devicesList.Where(d => d.IsSelected).ToList();
                if (selectedDevices.Count == 0)
                {
                    MessageBox.Show("Please select at least one output device in the checklist.", "No Devices Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                System.Console.WriteLine($"[Routing] Starting system audio loopback capture...");

                using (var enumerator = new MMDeviceEnumerator())
                {
                    var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    TxtCaptureDevice.Text = $"Capture: {defaultDevice.FriendlyName}";
                    System.Console.WriteLine($"[Routing] Captured Device: {defaultDevice.FriendlyName}");

                    // WASAPI Loopback captures the default playback output
                    capture = new WasapiLoopbackCapture(defaultDevice);
                }

                System.Console.WriteLine($"[Routing] Capture format: {capture.WaveFormat.SampleRate}Hz | {capture.WaveFormat.Channels}ch | {capture.WaveFormat.BitsPerSample}bits ({capture.WaveFormat.Encoding})");

                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;

                lock (routesLock)
                {
                    activeRoutes.Clear();
                    foreach (var item in selectedDevices)
                    {
                        // Safe check for potential feedback loop
                        if (item.IsDefault)
                        {
                            System.Console.WriteLine($"[Routing] Warning: feedback loop possible on '{item.Name}'");
                            var result = MessageBox.Show(
                                $"You have selected '{item.Name}' which is the default system output device.\n\n" +
                                "Routing system loopback audio back to the default output device will result in a feedback loop (echoes/screeching).\n\n" +
                                "Do you want to proceed anyway?",
                                "Warning: Audio Loop Feedback",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning
                            );

                            if (result == MessageBoxResult.No)
                            {
                                System.Console.WriteLine($"[Routing] Skipping default device '{item.Name}' to avoid feedback loop.");
                                item.IsSelected = false;
                                continue;
                            }
                        }

                        try
                        {
                            System.Console.WriteLine($"[Routing] Initializing route to: '{item.Name}' with delay={item.DelayMs}ms");
                            var route = new AudioRoute(item.Device, capture.WaveFormat);
                            route.SetDelay(item.DelayMs);
                            activeRoutes[item.Id] = route;
                        }
                        catch (Exception ex)
                        {
                            System.Console.WriteLine($"[Routing] Error initializing route to '{item.Name}': {ex.Message}");
                            MessageBox.Show($"Failed to initialize routing for {item.Name}: {ex.Message}", "Device Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            item.IsSelected = false;
                        }
                    }
                }

                // If all selected devices failed or user rejected feedback loop, abort start
                if (activeRoutes.Count == 0)
                {
                    System.Console.WriteLine($"[Routing] Aborting start: No active routing targets initialized.");
                    capture.Dispose();
                    capture = null;
                    RefreshDevicesList();
                    return;
                }

                capture.StartRecording();

                isRouting = true;
                BtnStartStop.Content = "Stop Routing";
                BtnStartStop.Background = (SolidColorBrush)FindResource("DangerColor");
                StatusDot.Background = (SolidColorBrush)FindResource("SuccessColor");
                StatusDot.ToolTip = "Active";
                System.Console.WriteLine($"[Routing] Loopback audio routing started successfully on {activeRoutes.Count} target(s).");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[Routing] Critical error during start: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Failed to start audio routing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StopRouting();
            }
        }

        private void StopRouting()
        {
            System.Console.WriteLine($"[Routing] Stopping audio routing...");
            isRouting = false;
            BtnStartStop.Content = "Start Routing";
            BtnStartStop.Background = (SolidColorBrush)FindResource("SuccessColor");
            StatusDot.Background = new SolidColorBrush(Color.FromRgb(75, 85, 99)); // Gray
            StatusDot.ToolTip = "Inactive";
            TxtCaptureDevice.Text = "Capture: Idle";

            if (capture != null)
            {
                capture.DataAvailable -= Capture_DataAvailable;
                capture.RecordingStopped -= Capture_RecordingStopped;
                try 
                { 
                    capture.StopRecording(); 
                    System.Console.WriteLine($"[Routing] Capture recording stopped.");
                } 
                catch (Exception ex) 
                {
                    System.Console.WriteLine($"[Routing] Warning during capture stop: {ex.Message}");
                }
                capture.Dispose();
                capture = null;
            }

            lock (routesLock)
            {
                int count = activeRoutes.Count;
                foreach (var route in activeRoutes.Values)
                {
                    try
                    {
                        System.Console.WriteLine($"[Routing] Disposing route to: '{route.TargetDevice.FriendlyName}'");
                        route.Dispose();
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[Routing] Error disposing route: {ex.Message}");
                    }
                }
                activeRoutes.Clear();
                System.Console.WriteLine($"[Routing] Routing stopped. Disposed {count} route(s).");
            }
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            // Replicate captured buffer to all active playback buffers
            lock (routesLock)
            {
                foreach (var route in activeRoutes.Values)
                {
                    route.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
            }

            // Real-time peak volume extraction from 32-bit float capture
            if (capture != null && capture.WaveFormat.BitsPerSample == 32 && capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                float max = 0f;
                int sampleCount = e.BytesRecorded / 4;
                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = BitConverter.ToSingle(e.Buffer, i * 4);
                    float absSample = Math.Abs(sample);
                    if (absSample > max) max = absSample;
                }

                // Quick attack, smooth decay
                if (max > currentPeakVolume)
                {
                    currentPeakVolume = max;
                }
                else
                {
                    currentPeakVolume = (currentPeakVolume * 0.92f) + (max * 0.08f);
                }
            }
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StopRouting();
                if (e.Exception != null)
                {
                    MessageBox.Show($"Audio capture stopped unexpectedly: {e.Exception.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is DeviceItem item)
            {
                item.IsSelected = cb.IsChecked ?? false;
                System.Console.WriteLine($"[Device Checklist] Checkbox changed: '{item.Name}' -> Selected: {item.IsSelected}");

                // Support live adding/removing of outputs without restarting loopback capture
                if (isRouting)
                {
                    if (item.IsSelected)
                    {
                        StartRoutingToDevice(item);
                    }
                    else
                    {
                        StopRoutingToDevice(item);
                    }
                }
            }
        }

        private void StartRoutingToDevice(DeviceItem item)
        {
            if (capture == null) return;

            lock (routesLock)
            {
                if (activeRoutes.ContainsKey(item.Id)) return;

                System.Console.WriteLine($"[Routing] Dynamically adding target device: '{item.Name}'");

                if (item.IsDefault)
                {
                    System.Console.WriteLine($"[Routing] Warning: dynamic feedback loop possible on '{item.Name}'");
                    var result = MessageBox.Show(
                        $"You have checked '{item.Name}' which is the default system output device.\n\n" +
                        "Routing system loopback audio back to the default output device will result in a feedback loop (echoes/screeching).\n\n" +
                        "Do you want to proceed anyway?",
                        "Warning: Audio Loop Feedback",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );

                    if (result == MessageBoxResult.No)
                    {
                        System.Console.WriteLine($"[Routing] Aborted dynamic routing to default device '{item.Name}' to prevent loop.");
                        item.IsSelected = false;
                        RefreshDevicesList();
                        return;
                    }
                }

                try
                {
                    System.Console.WriteLine($"[Routing] Initializing dynamic route to: '{item.Name}' with delay={item.DelayMs}ms");
                    var route = new AudioRoute(item.Device, capture.WaveFormat);
                    route.SetDelay(item.DelayMs);
                    activeRoutes[item.Id] = route;
                    System.Console.WriteLine($"[Routing] Dynamic route to '{item.Name}' started successfully.");
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Routing] Dynamic routing error to '{item.Name}': {ex.Message}");
                    MessageBox.Show($"Failed to route audio to {item.Name}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    item.IsSelected = false;
                    RefreshDevicesList();
                }
            }
        }

        private void StopRoutingToDevice(DeviceItem item)
        {
            lock (routesLock)
            {
                if (activeRoutes.TryGetValue(item.Id, out var route))
                {
                    System.Console.WriteLine($"[Routing] Dynamically removing target device: '{item.Name}'");
                    route.Dispose();
                    activeRoutes.Remove(item.Id);
                    System.Console.WriteLine($"[Routing] Dynamic route to '{item.Name}' stopped and removed.");
                }
            }
        }

        private void UiUpdateTimer_Tick(object sender, EventArgs e)
        {
            if (isRouting)
            {
                PrgVolume.Value = Math.Min(1.0, currentPeakVolume);
                TxtVolume.Text = $"{(int)(Math.Min(1.0, currentPeakVolume) * 100)}%";
            }
            else
            {
                currentPeakVolume *= 0.82f;
                if (currentPeakVolume < 0.01f) currentPeakVolume = 0f;
                PrgVolume.Value = currentPeakVolume;
                TxtVolume.Text = $"{(int)(currentPeakVolume * 100)}%";
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            StopRouting();
            uiUpdateTimer?.Stop();
            base.OnClosing(e);
        }
    }
}