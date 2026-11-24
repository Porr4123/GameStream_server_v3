using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameStream.Server.Audio;

/// <summary>
/// Cala o jogo nas caixas do PC do servidor (o som segue para o celular). Faz o mesmo que "silenciar o aplicativo" no
/// Mixer de volume do Windows, só para o processo do jogo e os filhos dele; o resto do PC não é tocado.
///
/// O jogo só cria a "sessão de áudio" quando toca o primeiro som, então um vigia repete a busca a cada 500 ms e silencia
/// as sessões novas. Ao terminar, TUDO o que foi silenciado volta ao normal (o Windows lembra o silêncio por aplicativo).
/// </summary>
internal sealed class GameAudioMuter : IDisposable
{
    private static readonly Guid IidSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly int _rootPid;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private Thread? _thread;
    private int _muted;
    private bool _disposed;
    private Guid _ctx = Guid.Empty;

    public GameAudioMuter(int rootPid, ILogger log)
    {
        _rootPid = rootPid;
        _log = log;
    }

    /// <summary>Quantas sessões de áudio do jogo foram silenciadas até agora.</summary>
    public int MutedSessions => Volatile.Read(ref _muted);

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(6, 1);

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "GameStream mute" };
        _thread.Start();
    }

    private void Loop()
    {
        bool warned = false;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    var tree = ProcessTree.Of(_rootPid);
                    ForEachSession((pid, volume) =>
                    {
                        if (!tree.Contains(pid)) return;
                        volume.GetMute(out bool already);
                        if (already) return;
                        volume.SetMute(true, ref _ctx);
                        int n = Interlocked.Increment(ref _muted);
                        _log.LogInformation("Som do jogo silenciado no PC do servidor (processo {Pid}); o celular continua recebendo. Sessões: {N}.", pid, n);
                    });
                }
            }
            catch (Exception ex)
            {
                if (!warned)
                {
                    warned = true;
                    _log.LogWarning("Não consegui silenciar o jogo no PC ({Type}: {Msg}). O som continua nas caixas do servidor e no celular.",
                        ex.GetType().Name, ex.Message);
                }
            }
            _cts.Token.WaitHandle.WaitOne(500);
        }
    }

    /// <summary>Devolve o som do jogo ao normal. Chame ANTES de fechar o jogo.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        try { _thread?.Join(1500); } catch { /* ignora */ }
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                var tree = ProcessTree.Of(_rootPid);
                int restored = 0;
                ForEachSession((pid, volume) =>
                {
                    if (!tree.Contains(pid)) return;
                    volume.GetMute(out bool muted);
                    if (!muted) return;
                    volume.SetMute(false, ref _ctx);
                    restored++;
                });
                if (_muted > 0) _log.LogInformation("Som do jogo devolvido ao normal no PC ({N} sessão(ões)).", restored);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Não consegui desfazer o silêncio do jogo ({Msg}). Se ele ficar mudo da próxima vez, tire o mudo no Mixer de volume do Windows.", ex.Message);
            }
        }
        _cts.Dispose();
    }

    private static void ForEachSession(Action<int, ISimpleAudioVolume> action)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out var device)); // saída, multimídia
        var iid = IidSessionManager2;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out object mgrObj)); // CLSCTX_ALL
        var manager = (IAudioSessionManager2)mgrObj;
        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out var sessions));
        Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
        for (int i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out var control) < 0) continue;
            if (control is not IAudioSessionControl2 control2) continue;
            if (control2.GetProcessId(out uint pid) < 0 || pid == 0) continue;
            if (control is ISimpleAudioVolume volume) action((int)pid, volume);
        }
    }

    /// <summary>Processo raiz + descendentes (jogos que abrem um launcher e depois outro processo).</summary>
    private static class ProcessTree
    {
        public static HashSet<int> Of(int root)
        {
            var parents = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try
                {
                    var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (Process32First(snap, ref e))
                    {
                        do parents[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                        while (Process32Next(snap, ref e));
                    }
                }
                finally { CloseHandle(snap); }
            }

            var result = new HashSet<int> { root };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var (pid, parent) in parents)
                    if (!result.Contains(pid) && result.Contains(parent)) { result.Add(pid); grew = true; }
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public UIntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionCount, [MarshalAs(UnmanagedType.Interface)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl (herdada, mesma ordem da vtable)
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid ctx);
        [PreserveSig] int GetMute(out bool mute);
    }
}
