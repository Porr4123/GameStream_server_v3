namespace GameStream.Server.Sandbox;

/// <summary>
/// Recursos de um jogo. Num i5-3470 (4 núcleos, sem hyper-threading) com 2 núcleos por jogo:
/// o jogo fica nos núcleos 0 e 1 (máscara 0b0011) e servidor + ffmpeg nos núcleos 2 e 3 (0b1100).
/// </summary>
public sealed record ResourceLimits(int Cores, int MemoryMb, ulong GameMask, ulong ServerMask)
{
    public static ResourceLimits Compute(int cores, int memoryMb, int coreStart)
    {
        int cpus = Math.Min(Environment.ProcessorCount, 64);
        cores = Math.Clamp(cores, 1, cpus);
        coreStart = Math.Clamp(coreStart, 0, cpus - cores);

        ulong all = cpus == 64 ? ulong.MaxValue : (1UL << cpus) - 1;
        ulong game = (cores == 64 ? ulong.MaxValue : (1UL << cores) - 1) << coreStart;
        ulong server = all & ~game;
        if (server == 0) server = all; // jogo usando todos os núcleos: ffmpeg divide com ele

        return new ResourceLimits(cores, Math.Max(256, memoryMb), game, server);
    }
}
