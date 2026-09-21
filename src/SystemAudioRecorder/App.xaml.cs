using System.Threading;
using System.Windows;
using System.IO;
using SystemAudioRecorder.Diagnostics;

namespace SystemAudioRecorder;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    public static DiagnosticRuntime? Diagnostics { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, @"Local\SystemAudioRecorder.Desktop", out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("System Audio Recorder is already open.", "System Audio Recorder");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        Diagnostics = new DiagnosticRuntime(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemAudioRecorder", "Logs"));
        DispatcherUnhandledException += (_, args) =>
        {
            Diagnostics.ReportUnhandled("fatal.dispatcher", args.Exception, fatal: true);
            // Leave Handled false: continuing after an arbitrary UI failure is unsafe.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Diagnostics.ReportUnhandled("fatal.appdomain",
                args.ExceptionObject as Exception ?? new InvalidOperationException("Unhandled non-Exception object."),
                args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Diagnostics.ReportUnhandled("task.unobserved", args.Exception, fatal: false);
        await Diagnostics.InitializeAsync();
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Diagnostics != null && !Diagnostics.EndSessionAsync(e.ApplicationExitCode).GetAwaiter().GetResult())
        {
            MessageBox.Show("Some diagnostics could not be saved. The next launch may report an unconfirmed exit. " +
                "Recovery audio has not been deleted.", "Diagnostics warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Diagnostics?.Log.Write(Core.Diagnostics.DiagnosticLevel.Warning, "app.windows_session_ending",
            $"reason={e.ReasonSessionEnding}", Diagnostics.RecordingId, Diagnostics.ExportId, durable: true);
        base.OnSessionEnding(e);
    }
}
