using System.Runtime.InteropServices;
using System.Windows;

namespace MultiAudioRouter;

/// <summary>
/// Interaction logic for App.xaml.
/// Attaches a console window so diagnostic logs (Console.WriteLine) are visible
/// alongside the main application window.
/// </summary>
public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Try to attach to an existing console (e.g. launched from a terminal).
        // If none exists, open a new console window.
        if (!AttachConsole(-1 /* ATTACH_PARENT_PROCESS */))
            AllocConsole();

        System.Console.Title = "MultiAudioRouter — DSP Diagnostics";
        System.Console.WriteLine("[MultiAudioRouter] Console attached. Acoustic diagnostic logs will appear here.");

        base.OnStartup(e);
    }
}
