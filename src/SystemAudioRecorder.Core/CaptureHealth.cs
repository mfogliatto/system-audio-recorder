namespace SystemAudioRecorder.Core;

// Capture-thread-owned numeric aggregates; no audio contents or per-packet events.
public sealed class CaptureHealth
{
    private long? previousArrival;
    private long? anchorDevice;
    private long anchorQpc;
    private long previousEndDevice;
    public long Packets { get; private set; }
    public long InputFrames { get; private set; }
    public long SilentInputFrames { get; private set; }
    public long Discontinuities { get; private set; }
    public long DeviceGapFrames { get; private set; }
    public long DeviceOverlapFrames { get; private set; }
    public long DeliveryGapsOver100Ms { get; private set; }
    public double MaxDeliveryGapMs { get; private set; }
    public double MaxPacketAgeMs { get; private set; }
    public double MinQpcDeviceOffsetFrames { get; private set; }
    public double MaxQpcDeviceOffsetFrames { get; private set; }
    public double LastQpcDeviceOffsetFrames { get; private set; }
    public int MaxPacketsPerDrain { get; private set; }
    public double MaxWriteMs { get; private set; }
    public double MaxFlushMs { get; private set; }

    public void Packet(int frames, long devicePosition, long qpcTicks, long arrivalTicks,
        bool silent, bool discontinuity)
    {
        if (frames <= 0) return;
        if (previousArrival.HasValue)
        {
            var gap = (arrivalTicks - previousArrival.Value) / (double)TimeSpan.TicksPerMillisecond;
            MaxDeliveryGapMs = Math.Max(MaxDeliveryGapMs, gap);
            if (gap > 100) DeliveryGapsOver100Ms++;
        }
        MaxPacketAgeMs = Math.Max(MaxPacketAgeMs,
            (arrivalTicks - qpcTicks) / (double)TimeSpan.TicksPerMillisecond);
        if (anchorDevice.HasValue && !discontinuity)
        {
            var gap = devicePosition - previousEndDevice;
            DeviceGapFrames += Math.Max(0, gap);
            DeviceOverlapFrames += Math.Max(0, -gap);
        }
        if (!anchorDevice.HasValue || discontinuity)
        {
            anchorDevice = devicePosition;
            anchorQpc = qpcTicks;
        }
        LastQpcDeviceOffsetFrames = (qpcTicks - anchorQpc) *
            ((double)AudioFormat.SampleRate / TimeSpan.TicksPerSecond) - (devicePosition - anchorDevice.Value);
        MinQpcDeviceOffsetFrames = Math.Min(MinQpcDeviceOffsetFrames, LastQpcDeviceOffsetFrames);
        MaxQpcDeviceOffsetFrames = Math.Max(MaxQpcDeviceOffsetFrames, LastQpcDeviceOffsetFrames);
        if (discontinuity && Packets > 0) Discontinuities++;
        Packets++;
        InputFrames += frames;
        if (silent) SilentInputFrames += frames;
        previousArrival = arrivalTicks;
        previousEndDevice = devicePosition + frames;
    }

    public void Drain(int packets) => MaxPacketsPerDrain = Math.Max(MaxPacketsPerDrain, packets);
    public void WriteElapsed(double milliseconds) => MaxWriteMs = Math.Max(MaxWriteMs, milliseconds);
    public void FlushElapsed(double milliseconds) => MaxFlushMs = Math.Max(MaxFlushMs, milliseconds);

    public string Summary() => FormattableString.Invariant(
        $"packets={Packets} inputFrames={InputFrames} silentInputFrames={SilentInputFrames} discontinuities={Discontinuities} deviceGapFrames={DeviceGapFrames} deviceOverlapFrames={DeviceOverlapFrames} deliveryGapsOver100ms={DeliveryGapsOver100Ms} maxDeliveryGapMs={MaxDeliveryGapMs:F2} maxPacketAgeMs={MaxPacketAgeMs:F2} qpcDeviceOffsetFrames={LastQpcDeviceOffsetFrames:F3} minQpcDeviceOffsetFrames={MinQpcDeviceOffsetFrames:F3} maxQpcDeviceOffsetFrames={MaxQpcDeviceOffsetFrames:F3} maxPacketsPerDrain={MaxPacketsPerDrain} maxWriteMs={MaxWriteMs:F2} maxFlushMs={MaxFlushMs:F2}");
}
