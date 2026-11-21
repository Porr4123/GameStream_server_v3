namespace GameStream.Server.Config;

public sealed record DiscoveredLaunch(string ExePath, string? Args, string? WorkingDir, string Source);

/// <summary>
/// Procura o que executar dentro da pasta de um jogo:
///  1) um atalho (.lnk) que aponte para um .exe (até 2 níveis de pasta);
///  2) senão, um .exe (até 3 níveis), preferindo "Game.exe" (RPG Maker), depois o mais raso e o maior.
/// Ignora instaladores, desinstaladores, redistribuíveis e crash handlers.
/// </summary>
public static class ExeDiscovery
{
    private static readonly string[] IgnoredParts =
    {
        "unins", "uninstall", "vcredist", "vc_redist", "redist", "dxsetup",
        "dotnetfx", "installer", "crash", "updater"
    };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "dados" };

    public static DiscoveredLaunch? Find(string gameFolder)
    {
        // 1) Atalhos
        foreach (var lnk in Enumerate(gameFolder, ".lnk", 2))
        {
            var info = ShortcutResolver.Resolve(lnk);
            if (info is null) continue;

            var target = info.Value.Target;
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
            if (IsIgnored(target)) continue;

            return new DiscoveredLaunch(
                target,
                string.IsNullOrWhiteSpace(info.Value.Args) ? null : info.Value.Args,
                string.IsNullOrWhiteSpace(info.Value.WorkingDir) ? null : info.Value.WorkingDir,
                $"atalho {Path.GetFileName(lnk)}");
        }

        // 2) Executáveis
        var best = Enumerate(gameFolder, ".exe", 3)
            .Where(p => !IsIgnored(p))
            .Select(p => new
            {
                FullPath = p,
                IsGameExe = Path.GetFileName(p).Equals("game.exe", StringComparison.OrdinalIgnoreCase),
                Depth = DepthOf(gameFolder, p),
                Size = new FileInfo(p).Length
            })
            .OrderByDescending(c => c.IsGameExe)
            .ThenBy(c => c.Depth)
            .ThenByDescending(c => c.Size)
            .FirstOrDefault();

        return best is null ? null : new DiscoveredLaunch(best.FullPath, null, null, "executável encontrado");
    }

    private static bool IsIgnored(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return IgnoredParts.Any(part => name.Contains(part));
    }

    private static int DepthOf(string root, string file) =>
        Path.GetRelativePath(root, file).Count(c => c == Path.DirectorySeparatorChar);

    /// <summary>Busca em largura (pastas mais rasas primeiro), filtrando pela extensão exata.</summary>
    private static IEnumerable<string> Enumerate(string root, string extension, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            string[] files = Array.Empty<string>();
            try { files = Directory.GetFiles(dir); } catch { /* sem permissão */ }

            foreach (var f in files.Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return f;

            if (depth >= maxDepth) continue;

            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                    if (!SkipDirs.Contains(Path.GetFileName(sub)))
                        queue.Enqueue((sub, depth + 1));
            }
            catch { /* sem permissão */ }
        }
    }
}
