using System.Text.Json;

namespace SystemAudioRecorder.Core;

public sealed record SessionRecord(string SessionId, DateTimeOffset StartedUtc, string Version, bool CleanExit);

// The file remains exclusively writable for the session. An incomplete record is
// evidence of an unconfirmed exit, not proof of a crash.
public sealed class SessionJournal : IDisposable
{
    private readonly FileStream stream;
    private readonly SessionRecord current;
    public SessionRecord? Previous { get; }
    public string? PreviousReadError { get; }

    public SessionJournal(string directory, string sessionId, string version)
    {
        Directory.CreateDirectory(directory);
        stream = new FileStream(Path.Combine(directory, "session-state.json"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length > 0)
            {
                try
                {
                    if (stream.Length > 4096) throw new InvalidDataException("Session marker exceeds its size limit.");
                    Previous = JsonSerializer.Deserialize<SessionRecord>(stream);
                    if (Previous == null || !Guid.TryParse(Previous.SessionId, out _) ||
                        Previous.StartedUtc == default || string.IsNullOrWhiteSpace(Previous.Version))
                        throw new InvalidDataException("Session marker is incomplete.");
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    Previous = null;
                    PreviousReadError = "The previous session marker is invalid or incomplete.";
                }
            }
            current = new(sessionId, DateTimeOffset.UtcNow, version, false);
            Write(current);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void MarkCleanExit() => Write(current with { CleanExit = true });

    private void Write(SessionRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > 4096) throw new InvalidDataException("Session marker exceeds its size limit.");
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(true);
    }

    public void Dispose() => stream.Dispose();
}
