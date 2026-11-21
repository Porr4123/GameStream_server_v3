using System.Diagnostics;

namespace GameStream.Server.Diagnostics;

/// <summary>Confere, na partida, se o ffmpeg existe e tem o codificador libx264 (sem ele não há vídeo nenhum).</summary>
internal static class FfmpegCheck
{
    internal readonly record struct Result(bool Found, string Version, bool HasX264, string? Error);

    public static async Task<Result> RunAsync(string ffmpegPath, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-encoders");

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            var text = await stdout + await stderr;
            bool x264 = text.Contains("libx264", StringComparison.Ordinal);

            // A versão só aparece com -version.
            var version = await VersionAsync(ffmpegPath, cts.Token);
            return new Result(true, version, x264, null);
        }
        catch (Exception ex)
        {
            return new Result(false, "", false, ex.Message);
        }
    }

    private static async Task<string> VersionAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-version");
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "?";
    }
}
