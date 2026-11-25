using System.Runtime.InteropServices;

namespace GameStream.Server.Sandbox;

/// <summary>
/// Recursos de um jogo. Padrão: núcleos DINÂMICOS (metade dos núcleos lógicos, de 1 a 4, começando no núcleo 0) para o jogo e o resto
/// para servidor + ffmpeg. A memória é DINÂMICA por padrão (memoryMb = 0): a cada jogo que abre, o teto é a RAM livre
/// naquele momento menos uma reserva para Windows, servidor e ffmpeg.
/// </summary>
public sealed record ResourceLimits(int Cores, int MemoryMb, ulong GameMask, ulong ServerMask, bool MemoryIsDynamic = false)
{
    /// <summary>Menor teto automático (MB): nunca estrangula um jogo abaixo disso.</summary>
    public const int MinAutoMb = 1024;

    /// <summary>RAM (MB) deixada livre para Windows, servidor e ffmpeg quando o teto é automático.</summary>
    public const int DefaultReserveMb = 768;

    /// <summary>
    /// Núcleos do jogo no modo dinâmico (cores &lt;= 0): metade dos núcleos lógicos, no mínimo 1 e no máximo 4.
    /// A outra metade fica para o servidor, o x264 e o ffmpeg do som. Ex.: 4 núcleos = 2 para o jogo.
    /// </summary>
    public static int AutoCores(int? cpus = null)
    {
        int n = Math.Min(cpus ?? Environment.ProcessorCount, 64);
        return Math.Clamp(n / 2, 1, 4);
    }

    /// <param name="memoryMb">&gt; 0 = limite fixo em MB. 0 (ou negativo) = dinâmico: RAM livre agora menos <paramref name="reserveMb"/>.</param>
    /// <param name="availableMb">Quanta RAM está livre agora (injetável para teste). null = pergunta ao sistema.</param>
    public static ResourceLimits Compute(int cores, int memoryMb, int coreStart,
        int reserveMb = DefaultReserveMb, Func<long>? availableMb = null)
    {
        int cpus = Math.Min(Environment.ProcessorCount, 64);
        if (cores <= 0) cores = AutoCores(cpus); // dinâmico
        cores = Math.Clamp(cores, 1, cpus);
        coreStart = Math.Clamp(coreStart, 0, cpus - cores);

        ulong all = cpus == 64 ? ulong.MaxValue : (1UL << cpus) - 1;
        ulong game = (cores == 64 ? ulong.MaxValue : (1UL << cores) - 1) << coreStart;
        ulong server = all & ~game;
        if (server == 0) server = all; // jogo usando todos os núcleos: ffmpeg divide com ele

        if (memoryMb > 0)
            return new ResourceLimits(cores, Math.Max(256, memoryMb), game, server);

        long free = (availableMb ?? SystemMemory.AvailableMb)();
        int auto = (int)Math.Clamp(free - Math.Max(0, reserveMb), MinAutoMb, int.MaxValue);
        return new ResourceLimits(cores, auto, game, server, MemoryIsDynamic: true);
    }
}

/// <summary>RAM física livre agora e total, em MB.</summary>
public static class SystemMemory
{
    public static long AvailableMb() => Query().Available;
    public static long TotalMb() => Query().Total;

    private static (long Available, long Total) Query()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref s))
                    return ((long)(s.ullAvailPhys / (1024 * 1024)), (long)(s.ullTotalPhys / (1024 * 1024)));
            }
        }
        catch { /* cai no valor abaixo */ }

        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        return (total, total);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
