using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace SystemAudioRecorder.Audio;

public sealed record OutputDevice(string Id, string Name, bool IsDefault)
{
    public string DisplayName => Name + (IsDefault ? " (default)" : "");
}

public static class OutputDevices
{
    public static IReadOnlyList<OutputDevice> GetActive()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Windows has no default playback endpoint.
        }
        var result = new List<OutputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
                result.Add(new OutputDevice(device.ID, device.FriendlyName, device.ID == defaultId));
        }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToArray();
    }
}
