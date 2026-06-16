using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using NAudio.CoreAudioApi;

namespace MultiAudioRouter
{
    public partial class CalibrationSetupView : Window
    {
        private readonly MainWindow _mainWindow;

        public class ComboDeviceItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public MMDevice Device { get; set; }
            public override string ToString() => Name;
        }

        public CalibrationSetupView(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            PopulateDevices();
        }

        private void PopulateDevices()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    // Populate Microphone
                    var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    var micList = new List<ComboDeviceItem>();
                    foreach (var device in captureDevices)
                    {
                        micList.Add(new ComboDeviceItem { Id = device.ID, Name = device.FriendlyName, Device = device });
                    }
                    CboMicrophone.ItemsSource = micList;
                    
                    // Try to select default communications or console mic if exists
                    if (micList.Count > 0)
                    {
                        MMDevice defaultMic = null;
                        try
                        {
                            defaultMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                        }
                        catch
                        {
                            try
                            {
                                defaultMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                            }
                            catch { }
                        }
                        var defaultMicItem = micList.FirstOrDefault(d => d.Id == defaultMic?.ID);
                        CboMicrophone.SelectedItem = defaultMicItem ?? micList[0];
                    }

                    // Populate Speakers
                    var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    var refList = new List<ComboDeviceItem>();
                    var targetList = new List<ComboDeviceItem>();

                    foreach (var device in renderDevices)
                    {
                        refList.Add(new ComboDeviceItem { Id = device.ID, Name = device.FriendlyName, Device = device });
                        targetList.Add(new ComboDeviceItem { Id = device.ID, Name = device.FriendlyName, Device = device });
                    }

                    CboReferenceSpeaker.ItemsSource = refList;
                    CboTargetSpeaker.ItemsSource = targetList;

                    var activeCheckedDevices = _mainWindow.DevicesList.Where(d => d.IsSelected).ToList();
                    if (activeCheckedDevices.Count >= 2)
                    {
                        // Select the first checked device as Reference, second as Target
                        var refItem = refList.FirstOrDefault(d => d.Id == activeCheckedDevices[0].Id);
                        var targetItem = targetList.FirstOrDefault(d => d.Id == activeCheckedDevices[1].Id);
                        
                        if (refItem != null) CboReferenceSpeaker.SelectedItem = refItem;
                        if (targetItem != null) CboTargetSpeaker.SelectedItem = targetItem;
                    }
                    else
                    {
                        // Fallback: Select default render device for Reference
                        try
                        {
                            var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                            if (defaultRender != null)
                            {
                                var refDefaultItem = refList.FirstOrDefault(d => d.Id == defaultRender.ID);
                                if (refDefaultItem != null) CboReferenceSpeaker.SelectedItem = refDefaultItem;
                            }
                        }
                        catch { }

                        // Setup target speaker: try to select a selected device from MainWindow list
                        var selectedRoutedDevice = activeCheckedDevices.FirstOrDefault();
                        if (selectedRoutedDevice != null)
                        {
                            var targetItem = targetList.FirstOrDefault(d => d.Id == selectedRoutedDevice.Id);
                            if (targetItem != null) CboTargetSpeaker.SelectedItem = targetItem;
                        }
                        else if (targetList.Count > 1)
                        {
                            CboTargetSpeaker.SelectedIndex = 1;
                        }
                        else if (targetList.Count > 0)
                        {
                            CboTargetSpeaker.SelectedIndex = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to enumerate audio devices:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            var micItem = CboMicrophone.SelectedItem as ComboDeviceItem;
            var refItem = CboReferenceSpeaker.SelectedItem as ComboDeviceItem;
            var targetItem = CboTargetSpeaker.SelectedItem as ComboDeviceItem;

            if (micItem == null || refItem == null || targetItem == null)
            {
                MessageBox.Show("Please select all devices before starting calibration.", "Selection Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (refItem.Id == targetItem.Id)
            {
                MessageBox.Show("Reference Speaker and Target Speaker must be different physical endpoints.", "Invalid Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Disable UI elements
            BtnStart.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            CboMicrophone.IsEnabled = false;
            CboReferenceSpeaker.IsEnabled = false;
            CboTargetSpeaker.IsEnabled = false;

            var progress = new Progress<string>(status =>
            {
                TxtStatus.Text = status;
            });

            try
            {
                var result = await _mainWindow.RunIsolatedCalibrationAsync(micItem.Device, refItem.Device, targetItem.Device, progress);
                
                double targetDelayMs = result.Item1;
                double referenceDelayMs = result.Item2;

                TxtStatus.Text = "Calibration complete!";
                
                // Apply result to MainWindow's data structure to compensate the faster device
                _mainWindow.ApplyCalibrationResult(refItem.Id, targetItem.Id, targetDelayMs, referenceDelayMs);

                string message;
                if (referenceDelayMs > 0)
                {
                    bool isRefRouted = _mainWindow.IsDeviceRouted(refItem.Id);

                    if (!isRefRouted)
                    {
                        message = $"Calibration Complete (Partial Sync)!\n\n" +
                                  $"Reference Speaker '{refItem.Name}' is faster by {referenceDelayMs:F1}ms.\n\n" +
                                  $"[WARNING] The delay of {referenceDelayMs:F1}ms is set on the checklist for '{refItem.Name}', but it is NOT currently active because the Reference Speaker (your Windows default device) is not checked in the checklist.\n\n" +
                                  $"To activate the delay and eliminate the lag, you must either:\n" +
                                  $"1. Check BOTH speakers in the checklist (requires setting a virtual/dummy playback device as Windows default to avoid feedback loops).\n" +
                                  $"2. Or set the slower speaker '{targetItem.Name}' as your Windows Default Playback Device and check '{refItem.Name}' in the checklist.";
                    }
                    else
                    {
                        message = $"Calibration Successful!\n\n" +
                                  $"Reference Speaker '{refItem.Name}' is faster.\n" +
                                  $"Applied Delay: {referenceDelayMs:F1}ms to Reference Speaker to sync.";
                    }
                }
                else if (targetDelayMs > 0)
                {
                    message = $"Calibration Successful!\n\n" +
                              $"Target Speaker '{targetItem.Name}' is faster.\n" +
                              $"Applied Delay: {targetDelayMs:F1}ms to Target Speaker to sync.";
                }
                else
                {
                    message = $"Calibration Successful!\n\n" +
                              $"Both speakers are already in sync (0ms delay applied).";
                }

                MessageBox.Show(message, "Sync Successful", MessageBoxButton.OK, MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Calibration failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                
                // Re-enable UI elements
                BtnStart.IsEnabled = true;
                BtnCancel.IsEnabled = true;
                CboMicrophone.IsEnabled = true;
                CboReferenceSpeaker.IsEnabled = true;
                CboTargetSpeaker.IsEnabled = true;
                TxtStatus.Text = "Calibration failed. Ready to try again.";
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
