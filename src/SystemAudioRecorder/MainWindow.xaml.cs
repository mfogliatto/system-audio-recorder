using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SystemAudioRecorder.Audio;
using SystemAudioRecorder.Core;
using SystemAudioRecorder.Core.Diagnostics;
using System.Diagnostics;

namespace SystemAudioRecorder;

public partial class MainWindow : Window
{
    private readonly RecoveryStore store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemAudioRecorder", "Recovery"));
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly float[] levels = new float[100];
    private LoopbackRecorder? recorder;
    private CancellationTokenSource? exportCancellation;
    private bool paused;
    private bool busy;
    private bool allowClose;
    private long lastDiagnosticsUpdate;
    private string? lastRecordingId;
    private string? currentExportId;
    private Task? captureObservation;
    private string? pendingTimedSave;

    public MainWindow()
    {
        InitializeComponent();
        timer.Tick += (_, _) => UpdateMeter();
        timer.Start();
        Loaded += (_, _) => RunUi(() =>
        {
            Trace("ui.window_loaded", $"version={App.Diagnostics?.Version}");
            LoadDevices();
            LoadRecoveries();
            DiagnosticsNotice.Text = App.Diagnostics?.PreviousSessionNotice ?? "";
            UpdateDiagnostics();
            UpdateTimerPreview();
            if (Recoveries.Items.Count > 0)
                StatusLabel.Text = "Recovered unsaved audio. Save it as MP3 or keep it here for later.";
        });
        Closed += (_, _) =>
        {
            timer.Stop();
            Trace("ui.window_closed", $"captureActive={recorder != null} busy={busy}", durable: true);
        };
    }

    private void LoadDevices()
    {
        var previous = (Devices.SelectedItem as OutputDevice)?.Id;
        var devices = OutputDevices.GetActive();
        Trace("devices.enumerated", $"activeRenderEndpoints={devices.Count} hasDefault={devices.Any(d => d.IsDefault)}");
        Devices.ItemsSource = devices;
        Devices.SelectedItem = devices.FirstOrDefault(d => d.Id == previous) ?? devices.FirstOrDefault();
        if (devices.Count == 0)
            StatusLabel.Text = "No active playback device. Connect speakers or headphones, then Refresh.";
        else
            StatusLabel.Text = "Ready. The selected playback device will be used for the next recording.";
        UpdateControls();
    }

    private void LoadRecoveries(string? selectPath = null)
    {
        selectPath ??= (Recoveries.SelectedItem as RecoveryRecording)?.Path;
        var items = store.List();
        Trace("recovery.inventory", $"count={items.Count} totalBytes={items.Sum(r => r.Bytes)} detailedEntries={Math.Min(items.Count, 20)}");
        foreach (var item in items.Take(20))
            App.Diagnostics?.Log.Write(DiagnosticLevel.Info, "recovery.available",
                FormattableString.Invariant($"bytes={item.Bytes} durationSeconds={item.Duration.TotalSeconds:F3}"),
                DiagnosticIds.Recording(item.Path));
        Recoveries.ItemsSource = items;
        Recoveries.SelectedItem = items.FirstOrDefault(r => r.Path == selectPath) ?? items.FirstOrDefault();
        UpdateControls();
    }

    private void UpdateControls()
    {
        var active = recorder != null;
        var idle = !active && !busy && pendingTimedSave == null;
        var validTimer = TryGetStopAfter(out _, out var timerError);
        StopAfterError.Text = timerError ?? "";
        StopAfterError.Visibility = timerError == null ? Visibility.Collapsed : Visibility.Visible;
        StopAfterEnabled.IsEnabled = idle;
        StopAfterHours.IsEnabled = StopAfterMinutes.IsEnabled = StopAfterSeconds.IsEnabled =
            idle && StopAfterEnabled.IsChecked == true;
        RecordButton.IsEnabled = idle && validTimer && Devices.SelectedItem != null;
        PauseButton.IsEnabled = active && !busy;
        StopButton.IsEnabled = active && !busy;
        Devices.IsEnabled = RefreshButton.IsEnabled = idle;
        Recoveries.IsEnabled = idle;
        SaveButton.IsEnabled = DiscardButton.IsEnabled = idle && Recoveries.SelectedItem != null;
        PauseButton.Content = paused ? "_Resume" : "_Pause";
        StateLabel.Text = exportCancellation != null ? "EXPORTING" :
            active ? paused ? "PAUSED" : "RECORDING" : "READY";
        CancelExportButton.Visibility = exportCancellation != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (busy || recorder != null || pendingTimedSave != null) return;
        if (Devices.SelectedItem is not OutputDevice device) return;
        if (!TryGetStopAfter(out var stopAfter, out var timerError))
        {
            ShowError("Invalid Stop after duration", new ArgumentException(timerError));
            return;
        }
        busy = true;
        paused = false;
        var current = new LoopbackRecorder(App.Diagnostics?.Log, stopAfter);
        recorder = current;
        lastRecordingId = current.RecordingId;
        App.Diagnostics?.SetRecording(current);
        Array.Clear(levels);
        CaptureWarning.Visibility = Visibility.Collapsed;
        UpdateCountdown();
        UpdateControls();
        try
        {
            await current.StartAsync(device.Id, store);
            StatusLabel.Text = $"Recording {device.Name}. Audio is buffered to disk, not memory.";
            captureObservation = ObserveCaptureAsync(current);
        }
        catch (Exception ex)
        {
            await current.Completion;
            recorder = null;
            captureObservation = null;
            CountdownLabel.Text = stopAfter.HasValue ? "Auto stop cancelled: recording did not start." : "Auto stop off";
            App.Diagnostics?.SetRecording(null);
            ShowError("Could not start recording", ex);
            RunUi(() => LoadRecoveries());
        }
        finally { busy = false; UpdateControls(); }
    }

    private async Task ObserveCaptureAsync(LoopbackRecorder current)
    {
        var result = await current.Completion;
        if (recorder != current) return;
        recorder = null;
        App.Diagnostics?.SetRecording(null);
        paused = false;
        var timedStop = result.Error == null && result.StopReason == "timer_elapsed";
        if (timedStop) pendingTimedSave = result.Path;
        CountdownLabel.Text = timedStop ? "Timer expired - recording stopped." :
            current.StopTimer.Duration.HasValue ? "Auto stop cancelled." : "Auto stop off";
        DurationLabel.Text = FormatDuration(current.Duration);
        if (result.Error != null) ShowError("Recording stopped. Existing audio is retained for recovery", result.Error);
        else StatusLabel.Text = timedStop ? "Stop after timer expired. Your recording is ready to save." :
            "Recording stopped. Save your MP3, or keep the unsaved recording for later.";
        UpdateCaptureWarning(current);
        RunUi(() => LoadRecoveries(result.Path));
        UpdateControls();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (recorder is not { } current) return;
        busy = true;
        UpdateControls();
        try
        {
            if (paused) await current.ResumeAsync();
            else await current.PauseAsync();
            if (recorder != current || current.Completion.IsCompleted) return;
            paused = !paused;
            StatusLabel.Text = paused ? "Paused. Paused audio and time are excluded." : "Recording resumed.";
        }
        catch (InvalidOperationException) when (current.StopTimer.StopReason != null)
        {
            Trace("ui.pause_superseded_by_stop");
        }
        catch (Exception ex) { ShowError("Could not change recording state", ex); }
        finally { busy = false; UpdateControls(); }
    }

    private async Task StopCaptureAsync(string reason = "user_stop")
    {
        if (recorder is not { } current) return;
        var observation = captureObservation;
        await current.StopAsync(reason);
        await current.Completion;
        if (observation != null) await observation;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (busy || recorder == null) return;
        busy = true;
        UpdateControls();
        try
        {
            await StopCaptureAsync();
            pendingTimedSave = null;
            await SaveSelectedAsync();
        }
        catch (Exception ex) { ShowError("Could not stop or save; recovery audio is retained", ex); }
        finally { busy = false; UpdateControls(); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        busy = true;
        UpdateControls();
        try { await SaveSelectedAsync(); }
        catch (Exception ex) { ShowError("Could not save; recovery audio is retained", ex); }
        finally { busy = false; UpdateControls(); }
    }

    private async Task<bool> SaveSelectedAsync()
    {
        if (Recoveries.SelectedItem is not RecoveryRecording recording) return false;
        lastRecordingId = DiagnosticIds.Recording(recording.Path);
        var operationId = Guid.NewGuid().ToString("N");
        currentExportId = operationId;
        Trace("save.dialog_opened", "format=mp3", durable: true);
        var dialog = new SaveFileDialog
        {
            Title = "Save recording as MP3",
            Filter = "MP3 audio (*.mp3)|*.mp3",
            DefaultExt = ".mp3",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"Recording_{Path.GetFileNameWithoutExtension(recording.Path)}.mp3"
        };
        if (dialog.ShowDialog(this) != true)
        {
            Trace("save.dialog_cancelled", "recoveryRetained=true", durable: true);
            currentExportId = null;
            StatusLabel.Text = "Save cancelled. Your audio is kept under Unsaved recordings, including after restart.";
            return false;
        }
        exportCancellation = new CancellationTokenSource();
        App.Diagnostics?.SetExport(operationId);
        UpdateControls();
        var cancellation = exportCancellation;
        var progress = new Progress<double>(fraction =>
        {
            if (ReferenceEquals(exportCancellation, cancellation))
                StatusLabel.Text = $"Encoding MP3... {fraction:P0}";
        });
        try
        {
            await Task.Run(() => Mp3Exporter.Export(recording.Path, dialog.FileName,
                exportCancellation.Token, progress, App.Diagnostics?.Log, operationId));
            StatusLabel.Text = $"Saved: {dialog.FileName}";
            try
            {
                store.Delete(recording);
                Trace("recovery.removed_after_export", $"bytes={recording.Bytes}", durable: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError("MP3 saved, but the recovery copy could not be removed", ex);
            }
            LoadRecoveries();
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Export cancelled. Your recording is still available to save again.";
            return false;
        }
        catch (Exception ex)
        {
            ShowError("Could not save; recovery audio is retained", ex);
            return false;
        }
        finally
        {
            exportCancellation.Dispose();
            exportCancellation = null;
            App.Diagnostics?.SetExport(null);
            currentExportId = null;
            UpdateControls();
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        Trace("export.cancel_requested", durable: true);
        exportCancellation?.Cancel();
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => RunUi(() =>
    {
        if (Recoveries.SelectedItem is not RecoveryRecording recording) return;
        lastRecordingId = DiagnosticIds.Recording(recording.Path);
        Trace("recovery.discard_prompt");
        if (MessageBox.Show(this, "Permanently discard this unsaved recording? This cannot be undone.",
            "Discard recording", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Trace("recovery.discard_cancelled");
            return;
        }
        store.Delete(recording);
        Trace("recovery.discarded", $"bytes={recording.Bytes}", durable: true);
        LoadRecoveries();
        StatusLabel.Text = "Unsaved recording discarded.";
    });

    private void Refresh_Click(object sender, RoutedEventArgs e) => RunUi(LoadDevices);
    private void Recoveries_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SaveButton == null) return;
        UpdateControls();
        if (recorder == null && Recoveries.SelectedItem is RecoveryRecording recording)
        {
            lastRecordingId = DiagnosticIds.Recording(recording.Path);
            Trace("recovery.selected", $"bytes={recording.Bytes}");
            DurationLabel.Text = FormatDuration(recording.Duration);
        }
    }

    private void UpdateMeter()
    {
        App.Diagnostics?.UiHeartbeat();
        if (Stopwatch.GetElapsedTime(lastDiagnosticsUpdate).TotalSeconds >= 1)
        {
            UpdateDiagnostics();
            lastDiagnosticsUpdate = Stopwatch.GetTimestamp();
        }
        if (recorder != null)
        {
            DurationLabel.Text = FormatDuration(recorder.Duration);
            UpdateCaptureWarning(recorder);
            UpdateCountdown();
        }
        Array.Copy(levels, 1, levels, 0, levels.Length - 1);
        levels[^1] = paused ? 0 : recorder?.TakePeak() ?? 0;
        DrawMeter();
        if (pendingTimedSave != null && !busy) _ = SaveTimedRecordingAsync();
    }

    private void WaveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMeter();
    private void DrawMeter()
    {
        if (WaveCanvas == null || WaveLine == null) return;
        var points = new PointCollection(levels.Length * 2);
        for (var i = 0; i < levels.Length; i++)
        {
            var x = i * WaveCanvas.ActualWidth / (levels.Length - 1);
            var magnitude = levels[i] * 27;
            points.Add(new Point(x, 30 - magnitude));
            points.Add(new Point(x, 30 + magnitude));
        }
        WaveLine.Points = points;
    }

    private void UpdateCaptureWarning(LoopbackRecorder current)
    {
        if (current.Discontinuities == 0) return;
        CaptureWarning.Text = "Windows reported a stream discontinuity. Any missing audio is represented by silence.";
        CaptureWarning.Visibility = Visibility.Visible;
    }

    private static string FormatDuration(TimeSpan time) => $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        Trace("ui.close_requested", $"captureActive={recorder != null} busy={busy}", durable: true);
        if (busy)
        {
            e.Cancel = true;
            StatusLabel.Text = "Wait for the current operation, or cancel the MP3 export before closing.";
            return;
        }
        if (recorder == null)
        {
            if (pendingTimedSave != null)
                Trace("timer.save_prompt_skipped", "reason=window_close recoveryRetained=true", durable: true);
            pendingTimedSave = null;
            return; // Unsaved files remain on disk for the next launch.
        }
        e.Cancel = true;
        // Modal dialogs pump dispatcher ticks. Hold the UI operation guard before
        // opening one; the capture deadline still runs independently on its worker.
        busy = true;
        UpdateControls();
        try
        {
            var choice = MessageBox.Show(this,
                "Recording is active.\n\nYes: stop and save MP3, then close.\nNo: stop and keep a recovery copy, then close.\nCancel: keep recording (unless the Stop after timer expires).",
                "Close recorder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            Trace("ui.close_choice", $"choice={choice}", durable: true);
            if (choice == MessageBoxResult.Cancel) return;
            await StopCaptureAsync("window_close");
            pendingTimedSave = null;
            if (choice == MessageBoxResult.Yes && !await SaveSelectedAsync()) return;
            allowClose = true;
            Close();
        }
        catch (Exception ex) { ShowError("Could not finish recording; the window will remain open", ex); }
        finally { busy = false; UpdateControls(); }
    }

    private void RunUi(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError("Operation failed", ex); }
    }

    private void ShowError(string title, Exception exception)
    {
        App.Diagnostics?.Log.Write(DiagnosticLevel.Error, "ui.error", title,
            recorder?.RecordingId ?? lastRecordingId, currentExportId, exception, durable: true);
        StatusLabel.Text = $"{title}: {exception.Message}";
        MessageBox.Show(this, $"{exception.Message}\n\nRecovery folder:\n{store.DirectoryPath}",
            title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Devices_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Devices.SelectedItem is OutputDevice device && App.Diagnostics is { } diagnostics)
            diagnostics.Log.Write(DiagnosticLevel.Info, "device.selected",
                $"endpointRef={DiagnosticIds.Endpoint(diagnostics.Log.SessionId, device.Id)} isDefault={device.IsDefault}");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => RunUi(() =>
    {
        if (App.Diagnostics is not { } diagnostics)
            throw new InvalidOperationException("Local diagnostics are not initialized.");
        Directory.CreateDirectory(diagnostics.Log.DirectoryPath);
        Process.Start(new ProcessStartInfo(diagnostics.Log.DirectoryPath) { UseShellExecute = true });
        Trace("ui.logs_folder_opened");
    });

    private void UpdateDiagnostics()
    {
        if (App.Diagnostics is not { } diagnostics) return;
        var status = diagnostics.Log.Status;
        var warning = diagnostics.Warning;
        var shortWarning = warning?.Split('\n')[0];
        if (shortWarning?.Length > 160) shortWarning = shortWarning[..160] + "...";
        var text = warning != null ? "Diagnostics warning: " + shortWarning :
            status.DroppedEvents > 0 ? $"Diagnostics limited: {status.DroppedEvents} log events dropped. Audio is separate." :
            status.IsReady ? $"Local logs active | Session {diagnostics.Log.SessionId[..8]} | No audio content" :
            "Local diagnostics are starting.";
        if (DiagnosticsStatus.Text != text) DiagnosticsStatus.Text = text;
        DiagnosticsStatus.ToolTip = warning;
        DiagnosticsStatus.Foreground = warning != null || status.DroppedEvents > 0 ? Brushes.LightSalmon : Brushes.LightSteelBlue;
    }

    private void Trace(string name, string message = "", bool durable = false) =>
        App.Diagnostics?.Log.Write(DiagnosticLevel.Info, name, message,
            recorder?.RecordingId ?? lastRecordingId, currentExportId, durable: durable);

    private bool TryGetStopAfter(out TimeSpan? duration, out string? error) =>
        StopAfterDuration.TryParse(StopAfterEnabled.IsChecked == true, StopAfterHours.Text,
            StopAfterMinutes.Text, StopAfterSeconds.Text, out duration, out error);

    private void StopAfter_Changed(object sender, RoutedEventArgs e)
    {
        if (StopAfterError == null || CancelExportButton == null || CountdownLabel == null) return;
        UpdateControls();
        UpdateTimerPreview();
    }

    private void UpdateTimerPreview()
    {
        if (recorder != null) return;
        var valid = TryGetStopAfter(out var duration, out _);
        CountdownLabel.Text = !valid ? "Enter a valid Stop after duration." :
            duration.HasValue ? $"Auto stop after {FormatDuration(duration.Value)} of elapsed time" : "Auto stop off";
    }

    private void UpdateCountdown()
    {
        if (recorder is not { } current) return;
        var scheduled = current.StopTimer;
        CountdownLabel.Text = !scheduled.Duration.HasValue ? "Auto stop off" :
            !scheduled.Started ? "Auto stop starts when recording begins." :
            scheduled.StopReason == RecordingStopReason.TimerElapsed ? "Timer expired - stopping recording..." :
            $"Auto stop in {FormatDuration(TimeSpan.FromSeconds(Math.Ceiling(scheduled.Remaining!.Value.TotalSeconds)))} (includes pauses)";
    }

    private async Task SaveTimedRecordingAsync()
    {
        if (busy || pendingTimedSave is not { } path) return;
        busy = true;
        pendingTimedSave = null;
        UpdateControls();
        try
        {
            LoadRecoveries(path);
            if (Recoveries.SelectedItem is not RecoveryRecording selected || selected.Path != path)
                throw new IOException("The timed recording is no longer in the recovery folder.");
            Trace("timer.save_prompt", "recoveryRetainedUntilSaved=true", durable: true);
            await SaveSelectedAsync();
        }
        catch (Exception ex) { ShowError("Could not save the timed recording; check Unsaved recordings", ex); }
        finally { busy = false; UpdateControls(); }
    }
}
