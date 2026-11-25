using System.Runtime.InteropServices;

namespace GameStream.Server.Interop;

/// <summary>
/// Ajustes do próprio servidor para ele continuar rápido e conseguindo dar foco ao jogo MESMO em segundo plano
/// (outra janela na frente, console minimizado). Sem isto o Windows pode:
///  - arredondar os temporizadores para ~15,6 ms em processos que não estão em primeiro plano (o vídeo cai para ~30 fps);
///  - tratar o servidor como "eficiência" (EcoQoS) e reduzir a CPU dele;
///  - recusar o SetForegroundWindow ("bloqueio de foreground"), e o servidor descarta as teclas por segurança.
/// Nada aqui injeta tecla nem mexe em outro programa.
/// </summary>
internal static class ProcessTuning
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingExecutionSpeed = 0x1;
    private const uint PowerThrottlingIgnoreTimerResolution = 0x4;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x2;

    /// <summary>Aplica tudo e devolve uma linha para o log com o que funcionou.</summary>
    public static string Apply()
    {
        if (!OperatingSystem.IsWindows()) return "ajustes do Windows: não se aplica";
        var parts = new List<string>();

        try { parts.Add(timeBeginPeriod(1) == 0 ? "temporizador de 1 ms: ok" : "temporizador de 1 ms: recusado"); }
        catch (Exception ex) { parts.Add("temporizador: " + ex.GetType().Name); }

        try
        {
            var state = new PowerThrottlingState
            {
                Version = 1,
                ControlMask = PowerThrottlingExecutionSpeed | PowerThrottlingIgnoreTimerResolution,
                StateMask = 0 // 0 = NÃO reduzir (desliga o EcoQoS e o arredondamento do temporizador em segundo plano)
            };
            bool ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            parts.Add(ok ? "sem redução de CPU em segundo plano: ok" : "sem redução de CPU em segundo plano: não suportado nesta versão do Windows");
        }
        catch (Exception ex) { parts.Add("redução de CPU: " + ex.GetType().Name); }

        try
        {
            // Tempo de bloqueio de foreground = 0: o Windows deixa o servidor trazer o jogo para a frente sem exigir que ele esteja ativo.
            bool ok = SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
            parts.Add(ok ? "bloqueio de foreground desligado: ok" : "bloqueio de foreground: o Windows recusou (rode o servidor com a janela dele na frente uma vez)");
        }
        catch (Exception ex) { parts.Add("bloqueio de foreground: " + ex.GetType().Name); }

        return "Ajustes do servidor: " + string.Join("; ", parts);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
}
