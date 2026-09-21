using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SystemAudioRecorder.Audio;

namespace SystemAudioRecorder.Tests;

public sealed class StopTimerUiTests
{
    [Fact]
    public async Task TimerDefaultsOffAndValidatesInputsBeforeRecordingCanStart()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                // Construct only: never show the window, raise Loaded, or click Record.
                window = new MainWindow();
                var enabled = (CheckBox)window.FindName("StopAfterEnabled");
                var hours = (TextBox)window.FindName("StopAfterHours");
                var minutes = (TextBox)window.FindName("StopAfterMinutes");
                var seconds = (TextBox)window.FindName("StopAfterSeconds");
                var error = (TextBlock)window.FindName("StopAfterError");
                var countdown = (TextBlock)window.FindName("CountdownLabel");
                var record = (Button)window.FindName("RecordButton");
                var devices = (ComboBox)window.FindName("Devices");
                Assert.NotEqual(true, enabled.IsChecked);
                Assert.False(hours.IsEnabled);
                Assert.False(minutes.IsEnabled);
                Assert.False(seconds.IsEnabled);

                devices.ItemsSource = new[] { new OutputDevice("synthetic-not-opened", "Synthetic", true) };
                devices.SelectedIndex = 0;
                enabled.IsChecked = true;
                Assert.True(hours.IsEnabled);
                Assert.True(record.IsEnabled);
                Assert.Contains("00:30:00", countdown.Text);
                hours.Text = "00";
                minutes.Text = "00";
                seconds.Text = "00";
                Assert.False(record.IsEnabled);
                Assert.Equal(Visibility.Visible, error.Visibility);
                Assert.Contains("00:00:01", error.Text);
                seconds.Text = "01";
                Assert.True(record.IsEnabled);
                Assert.Equal(Visibility.Collapsed, error.Visibility);
                Assert.Contains("00:00:01", countdown.Text);
                hours.Text = "24";
                Assert.False(record.IsEnabled);
                seconds.Text = "00";
                Assert.True(record.IsEnabled);
                hours.Text = "x";
                Assert.False(record.IsEnabled);
                enabled.IsChecked = false;
                Assert.True(record.IsEnabled);
                Assert.Equal(Visibility.Collapsed, error.Visibility);
                Assert.Equal("Auto stop off", countdown.Text);
                completed.TrySetResult();
            }
            catch (Exception ex) { completed.TrySetException(ex); }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
