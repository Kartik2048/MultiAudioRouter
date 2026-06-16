using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;

namespace MultiAudioRouter
{
    public enum ChannelIsolationMode
    {
        Stereo,
        LeftOnly,
        RightOnly
    }

    public enum CrossoverMode
    {
        FullRange,
        LowPass,
        HighPass
    }

    public partial class MainWindow : Window
    {
        // Device selection wrapper class
        public class DeviceItem : System.ComponentModel.INotifyPropertyChanged
        {
            private int delayMs;
            private int measuredLatencyMs;
            private ChannelIsolationMode isolationMode = ChannelIsolationMode.Stereo;
            private CrossoverMode crossoverMode = CrossoverMode.FullRange;

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

            public ChannelIsolationMode IsolationMode
            {
                get => isolationMode;
                set
                {
                    if (isolationMode != value)
                    {
                        isolationMode = value;
                        OnPropertyChanged(nameof(IsolationMode));
                        IsolationModeChangedCallback?.Invoke(Id, isolationMode);
                    }
                }
            }

            public CrossoverMode CrossoverMode
            {
                get => crossoverMode;
                set
                {
                    if (crossoverMode != value)
                    {
                        crossoverMode = value;
                        OnPropertyChanged(nameof(CrossoverMode));
                        CrossoverModeChangedCallback?.Invoke(Id, crossoverMode);
                    }
                }
            }

            public float Volume
            {
                get
                {
                    try
                    {
                        return Device?.AudioEndpointVolume?.MasterVolumeLevelScalar * 100f ?? 100f;
                    }
                    catch
                    {
                        return 100f;
                    }
                }
                set
                {
                    try
                    {
                        if (Device?.AudioEndpointVolume != null)
                        {
                            float currentVol = Device.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                            if (Math.Abs(currentVol - value) > 0.1f)
                            {
                                Device.AudioEndpointVolume.MasterVolumeLevelScalar = value / 100f;
                                OnPropertyChanged(nameof(Volume));
                                System.Console.WriteLine($"[Volume Control] Device: '{Name}' -> Hardware Volume set to: {value:F1}%");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[Volume Control] Error setting hardware volume for '{Name}': {ex.Message}");
                    }
                }
            }

            public string MeasuredLatencyText => $"Measured Latency: {measuredLatencyMs}ms";

            public Action<string, int> DelayChangedCallback { get; set; }
            public Action<string, ChannelIsolationMode> IsolationModeChangedCallback { get; set; }
            public Action<string, CrossoverMode> CrossoverModeChangedCallback { get; set; }

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
            public ISampleProvider Resampler { get; }
            public UnifiedDspProvider DspProvider { get; }
            public ChannelMatrixProvider MatrixProvider { get; }

            public AudioRoute(MMDevice targetDevice, WaveFormat captureFormat, ChannelIsolationMode initialIsolationMode, CrossoverMode initialCrossoverMode, float initialCrossoverFrequency, double initialDelayMs = 0)
            {
                TargetDevice = targetDevice;

                // Create input buffer with 500ms capacity to prevent stuttering while avoiding infinite lag buildup
                Buffer = new BufferedWaveProvider(captureFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromMilliseconds(500)
                };

                var targetFormat = targetDevice.AudioClient.MixFormat;

                // Convert Buffer (IWaveProvider) to ISampleProvider
                ISampleProvider sampleSource = Buffer.ToSampleProvider();

                // Create UnifiedDspProvider running at capture format sample rate (48000Hz)
                DspProvider = new UnifiedDspProvider(sampleSource, initialCrossoverMode, initialCrossoverFrequency, initialDelayMs);

                // Wrap DspProvider in ChannelMatrixProvider (ISampleProvider)
                MatrixProvider = new ChannelMatrixProvider(DspProvider);
                MatrixProvider.SetMode(initialIsolationMode);

                ISampleProvider finalSampleSource = MatrixProvider;

                // Dynamically resample using WdlResamplingSampleProvider if sample rates mismatch.
                // Placing the resampler at the very end of the routing pipeline keeps the DSP pipeline running at the
                // uniform capture rate of 48000Hz, resolving delay sync rounding/underrun bugs.
                if (captureFormat.SampleRate != targetFormat.SampleRate)
                {
                    var resampler = new WdlResamplingSampleProvider(finalSampleSource, targetFormat.SampleRate);
                    Resampler = resampler;
                    finalSampleSource = resampler;
                }
                else
                {
                    Resampler = null;
                }

                // Convert back to IWaveProvider for WasapiOut
                var finalWaveProvider = new SampleToWaveProvider(finalSampleSource);

                Player = new WasapiOut(targetDevice, AudioClientShareMode.Shared, true, 30);
                Player.Init(finalWaveProvider);
                Player.Play();
            }

            public void SetDelay(double milliseconds)
            {
                if (DspProvider != null)
                {
                    DspProvider.DelayMs = milliseconds;
                }
            }

            public void SetIsolationMode(ChannelIsolationMode mode)
            {
                MatrixProvider?.SetMode(mode);
            }

            public void SetCrossover(CrossoverMode mode, float frequency)
            {
                DspProvider?.SetCrossover(mode, frequency);
            }

            public void AddSamples(byte[] data, int offset, int count)
            {
                Buffer.AddSamples(data, offset, count);

                // Prevent buffer buildup to keep routing latency low and stable.
                // Since the Player buffer duration is 30ms, we want to maintain around 60-100ms in the buffer.
                // If the buffered duration exceeds 100ms, we discard the oldest samples to bring it back to 60ms.
                int bytesPerSecond = Buffer.WaveFormat.AverageBytesPerSecond;
                int maxBufferedBytes = (int)(0.100 * bytesPerSecond); // 100ms threshold
                int targetBufferedBytes = (int)(0.060 * bytesPerSecond); // 60ms target

                // Ensure block alignment
                int blockAlign = Buffer.WaveFormat.BlockAlign;
                maxBufferedBytes = (maxBufferedBytes / blockAlign) * blockAlign;
                targetBufferedBytes = (targetBufferedBytes / blockAlign) * blockAlign;

                if (Buffer.BufferedBytes > maxBufferedBytes)
                {
                    int bytesToDiscard = Buffer.BufferedBytes - targetBufferedBytes;
                    bytesToDiscard = (bytesToDiscard / blockAlign) * blockAlign;

                    if (bytesToDiscard > 0)
                    {
                        byte[] temp = new byte[bytesToDiscard];
                        Buffer.Read(temp, 0, bytesToDiscard);
                        System.Console.WriteLine($"[Routing Latency Control] Discarded {bytesToDiscard} bytes ({bytesToDiscard * 1000.0 / bytesPerSecond:F1}ms) to catch up from {Buffer.BufferedBytes * 1000.0 / bytesPerSecond:F1}ms.");
                    }
                }
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

        // Custom ISampleProvider that feeds background keep-alive noise and sweeps a chirp when triggered.
        // It triggers a callback when the first chirp sample is read.
        private class KeepAliveCalibrationProvider : ISampleProvider
        {
            private readonly WaveFormat format;
            private readonly float[] chirpSamples;
            private int chirpSampleIndex = 0;
            private bool chirpTriggered = false;
            private readonly object lockObject = new object();
            private Action onChirpStart;
            private readonly Random random = new Random();

            public WaveFormat WaveFormat => format;

            public KeepAliveCalibrationProvider(WaveFormat format, float[] chirpSamples)
            {
                this.format = format;
                this.chirpSamples = chirpSamples;
            }

            public void Trigger(Action onChirpStartCallback)
            {
                lock (lockObject)
                {
                    this.onChirpStart = onChirpStartCallback;
                    this.chirpTriggered = true;
                    this.chirpSampleIndex = 0;
                }
            }

            public int Read(float[] buffer, int offset, int count)
            {
                lock (lockObject)
                {
                    int channels = format.Channels;
                    for (int i = 0; i < count; i += channels)
                    {
                        // Generate keep-alive signal (White Noise at 0.01 amplitude)
                        float noise = (float)(random.NextDouble() * 2.0 - 1.0) * 0.01f;

                        float chirpVal = 0f;
                        if (chirpTriggered)
                        {
                            if (chirpSampleIndex == 0 && onChirpStart != null)
                            {
                                onChirpStart();
                                onChirpStart = null;
                            }

                            if (chirpSampleIndex < chirpSamples.Length)
                            {
                                chirpVal = chirpSamples[chirpSampleIndex];
                                chirpSampleIndex++;
                            }
                            else
                            {
                                chirpTriggered = false;
                            }
                        }

                        for (int c = 0; c < channels; c++)
                        {
                            if (offset + i + c < buffer.Length)
                            {
                                buffer[offset + i + c] = noise + chirpVal;
                            }
                        }
                    }
                    return count;
                }
            }
        }

        // Custom ISampleProvider for Left/Right stereo channel isolation
        public class ChannelMatrixProvider : ISampleProvider
        {
            private readonly ISampleProvider sourceProvider;
            private ChannelIsolationMode mode = ChannelIsolationMode.Stereo;

            public WaveFormat WaveFormat => sourceProvider.WaveFormat;

            public ChannelMatrixProvider(ISampleProvider sourceProvider)
            {
                this.sourceProvider = sourceProvider;
            }

            public void SetMode(ChannelIsolationMode mode)
            {
                this.mode = mode;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int samplesRead = sourceProvider.Read(buffer, offset, count);
                if (mode == ChannelIsolationMode.Stereo || WaveFormat.Channels < 2)
                {
                    return samplesRead;
                }

                int channels = WaveFormat.Channels;
                for (int i = 0; i < samplesRead; i += channels)
                {
                    if (mode == ChannelIsolationMode.LeftOnly)
                    {
                        float leftVal = buffer[offset + i];
                        buffer[offset + i + 1] = leftVal; // Duplicate Left to Right
                    }
                    else if (mode == ChannelIsolationMode.RightOnly)
                    {
                        float rightVal = buffer[offset + i + 1];
                        buffer[offset + i] = rightVal; // Duplicate Right to Left
                    }
                }
                return samplesRead;
            }
        }

        // Custom ISampleProvider that combines crossover filtering and simple queue-based delay
        public class UnifiedDspProvider : ISampleProvider
        {
            private readonly ISampleProvider _sourceProvider;
            private readonly BiQuadFilter _leftBiquad;
            private readonly BiQuadFilter _rightBiquad;
            private readonly Queue<float> _delayQueue = new Queue<float>();
            private readonly object _lockObject = new object();

            private CrossoverMode _currentMode;
            private float _currentFrequency;
            private bool _coefficientsNeedUpdate = false;
            private double _delayMs;

            public WaveFormat WaveFormat => _sourceProvider.WaveFormat;

            public double DelayMs
            {
                get
                {
                    lock (_lockObject)
                    {
                        return _delayMs;
                    }
                }
                set
                {
                    lock (_lockObject)
                    {
                        _delayMs = value;
                    }
                }
            }

            public UnifiedDspProvider(ISampleProvider sourceProvider, CrossoverMode initialMode, float initialFrequency, double initialDelayMs)
            {
                _sourceProvider = sourceProvider;
                _currentMode = initialMode;
                _currentFrequency = initialFrequency;
                _delayMs = initialDelayMs;

                // Instantiate two separate BiQuadFilter objects
                _leftBiquad = BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, initialFrequency, 0.707f);
                _rightBiquad = BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, initialFrequency, 0.707f);

                UpdateFilterCoefficients(initialMode, initialFrequency);
                LogDebug($"UnifiedDspProvider created: sampleRate={WaveFormat.SampleRate}, channels={WaveFormat.Channels}, mode={initialMode}, freq={initialFrequency}, delay={initialDelayMs}ms");
            }

            public void SetCrossover(CrossoverMode mode, float frequency)
            {
                lock (_lockObject)
                {
                    _currentMode = mode;
                    _currentFrequency = frequency;
                    _coefficientsNeedUpdate = true;
                    LogDebug($"UnifiedDspProvider.SetCrossover: mode={mode}, freq={frequency}");
                }
            }

            private void UpdateFilterCoefficients(CrossoverMode mode, float frequency)
            {
                // Clamp cutoff frequency to valid ranges: keep it below Nyquist limit and above sub-audible frequencies
                float cutoff = Math.Clamp(frequency, 20f, WaveFormat.SampleRate / 2.01f);

                if (mode == CrossoverMode.LowPass)
                {
                    _leftBiquad.SetLowPassFilter(WaveFormat.SampleRate, cutoff, 0.707f);
                    _rightBiquad.SetLowPassFilter(WaveFormat.SampleRate, cutoff, 0.707f);
                    LogDebug($"UpdateFilterCoefficients LowPass: sampleRate={WaveFormat.SampleRate}, cutoff={cutoff}");
                }
                else if (mode == CrossoverMode.HighPass)
                {
                    _leftBiquad.SetHighPassFilter(WaveFormat.SampleRate, cutoff, 0.707f);
                    _rightBiquad.SetHighPassFilter(WaveFormat.SampleRate, cutoff, 0.707f);
                    LogDebug($"UpdateFilterCoefficients HighPass: sampleRate={WaveFormat.SampleRate}, cutoff={cutoff}");
                }
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int samplesRead = _sourceProvider.Read(buffer, offset, count);

                CrossoverMode modeToUse;
                double delayMsToUse;

                lock (_lockObject)
                {
                    if (_coefficientsNeedUpdate)
                    {
                        UpdateFilterCoefficients(_currentMode, _currentFrequency);
                        _coefficientsNeedUpdate = false;
                    }
                    modeToUse = _currentMode;
                    delayMsToUse = _delayMs;
                }

                int channels = WaveFormat.Channels;
                int targetDelaySamples = (int)((delayMsToUse / 1000.0) * WaveFormat.SampleRate * channels);
                if (channels == 2 && targetDelaySamples % 2 != 0)
                {
                    targetDelaySamples++;
                }

                for (int n = 0; n < samplesRead; n += 2)
                {
                    if (n + 1 < samplesRead)
                    {
                        if (modeToUse == CrossoverMode.LowPass || modeToUse == CrossoverMode.HighPass)
                        {
                            buffer[offset + n] = _leftBiquad.Transform(buffer[offset + n]);
                            buffer[offset + n + 1] = _rightBiquad.Transform(buffer[offset + n + 1]);
                        }

                        _delayQueue.Enqueue(buffer[offset + n]);
                        _delayQueue.Enqueue(buffer[offset + n + 1]);

                        // Shrinking delay check: before writing/dequeuing, discard excess samples to drop delay instantly
                        while (_delayQueue.Count > targetDelaySamples + 2)
                        {
                            _delayQueue.Dequeue();
                        }

                        if (_delayQueue.Count > targetDelaySamples)
                        {
                            buffer[offset + n] = _delayQueue.Dequeue();
                        }
                        else
                        {
                            buffer[offset + n] = 0.0f;
                        }

                        if (_delayQueue.Count > targetDelaySamples)
                        {
                            buffer[offset + n + 1] = _delayQueue.Dequeue();
                        }
                        else
                        {
                            buffer[offset + n + 1] = 0.0f;
                        }
                    }
                    else
                    {
                        // Handle single remaining sample (in case samplesRead is odd, though rare for stereo)
                        if (modeToUse == CrossoverMode.LowPass || modeToUse == CrossoverMode.HighPass)
                        {
                            buffer[offset + n] = _leftBiquad.Transform(buffer[offset + n]);
                        }

                        _delayQueue.Enqueue(buffer[offset + n]);

                        while (_delayQueue.Count > targetDelaySamples + 1)
                        {
                            _delayQueue.Dequeue();
                        }

                        if (_delayQueue.Count > targetDelaySamples)
                        {
                            buffer[offset + n] = _delayQueue.Dequeue();
                        }
                        else
                        {
                            buffer[offset + n] = 0.0f;
                        }
                    }
                }

                return samplesRead;
            }
        }

        // Acoustic calibration DependencyProperty & fields
        public static readonly DependencyProperty DelaySlidersVisibilityProperty =
            DependencyProperty.Register("DelaySlidersVisibility", typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Visible));

        public Visibility DelaySlidersVisibility
        {
            get => (Visibility)GetValue(DelaySlidersVisibilityProperty);
            set => SetValue(DelaySlidersVisibilityProperty, value);
        }

        public List<DeviceItem> DevicesList => devicesList;
        public bool IsRoutingState => isRouting;

        private List<DeviceItem> devicesList = new List<DeviceItem>();
        private WasapiLoopbackCapture capture;
        private readonly Dictionary<string, AudioRoute> activeRoutes = new Dictionary<string, AudioRoute>();
        private readonly object routesLock = new object();
        private bool isRouting = false;

        private float currentPeakVolume = 0f;
        private readonly DispatcherTimer uiUpdateTimer;

        // Acoustic calibration error constants
        private const double MEASUREMENT_ERROR_GENERIC = -1.0;
        private const double MEASUREMENT_ERROR_LOW_SIGNAL = -2.0;
        private const double MEASUREMENT_ERROR_NEGATIVE_LATENCY = -3.0;
        private const double MEASUREMENT_ERROR_TIMEOUT = -4.0;
        private const int ROUTING_OVERHEAD_MS = 160;

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
            var previousIsolationModes = devicesList.ToDictionary(d => d.Id, d => d.IsolationMode);
            var previousCrossoverModes = devicesList.ToDictionary(d => d.Id, d => d.CrossoverMode);
            var previousMeasuredLatencies = devicesList.ToDictionary(d => d.Id, d => d.MeasuredLatencyMs);
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
                        ChannelIsolationMode prevMode = previousIsolationModes.TryGetValue(device.ID, out ChannelIsolationMode mVal) ? mVal : ChannelIsolationMode.Stereo;
                        CrossoverMode prevCrossover = previousCrossoverModes.TryGetValue(device.ID, out CrossoverMode cVal) ? cVal : CrossoverMode.FullRange;
                        int prevMeasured = previousMeasuredLatencies.TryGetValue(device.ID, out int lVal) ? lVal : 0;

                        var item = new DeviceItem
                        {
                            Id = device.ID,
                            Name = device.FriendlyName,
                            StatusText = status,
                            Device = device,
                            IsSelected = selectedIds.Contains(device.ID),
                            IsDefault = isDefault,
                            DelayMs = prevDelay,
                            IsolationMode = prevMode,
                            CrossoverMode = prevCrossover,
                            MeasuredLatencyMs = prevMeasured
                        };
                        item.DelayChangedCallback = OnDeviceDelayChanged;
                        item.IsolationModeChangedCallback = OnDeviceIsolationModeChanged;
                        item.CrossoverModeChangedCallback = OnDeviceCrossoverModeChanged;
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
 
        private void OnDeviceIsolationModeChanged(string deviceId, ChannelIsolationMode mode)
        {
            string devName = devicesList.FirstOrDefault(d => d.Id == deviceId)?.Name ?? deviceId;
            System.Console.WriteLine($"[Channel Isolation] Set isolation mode for '{devName}' to {mode}");
            lock (routesLock)
            {
                if (activeRoutes.TryGetValue(deviceId, out var route))
                {
                    route.SetIsolationMode(mode);
                }
            }
        }

        private static readonly object logLock = new object();
        public static void LogDebug(string message)
        {
            try
            {
                lock (logLock)
                {
                    System.IO.File.AppendAllText(@"c:\Users\karti\Documents\MultiAudioRouter\crossover_debug.log", 
                        $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
                }
            }
            catch { }
        }

        private float globalCrossoverFrequency = 80f;

        private void OnDeviceCrossoverModeChanged(string deviceId, CrossoverMode mode)
        {
            string devName = devicesList.FirstOrDefault(d => d.Id == deviceId)?.Name ?? deviceId;
            string msg = $"[Crossover Network] Set crossover mode for '{devName}' to {mode}";
            System.Console.WriteLine(msg);
            LogDebug(msg);
            lock (routesLock)
            {
                if (activeRoutes.TryGetValue(deviceId, out var route))
                {
                    route.SetCrossover(mode, globalCrossoverFrequency);
                }
                else
                {
                    LogDebug($"OnDeviceCrossoverModeChanged: activeRoutes does not contain route for {deviceId}");
                }
            }
        }

        private void CrossoverComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is DeviceItem item && cb.SelectedValue is CrossoverMode mode)
            {
                item.CrossoverMode = mode;
            }
        }

        private void IsolationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is DeviceItem item && cb.SelectedValue is ChannelIsolationMode mode)
            {
                item.IsolationMode = mode;
            }
        }

        private void SldGlobalCrossover_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            globalCrossoverFrequency = (float)e.NewValue;
            if (devicesList == null) return;
 
            string msg = $"[Crossover Network] Global Crossover frequency updated to {globalCrossoverFrequency:F0} Hz";
            System.Console.WriteLine(msg);
            LogDebug(msg);
 
            lock (routesLock)
            {
                foreach (var kvp in activeRoutes)
                {
                    string deviceId = kvp.Key;
                    var route = kvp.Value;
                    var deviceItem = devicesList.FirstOrDefault(d => d.Id == deviceId);
                    if (deviceItem != null)
                    {
                        route.SetCrossover(deviceItem.CrossoverMode, globalCrossoverFrequency);
                    }
                }
            }
        }

        private float[] GenerateLogChirp(int sampleRate, double durationSeconds, double f0, double f1)
        {
            int sampleCount = (int)(sampleRate * durationSeconds);
            float[] chirp = new float[sampleCount];
            double logFreqRatio = Math.Log(f1 / f0);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / sampleRate;
                double phase = 2.0 * Math.PI * f0 * (durationSeconds / logFreqRatio) * (Math.Exp(t * logFreqRatio / durationSeconds) - 1.0);
                float value = (float)Math.Sin(phase);

                // Apply a 5ms linear fade envelope to prevent click artifacts
                double fadeDuration = 0.005;
                if (t < fadeDuration)
                {
                    value *= (float)(t / fadeDuration);
                }
                else if (t > durationSeconds - fadeDuration)
                {
                    value *= (float)((durationSeconds - t) / fadeDuration);
                }

                chirp[i] = value * 0.8f; // 80% amplitude
            }
            return chirp;
        }

        private bool IsFormatFloat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
            if (format is WaveFormatExtensible ext)
            {
                if (ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
                {
                    return true;
                }
            }
            if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.Extensible)
            {
                return true;
            }
            return false;
        }

        private byte[] ConvertFloatToActiveWaveFormat(float[] floatSamples, WaveFormat format)
        {
            int channels = format.Channels;
            int totalBytes = floatSamples.Length * channels * (format.BitsPerSample / 8);
            byte[] bytes = new byte[totalBytes];
            int bytesPerSample = format.BitsPerSample / 8;
            bool isFloat = IsFormatFloat(format);

            for (int i = 0; i < floatSamples.Length; i++)
            {
                float val = floatSamples[i];

                for (int channel = 0; channel < channels; channel++)
                {
                    int offset = (i * channels + channel) * bytesPerSample;
                    if (format.BitsPerSample == 32 && isFloat)
                    {
                        byte[] sampleBytes = BitConverter.GetBytes(val);
                        Array.Copy(sampleBytes, 0, bytes, offset, 4);
                    }
                    else if (format.BitsPerSample == 16)
                    {
                        short shortVal = (short)(val * 32767f);
                        byte[] sampleBytes = BitConverter.GetBytes(shortVal);
                        Array.Copy(sampleBytes, 0, bytes, offset, 2);
                    }
                    else if (format.BitsPerSample == 24)
                    {
                        int intVal = (int)(val * 8388607f);
                        byte[] sampleBytes = BitConverter.GetBytes(intVal);
                        Array.Copy(sampleBytes, 0, bytes, offset, 3);
                    }
                }
            }

            return bytes;
        }

        private float[] ParseCaptureBuffer(byte[] rawBuffer, int bytesRecorded, WaveFormat format)
        {
            int bytesPerSample = format.BitsPerSample / 8;
            int channels = format.Channels;
            int totalSamples = bytesRecorded / (bytesPerSample * channels);
            float[] monoSamples = new float[totalSamples];
            bool isFloat = IsFormatFloat(format);

            for (int i = 0; i < totalSamples; i++)
            {
                float sum = 0f;
                for (int channel = 0; channel < channels; channel++)
                {
                    int offset = (i * channels + channel) * bytesPerSample;
                    float sampleValue = 0f;

                    if (format.BitsPerSample == 32 && isFloat)
                    {
                        sampleValue = BitConverter.ToSingle(rawBuffer, offset);
                    }
                    else if (format.BitsPerSample == 16)
                    {
                        short shortVal = BitConverter.ToInt16(rawBuffer, offset);
                        sampleValue = shortVal / 32768f;
                    }
                    else if (format.BitsPerSample == 24)
                    {
                        int intVal = (rawBuffer[offset + 2] << 16) | (rawBuffer[offset + 1] << 8) | rawBuffer[offset];
                        if ((intVal & 0x800000) != 0) intVal |= unchecked((int)0xff000000);
                        sampleValue = intVal / 8388608f;
                    }
                    sum += sampleValue;
                }
                monoSamples[i] = sum / channels;
            }

            return monoSamples;
        }

        private int RunCrossCorrelation(float[] reference, float[] recorded, out double peakToAverageRatio, int captureSampleRate = 0)
        {
            int M = reference.Length;
            int N = recorded.Length;
            int maxLag = N - M;

            // 1. Subtract mean from reference to remove DC offset
            float refSum = 0f;
            for (int i = 0; i < M; i++) refSum += reference[i];
            float refMean = refSum / M;
            float[] refNorm = new float[M];
            for (int i = 0; i < M; i++) refNorm[i] = reference[i] - refMean;

            // 2. Subtract mean from recorded to remove DC offset
            float recSum = 0f;
            for (int i = 0; i < N; i++) recSum += recorded[i];
            float recMean = recSum / N;
            float[] recNorm = new float[N];
            for (int i = 0; i < N; i++) recNorm[i] = recorded[i] - recMean;

            // 3. Compute cross-correlation in parallel
            float[] correlation = new float[maxLag];
            System.Threading.Tasks.Parallel.For(0, maxLag, n =>
            {
                float sum = 0f;
                for (int m = 0; m < M; m++)
                {
                    sum += refNorm[m] * recNorm[n + m];
                }
                correlation[n] = sum;
            });

            // 4. Find peak and compute Peak-to-Average Ratio
            float maxVal = float.MinValue;
            int maxIndex = 0;
            float sumAbs = 0f;

            for (int n = 0; n < maxLag; n++)
            {
                float absVal = Math.Abs(correlation[n]);
                sumAbs += absVal;
                if (absVal > maxVal)
                {
                    maxVal = absVal;
                    maxIndex = n;
                }
            }

            float avgVal = sumAbs / maxLag;
            peakToAverageRatio = avgVal > 0 ? (maxVal / avgVal) : 0;

            // 5. Diagnostic logging — emitted to both the debug output and the attached terminal console
            double calculatedLatencyMs = captureSampleRate > 0
                ? (double)maxIndex / captureSampleRate * 1000.0
                : double.NaN;

            string dspLog1 = $"[DSP] Peak Correlation Score: {maxVal:F4}";
            string dspLog2 = $"[DSP] Raw Sample Index: {maxIndex}";
            string dspLog3 = $"[DSP] Calculated Latency (ms): {calculatedLatencyMs:F2}";

            System.Diagnostics.Debug.WriteLine(dspLog1);
            System.Diagnostics.Debug.WriteLine(dspLog2);
            System.Diagnostics.Debug.WriteLine(dspLog3);
            System.Console.WriteLine(dspLog1);
            System.Console.WriteLine(dspLog2);
            System.Console.WriteLine(dspLog3);

            return maxIndex;
        }

        private Tuple<int, int> FindDualPeaks(float[] reference, float[] recorded, int sampleRate)
        {
            int M = reference.Length;
            int N = recorded.Length;
            int maxLag = N - M;
            if (maxLag <= 0)
            {
                return Tuple.Create(0, 0);
            }

            // 1. Subtract mean from reference to remove DC offset
            float refSum = 0f;
            for (int i = 0; i < M; i++) refSum += reference[i];
            float refMean = refSum / M;
            float[] refNorm = new float[M];
            for (int i = 0; i < M; i++) refNorm[i] = reference[i] - refMean;

            // 2. Subtract mean from recorded to remove DC offset
            float recSum = 0f;
            for (int i = 0; i < N; i++) recSum += recorded[i];
            float recMean = recSum / N;
            float[] recNorm = new float[N];
            for (int i = 0; i < N; i++) recNorm[i] = recorded[i] - recMean;

            // 3. Compute cross-correlation in parallel
            float[] correlation = new float[maxLag];
            System.Threading.Tasks.Parallel.For(0, maxLag, n =>
            {
                float sum = 0f;
                for (int m = 0; m < M; m++)
                {
                    sum += refNorm[m] * recNorm[n + m];
                }
                correlation[n] = sum;
            });

            // 4. Find the first peak
            float maxVal1 = float.MinValue;
            int peak1Index = 0;
            for (int n = 0; n < maxLag; n++)
            {
                float absVal = Math.Abs(correlation[n]);
                if (absVal > maxVal1)
                {
                    maxVal1 = absVal;
                    peak1Index = n;
                }
            }

            // 5. Blanking Window: To prevent finding the same chirp twice, clear or zero out correlation scores in 200ms window surrounding peak1Index
            int blankingSamples = (int)(0.200 * sampleRate);
            int startBlank = Math.Max(0, peak1Index - blankingSamples / 2);
            int endBlank = Math.Min(maxLag - 1, peak1Index + blankingSamples / 2);
            for (int n = startBlank; n <= endBlank; n++)
            {
                correlation[n] = 0f;
            }

            // 6. Find the second peak
            float maxVal2 = float.MinValue;
            int peak2Index = 0;
            for (int n = 0; n < maxLag; n++)
            {
                float absVal = Math.Abs(correlation[n]);
                if (absVal > maxVal2)
                {
                    maxVal2 = absVal;
                    peak2Index = n;
                }
            }

            return Tuple.Create(peak1Index, peak2Index);
        }

        public async Task<Tuple<double, double>> RunIsolatedCalibrationAsync(
            MMDevice micDevice,
            MMDevice refDevice,
            MMDevice targetDevice,
            IProgress<string> progress)
        {
            if (!isRouting)
            {
                throw new InvalidOperationException("Live audio routing must be active to perform calibration. Please start routing first.");
            }

            AudioRoute refRoute = null;
            AudioRoute targetRoute = null;

            lock (routesLock)
            {
                activeRoutes.TryGetValue(refDevice.ID, out refRoute);
                activeRoutes.TryGetValue(targetDevice.ID, out targetRoute);
            }

            var result = await MeasureLiveLatencyAsync(micDevice, refDevice, refRoute, targetDevice, targetRoute, progress);
            return result;
        }

        private async Task<Tuple<double, double>> MeasureLiveLatencyAsync(
            MMDevice micDevice,
            MMDevice refDevice,
            AudioRoute refRoute,
            MMDevice targetDevice,
            AudioRoute targetRoute,
            IProgress<string> progress)
        {
            int micSampleRate = micDevice.AudioClient.MixFormat.SampleRate;

            double chirpDuration = 0.250; // 250ms
            double f0 = 500.0;
            double f1 = Math.Min(8000.0, micSampleRate * 0.45);

            progress.Report("Generating reference signals...");

            // Generate reference chirp (microphone sample rate)
            float[] referenceChirp = GenerateLogChirp(micSampleRate, chirpDuration, f0, f1);

            // Determine sample rates for Reference and Target playback
            MMDevice defaultPlayDevice;
            using (var enumerator = new MMDeviceEnumerator())
            {
                defaultPlayDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            }
            if (defaultPlayDevice == null)
            {
                throw new InvalidOperationException("No default playback device found.");
            }
            int refSampleRate = defaultPlayDevice.AudioClient.MixFormat.SampleRate;
            int targetSampleRate = targetRoute != null ? targetRoute.DspProvider.WaveFormat.SampleRate : targetDevice.AudioClient.MixFormat.SampleRate;

            float[] refPlayChirp = GenerateLogChirp(refSampleRate, chirpDuration, f0, f1);

            double baseMasterDelayMs = 0;
            double baseTargetDelayMs = 0;

            if (refRoute != null)
            {
                baseMasterDelayMs = refRoute.DspProvider.DelayMs;
            }
            if (targetRoute != null)
            {
                baseTargetDelayMs = targetRoute.DspProvider.DelayMs;
            }

            double relativeDelayMs = baseTargetDelayMs - baseMasterDelayMs;

            // Apply target spacer
            if (targetRoute != null)
            {
                targetRoute.SetDelay(baseTargetDelayMs + 500.0);
                var targetItem = devicesList.FirstOrDefault(d => d.Id == targetDevice.ID);
                if (targetItem != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        targetItem.DelayMs = (int)Math.Round(baseTargetDelayMs + 500.0);
                    });
                }
            }
            if (refRoute != null)
            {
                refRoute.SetDelay(baseMasterDelayMs);
                var refItem = devicesList.FirstOrDefault(d => d.Id == refDevice.ID);
                if (refItem != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        refItem.DelayMs = (int)Math.Round(baseMasterDelayMs);
                    });
                }
            }

            // Start the continuous keep-alive playback before the loop
            using (var refPlayer = new WasapiOut(defaultPlayDevice, AudioClientShareMode.Shared, true, 50))
            {
                var refFormat = WaveFormat.CreateIeeeFloatWaveFormat(refSampleRate, defaultPlayDevice.AudioClient.MixFormat.Channels);
                var refProvider = new IsolatedCalibrationProvider(refFormat, refPlayChirp);
                refPlayer.Init(new SampleToWaveProvider(refProvider));
                refPlayer.Play();

                // Wait 2000ms to allow audio buffer to stretch and stabilize (with continuous hum playing)
                await Task.Delay(2000);

                for (int iteration = 1; iteration <= 3; iteration++)
                {
                    progress.Report($"[Calibration Loop] Starting iteration {iteration}...");

                    var recordedSamples = new List<float>();
                    var recordLock = new object();

                    using (var capture = new WasapiCapture(micDevice))
                    {
                        capture.DataAvailable += (s, e) =>
                        {
                            float[] parsed = ParseCaptureBuffer(e.Buffer, e.BytesRecorded, capture.WaveFormat);
                            lock (recordLock)
                            {
                                recordedSamples.AddRange(parsed);
                            }
                        };

                        capture.StartRecording();

                        // Wait 300ms to ensure the WASAPI capture stream is fully active and recording
                        await Task.Delay(300);

                        // Trigger the chirp on the continuous player
                        refProvider.Trigger(null);

                        // Wait 2000ms for Master and routed Target sounds to clear the room and reach mic
                        await Task.Delay(2000);

                        try { capture.StopRecording(); } catch { }
                    }

                    // Process recording array on background thread pool worker
                    float[] recordedArray;
                    lock (recordLock)
                    {
                        recordedArray = recordedSamples.ToArray();
                    }

                    double measuredGapMs = 0;
                    await Task.Run(() =>
                    {
                        var hpFilter = BiQuadFilter.HighPassFilter(micSampleRate, (float)f0, 0.707f);
                        var lpFilter = BiQuadFilter.LowPassFilter(micSampleRate, (float)f1, 0.707f);
                        float[] filteredRecorded = new float[recordedArray.Length];
                        for (int i = 0; i < recordedArray.Length; i++)
                        {
                            float sample = hpFilter.Transform(recordedArray[i]);
                            filteredRecorded[i] = lpFilter.Transform(sample);
                        }

                        var peaks = FindDualPeaks(referenceChirp, filteredRecorded, micSampleRate);
                        int Peak1_SampleIndex = Math.Min(peaks.Item1, peaks.Item2);
                        int Peak2_SampleIndex = Math.Max(peaks.Item1, peaks.Item2);

                        measuredGapMs = ((double)(Peak2_SampleIndex - Peak1_SampleIndex) / micSampleRate) * 1000.0;
                    });

                    // Calculate the real inherent pipeline error
                    double errorMs = measuredGapMs - 500.0;

                    // Log diagnostic data for each pass
                    string passLog = $"[Calibration Loop] Pass {iteration} - Measured Gap: {measuredGapMs:F2}ms, Residual Error: {errorMs:F2}ms";
                    LogDebug(passLog);
                    System.Console.WriteLine(passLog);
                    progress.Report(passLog);

                    // Convergence threshold is 2.0ms
                    if (Math.Abs(errorMs) <= 2.0)
                    {
                        string lockLog = $"[Calibration Loop] Successful lock achieved on pass {iteration}.";
                        LogDebug(lockLog);
                        System.Console.WriteLine(lockLog);
                        progress.Report(lockLog);
                        break;
                    }

                    // Map relativeDelayMs back to baseMasterDelayMs and baseTargetDelayMs
                    if (refRoute != null)
                    {
                        // Case A: Reference speaker is routed, so delays are actively applied.
                        // We accumulate the residual error to converge.
                        relativeDelayMs -= errorMs;

                        if (relativeDelayMs >= 0)
                        {
                            baseTargetDelayMs = relativeDelayMs;
                            baseMasterDelayMs = 0.0;
                        }
                        else
                        {
                            baseTargetDelayMs = 0.0;
                            baseMasterDelayMs = -relativeDelayMs;
                        }
                    }
                    else
                    {
                        // Case B: Reference speaker is the default device and NOT routed.
                        // Its delay is physically always 0.0ms, so we cannot accumulate delay.
                        // The actual required target delay difference is (baseTargetDelayMs - errorMs).
                        double requiredDiff = baseTargetDelayMs - errorMs;

                        if (requiredDiff >= 0)
                        {
                            baseTargetDelayMs = requiredDiff;
                            baseMasterDelayMs = 0.0;
                            relativeDelayMs = baseTargetDelayMs;
                        }
                        else
                        {
                            baseTargetDelayMs = 0.0;
                            baseMasterDelayMs = -requiredDiff;
                            relativeDelayMs = -baseMasterDelayMs;

                            string warningMsg = $"[Calibration Warning] Reference speaker '{refDevice.FriendlyName}' is faster by {-requiredDiff:F1}ms but is not currently routed. " +
                                                $"To sync them, either: (1) Set the slower speaker '{targetDevice.FriendlyName}' as the Windows Default Playback Device and route the faster one, or " +
                                                $"(2) Check both speakers in the checklist (requires setting a dummy device as Windows default to avoid feedback).";
                            LogDebug(warningMsg);
                            System.Console.WriteLine(warningMsg);
                            progress.Report(warningMsg);
                        }
                    }

                    // Apply updated delays
                    if (targetRoute != null)
                    {
                        targetRoute.SetDelay(baseTargetDelayMs + 500.0);
                        var targetItem = devicesList.FirstOrDefault(d => d.Id == targetDevice.ID);
                        if (targetItem != null)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                targetItem.DelayMs = (int)Math.Round(baseTargetDelayMs + 500.0);
                            });
                        }
                    }
                    if (refRoute != null)
                    {
                        refRoute.SetDelay(baseMasterDelayMs);
                        var refItem = devicesList.FirstOrDefault(d => d.Id == refDevice.ID);
                        if (refItem != null)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                refItem.DelayMs = (int)Math.Round(baseMasterDelayMs);
                            });
                        }
                    }

                    // Await 1500ms before starting next iteration
                    await Task.Delay(1500);
                }

                refPlayer.Stop();
            }

            // Teardown: restore delays strictly to base values without spacer
            if (targetRoute != null)
            {
                targetRoute.SetDelay(baseTargetDelayMs);
                var targetItem = devicesList.FirstOrDefault(d => d.Id == targetDevice.ID);
                if (targetItem != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        targetItem.DelayMs = (int)Math.Round(baseTargetDelayMs);
                    });
                }
            }
            if (refRoute != null)
            {
                refRoute.SetDelay(baseMasterDelayMs);
                var refItem = devicesList.FirstOrDefault(d => d.Id == refDevice.ID);
                if (refItem != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        refItem.DelayMs = (int)Math.Round(baseMasterDelayMs);
                    });
                }
            }

            return Tuple.Create(baseTargetDelayMs, baseMasterDelayMs);
        }

        public void ApplyCalibrationResult(string referenceDeviceId, string targetDeviceId, double targetDelayMs, double referenceDelayMs)
        {
            var refItem = devicesList.FirstOrDefault(d => d.Id == referenceDeviceId);
            var targetItem = devicesList.FirstOrDefault(d => d.Id == targetDeviceId);

            if (refItem != null && targetItem != null)
            {
                refItem.DelayMs = (int)Math.Round(referenceDelayMs);
                targetItem.DelayMs = (int)Math.Round(targetDelayMs);

                // Set meaningful measured relative latencies:
                // The faster device has relative latency 0ms, the slower device has its delay value as latency
                if (referenceDelayMs > 0)
                {
                    refItem.MeasuredLatencyMs = (int)Math.Round(referenceDelayMs);
                    targetItem.MeasuredLatencyMs = 0;
                }
                else
                {
                    refItem.MeasuredLatencyMs = 0;
                    targetItem.MeasuredLatencyMs = (int)Math.Round(targetDelayMs);
                }

                LogDebug($"[Isolated Calibration] Applied sync result: Reference '{refItem.Name}' delay={refItem.DelayMs}ms (latency={refItem.MeasuredLatencyMs}ms), Target '{targetItem.Name}' delay={targetItem.DelayMs}ms (latency={targetItem.MeasuredLatencyMs}ms)");
            }
        }

        public bool IsDeviceRouted(string deviceId)
        {
            lock (routesLock)
            {
                return activeRoutes.ContainsKey(deviceId);
            }
        }

        private void BtnAutoSync_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CalibrationSetupView(this);
            dialog.Owner = this;
            dialog.ShowDialog();
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

        public void StartRouting()
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
                                $"You have selected '{item.Name}', which is currently the default system playback device.\n\n" +
                                "Routing the loopback capture back to the default output device will cause an infinite feedback loop (echoes/screeching).\n\n" +
                                "To apply delay and crossover processing to BOTH your main speakers and headphones:\n" +
                                "1. Set the Windows default playback device to another output (such as built-in 'Realtek Audio' laptop speakers, or a virtual cable).\n" +
                                "2. Mute or turn down the volume of that dummy/default device physically so you don't hear it.\n" +
                                "3. In this app, check BOTH your main speakers and headphones in the checklist so both are routed.\n\n" +
                                "Do you want to proceed with routing back to the default device anyway?",
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
                            System.Console.WriteLine($"[Routing] Initializing route to: '{item.Name}' with delay={item.DelayMs}ms, isolation={item.IsolationMode}, crossover={item.CrossoverMode} ({globalCrossoverFrequency}Hz)");
                            var route = new AudioRoute(item.Device, capture.WaveFormat, item.IsolationMode, item.CrossoverMode, globalCrossoverFrequency, item.DelayMs);
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

        public void StopRouting()
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
                        $"You have checked '{item.Name}', which is currently the default system playback device.\n\n" +
                        "Routing the loopback capture back to the default output device will cause an infinite feedback loop (echoes/screeching).\n\n" +
                        "To apply delay and crossover processing to BOTH your main speakers and headphones:\n" +
                        "1. Set the Windows default playback device to another output (such as built-in 'Realtek Audio' laptop speakers, or a virtual cable).\n" +
                        "2. Mute or turn down the volume of that dummy/default device physically so you don't hear it.\n" +
                        "3. In this app, check BOTH your main speakers and headphones in the checklist so both are routed.\n\n" +
                        "Do you want to proceed with routing back to the default device anyway?",
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
                    System.Console.WriteLine($"[Routing] Initializing dynamic route to: '{item.Name}' with delay={item.DelayMs}ms, isolation={item.IsolationMode}, crossover={item.CrossoverMode} ({globalCrossoverFrequency}Hz)");
                    var route = new AudioRoute(item.Device, capture.WaveFormat, item.IsolationMode, item.CrossoverMode, globalCrossoverFrequency, item.DelayMs);
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

    public class IsolatedCalibrationProvider : ISampleProvider
    {
        private readonly WaveFormat waveFormat;
        private readonly float[] chirpSamples;
        private readonly double sineFrequency = 50.0;
        private readonly double sineAmplitude = 0.01;
        private double sinePhase = 0.0;

        private int chirpSampleIndex = -1;
        private readonly object triggerLock = new object();
        private Action onChirpStart;

        public IsolatedCalibrationProvider(WaveFormat format, float[] chirp)
        {
            this.waveFormat = format;
            this.chirpSamples = chirp;
        }

        public WaveFormat WaveFormat => waveFormat;

        public void Trigger(Action callback)
        {
            lock (triggerLock)
            {
                onChirpStart = callback;
                chirpSampleIndex = 0;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = waveFormat.Channels;
            int frames = count / channels;

            double sampleRate = waveFormat.SampleRate;
            double phaseIncrement = 2.0 * Math.PI * sineFrequency / sampleRate;

            for (int f = 0; f < frames; f++)
            {
                // Generate 50Hz hum
                float sineSample = (float)(sineAmplitude * Math.Sin(sinePhase));
                sinePhase += phaseIncrement;
                if (sinePhase >= 2.0 * Math.PI)
                {
                    sinePhase -= 2.0 * Math.PI;
                }

                // Check and mix chirp
                float chirpVal = 0.0f;
                bool triggerNow = false;

                lock (triggerLock)
                {
                    if (chirpSampleIndex >= 0)
                    {
                        if (chirpSampleIndex == 0)
                        {
                            triggerNow = true;
                        }

                        if (chirpSampleIndex < chirpSamples.Length)
                        {
                            chirpVal = chirpSamples[chirpSampleIndex];
                            chirpSampleIndex++;
                        }
                        else
                        {
                            chirpSampleIndex = -1;
                        }
                    }
                }

                if (triggerNow)
                {
                    onChirpStart?.Invoke();
                    onChirpStart = null;
                }

                float mixedSample = sineSample + chirpVal;
                // Clamp to prevent clipping
                if (mixedSample > 1.0f) mixedSample = 1.0f;
                else if (mixedSample < -1.0f) mixedSample = -1.0f;

                for (int c = 0; c < channels; c++)
                {
                    buffer[offset + f * channels + c] = mixedSample;
                }
            }

            return count;
        }
    }

    public class ChirpInjectionProvider : ISampleProvider
    {
        private readonly ISampleProvider _sourceProvider;
        private float[] _chirpData;
        private int _chirpIndex = -1;
        private Action _onStart;
        private readonly object _lock = new object();

        public ChirpInjectionProvider(ISampleProvider sourceProvider)
        {
            _sourceProvider = sourceProvider;
        }

        public WaveFormat WaveFormat => _sourceProvider.WaveFormat;

        public void TriggerChirp(float[] chirpData, Action onStart)
        {
            lock (_lock)
            {
                _chirpData = chirpData;
                _chirpIndex = 0;
                _onStart = onStart;
            }
        }

        public void CancelChirp()
        {
            lock (_lock)
            {
                _chirpData = null;
                _chirpIndex = -1;
                _onStart = null;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _sourceProvider.Read(buffer, offset, count);

            lock (_lock)
            {
                if (_chirpData != null && _chirpIndex >= 0)
                {
                    int channels = WaveFormat.Channels;
                    int framesRead = samplesRead / channels;

                    if (_chirpIndex == 0 && _onStart != null)
                    {
                        _onStart.Invoke();
                        _onStart = null;
                    }

                    for (int f = 0; f < framesRead; f++)
                    {
                        if (_chirpIndex < _chirpData.Length)
                        {
                            float chirpVal = _chirpData[_chirpIndex];

                            for (int c = 0; c < channels; c++)
                            {
                                int idx = offset + f * channels + c;
                                // Duck the live audio to 15% volume, then mix the chirp
                                float mixed = (buffer[idx] * 0.15f) + chirpVal;

                                // Clamp to prevent clipping
                                if (mixed > 1.0f) mixed = 1.0f;
                                else if (mixed < -1.0f) mixed = -1.0f;

                                buffer[idx] = mixed;
                            }
                            _chirpIndex++;
                        }
                        else
                        {
                            _chirpIndex = -1;
                            _chirpData = null;
                            break;
                        }
                    }
                }
            }

            return samplesRead;
        }
    }
}
