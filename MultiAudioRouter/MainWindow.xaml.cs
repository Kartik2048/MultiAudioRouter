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
        public class DeviceItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string StatusText { get; set; }
            public MMDevice Device { get; set; }
            public bool IsSelected { get; set; }
            public bool IsDefault { get; set; }
        }

        // Active routing player structure
        private class AudioRoute : IDisposable
        {
            public MMDevice TargetDevice { get; }
            public WasapiOut Player { get; }
            public BufferedWaveProvider Buffer { get; }
            public IWaveProvider Resampler { get; }

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

                if (sampleRatesMatch)
                {
                    Resampler = null;
                    Player = new WasapiOut(targetDevice, AudioClientShareMode.Shared, true, 100);
                    Player.Init(Buffer);
                }
                else
                {
                    // Dynamically resample to target device's native mix format
                    var resampler = new MediaFoundationResampler(Buffer, targetFormat)
                    {
                        ResamplerQuality = 60
                    };
                    Resampler = resampler;

                    Player = new WasapiOut(targetDevice, AudioClientShareMode.Shared, true, 100);
                    Player.Init(resampler);
                }

                Player.Play();
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

                        devicesList.Add(new DeviceItem
                        {
                            Id = device.ID,
                            Name = device.FriendlyName,
                            StatusText = status,
                            Device = device,
                            IsSelected = selectedIds.Contains(device.ID),
                            IsDefault = isDefault
                        });
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

                using (var enumerator = new MMDeviceEnumerator())
                {
                    var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    TxtCaptureDevice.Text = $"Capture: {defaultDevice.FriendlyName}";

                    // WASAPI Loopback captures the default playback output
                    capture = new WasapiLoopbackCapture(defaultDevice);
                }

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
                                item.IsSelected = false;
                                continue;
                            }
                        }

                        try
                        {
                            var route = new AudioRoute(item.Device, capture.WaveFormat);
                            activeRoutes[item.Id] = route;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to initialize routing for {item.Name}: {ex.Message}", "Device Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            item.IsSelected = false;
                        }
                    }
                }

                // If all selected devices failed or user rejected feedback loop, abort start
                if (activeRoutes.Count == 0)
                {
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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start audio routing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StopRouting();
            }
        }

        private void StopRouting()
        {
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
                try { capture.StopRecording(); } catch { }
                capture.Dispose();
                capture = null;
            }

            lock (routesLock)
            {
                foreach (var route in activeRoutes.Values)
                {
                    route.Dispose();
                }
                activeRoutes.Clear();
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

                if (item.IsDefault)
                {
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
                        item.IsSelected = false;
                        RefreshDevicesList();
                        return;
                    }
                }

                try
                {
                    var route = new AudioRoute(item.Device, capture.WaveFormat);
                    activeRoutes[item.Id] = route;
                }
                catch (Exception ex)
                {
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
                    route.Dispose();
                    activeRoutes.Remove(item.Id);
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