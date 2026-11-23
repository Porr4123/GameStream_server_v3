namespace GameStream.Server.Diagnostics;

internal static class DiagnosticsCleaner
{
    /// <summary>Apaga arquivos de diagnóstico mais velhos que <paramref name="keepDays"/> dias. Nunca lança.</summary>
    public static int Cleanup(string dir, int keepDays)
    {
        int removed = 0;
        try
        {
            if (keepDays <= 0 || !Directory.Exists(dir)) return 0;
            var limit = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < limit)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch { /* arquivo em uso: fica para a próxima */ }
            }
        }
        catch { /* ignora */ }
        return removed;
    }
}
