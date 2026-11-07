using System.IO;
using System.Net.Http;

namespace Beam.Core;

public static class Cloudflared
{
    const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    static readonly string LocalCopy = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beam", "cloudflared.exe");

    public static string? Find()
    {
        var candidates = new List<string>
        {
            LocalCopy,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared", "cloudflared.exe"),
        };

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), "cloudflared.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<string> EnsureAsync(IProgress<int> progress, CancellationToken ct)
    {
        var existing = Find();
        if (existing is not null)
            return existing;

        Directory.CreateDirectory(Path.GetDirectoryName(LocalCopy)!);
        var temp = LocalCopy + ".download";

        using var http = new HttpClient();
        using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(temp))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            var last = -1;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    var percent = (int)(done * 100 / total);
                    if (percent != last)
                    {
                        last = percent;
                        progress.Report(percent);
                    }
                }
            }
        }

        File.Move(temp, LocalCopy, true);
        return LocalCopy;
    }
}
