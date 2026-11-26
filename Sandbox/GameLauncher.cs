using System.Diagnostics;
using GameStream.Server.Config;
using GameStream.Server.Interop;

namespace GameStream.Server.Sandbox;

public sealed class LaunchedGame : IDisposable
{
    private readonly JobObject _job;

    internal LaunchedGame(Process process, JobObject job, IntPtr windowHandle, string windowTitle, ResourceLimits limits)
    {
        Process = process;
        _job = job;
        WindowHandle = windowHandle;
        WindowTitle = windowTitle;
        Limits = limits;
    }

    public Process Process { get; }
    public IntPtr WindowHandle { get; }
    public string WindowTitle { get; }
    public ResourceLimits Limits { get; }

    /// <summary>CPU acumulada e processos vivos de todo o job do jogo. null depois que o jogo é encerrado.</summary>
    internal (TimeSpan Cpu, int ActiveProcesses)? GetAccounting() => _job.GetAccounting();

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { /* ignora */ }
        _job.Dispose();
        Process.Dispose();
    }
}

public static class GameLauncher
{
    public static async Task<LaunchedGame> LaunchAsync(GameProfile profile, ResourceLimits limits, CancellationToken ct)
    {
        var folder = Path.GetFullPath(profile.Folder);

        // Caminho absoluto = escolha explícita do dono (ex.: atalho para outra pasta).
        // Caminho relativo precisa ficar dentro da pasta do jogo (impede "..\..\algo.exe").
        string exe;
        if (Path.IsPathRooted(profile.Exe))
        {
            exe = Path.GetFullPath(profile.Exe);
        }
        else
        {
            exe = Path.GetFullPath(Path.Combine(folder, profile.Exe));
            if (!exe.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O executável precisa estar dentro da pasta do jogo.");
        }
        if (!File.Exists(exe))
            throw new FileNotFoundException("Executável do jogo não encontrado.", exe);

        var workingDir = !string.IsNullOrWhiteSpace(profile.WorkingDir)
            ? profile.WorkingDir!
            : Path.GetDirectoryName(exe)!;

        var psi = new ProcessStartInfo(exe) { WorkingDirectory = workingDir, UseShellExecute = false };
        string finalArgs = PrepareArgs(exe, profile.Args, folder);
        if (finalArgs.Length > 0) psi.Arguments = finalArgs;

        if (profile.RedirectAppData)
        {
            var roaming = Path.Combine(folder, "dados", "appdata", "Roaming");
            var local = Path.Combine(folder, "dados", "appdata", "Local");
            Directory.CreateDirectory(roaming);
            Directory.CreateDirectory(local);
            psi.Environment["APPDATA"] = roaming;
            psi.Environment["LOCALAPPDATA"] = local;
        }

        var job = new JobObject(limits);
        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao iniciar o jogo.");
            job.Assign(process); // TODO: iniciar suspenso para não haver janela de tempo antes do job.

            var (hwnd, title) = await WaitForWindowAsync(process, profile, TimeSpan.FromSeconds(45), ct);
            return new LaunchedGame(process, job, hwnd, title, limits);
        }
        catch
        {
            try { process?.Kill(entireProcessTree: true); } catch { /* ignora */ }
            job.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Troca {pasta}/{dados} nos argumentos. Para navegadores Chromium (Chrome, Edge, Brave) sem --user-data-dir, cria um perfil PRÓPRIO do jogo:
    /// sem ele o navegador entrega a página ao Chrome que já está aberto do usuário (outro processo, fora do limite de CPU/RAM
    /// e que não fecha junto com o jogo) e a captura/som/teclas perdem o controle.
    /// </summary>
    internal static string PrepareArgs(string exe, string? args, string folder)
    {
        string dados = Path.Combine(folder, "dados");
        string result = (args ?? "").Replace("{pasta}", folder, StringComparison.OrdinalIgnoreCase)
                                    .Replace("{dados}", dados, StringComparison.OrdinalIgnoreCase).Trim();

        string name = Path.GetFileNameWithoutExtension(exe.Substring(exe.LastIndexOfAny(new[] { '\\', '/' }) + 1)).ToLowerInvariant();
        bool chromium = name is "chrome" or "msedge" or "brave" or "chromium" or "vivaldi";
        if (chromium && !result.Contains("--user-data-dir", StringComparison.OrdinalIgnoreCase))
        {
            string profileDir = Path.Combine(dados, "navegador");
            try { Directory.CreateDirectory(profileDir); } catch { /* o navegador cria */ }
            result = $"{result} --user-data-dir=\"{profileDir}\" --no-first-run --no-default-browser-check".Trim();
        }
        return result;
    }

    private static async Task<(IntPtr, string)> WaitForWindowAsync(
        Process process, GameProfile profile, TimeSpan timeout, CancellationToken ct)
    {
        bool byTitle = !string.IsNullOrWhiteSpace(profile.WindowTitle);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            IntPtr hwnd;
            if (byTitle)
            {
                hwnd = Native.FindWindowContaining(profile.WindowTitle!);
            }
            else
            {
                if (process.HasExited)
                    throw new InvalidOperationException("O jogo fechou antes de abrir a janela. Se o .exe é um launcher, preencha windowTitle.");
                process.Refresh();
                hwnd = process.MainWindowHandle;
            }

            if (hwnd != IntPtr.Zero)
            {
                var title = Native.GetTitle(hwnd);
                if (title.Length > 0) return (hwnd, title);
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException("A janela do jogo não apareceu a tempo.");
    }
}
