namespace SystemAudioRecorder.Core;

public sealed record RecoveryRecording(string Path, long Bytes)
{
    public TimeSpan Duration => AudioFormat.Duration(Bytes);
    public string DisplayName => $"{System.IO.Path.GetFileNameWithoutExtension(Path)}  |  {Duration:hh\\:mm\\:ss}";
}

public sealed class RecoveryStore(string directory)
{
    public string DirectoryPath { get; } = directory;

    public FileStream Create(Guid? recordingId = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{recordingId ?? Guid.NewGuid():N}.pcm");
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            65536, FileOptions.SequentialScan);
    }

    public IReadOnlyList<RecoveryRecording> List()
    {
        Directory.CreateDirectory(DirectoryPath);
        return Directory.EnumerateFiles(DirectoryPath, "*.pcm")
            .OrderDescending()
            .Select(path => new RecoveryRecording(path, new FileInfo(path).Length))
            .ToArray();
    }

    public void Delete(RecoveryRecording recording)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(recording.Path));
        if (!string.Equals(parent, Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(recording.Path) != ".pcm")
            throw new InvalidOperationException("The file is not in this recovery store.");
        File.Delete(recording.Path);
    }
}
