using System.Security.Cryptography;
using System.Text;

namespace SystemAudioRecorder.Core;

public static class DiagnosticIds
{
    public static string Endpoint(string sessionId, string deviceId) => Hash(sessionId + "\0" + deviceId);

    public static string Recording(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var suffix = name[(name.LastIndexOf('_') + 1)..];
        return Guid.TryParseExact(suffix, "N", out var id) ? id.ToString("N") : "recovery-" + Hash(name);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
