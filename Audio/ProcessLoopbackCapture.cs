using System.Runtime.InteropServices;

namespace GameStream.Server.Audio;

/// <summary>
/// Captura o som de UM processo (e dos processos filhos dele) com a "captura de loopback por processo" do WASAPI
/// (Windows 10 versão 2004 / build 19041 ou mais novo). Só o áudio do jogo entra: notificações, música e
/// outros programas do PC NUNCA vão para o celular. Entrega PCM 16 bits, 48 kHz, estéreo.
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerFrame = Channels * 2;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    private const string VirtualDevice = "VAD\\Process_Loopback";
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const int AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;
    private const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private EventWaitHandle? _event;
    private bool _started;
    private long _loud;

    /// <summary>Pacotes que tinham som de verdade (não só zeros).</summary>
    public long LoudPackets => Interlocked.Read(ref _loud);

    /// <summary>Abre a captura do processo. Lança exceção com a causa (todas as tentativas) se o Windows recusar.</summary>
    public void Open(int processId)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("precisa do Windows 10 versão 2004 (build 19041) ou mais novo");

        // Cada combinação de flags/duração é tentada num IAudioClient NOVO (um Initialize que falhou não é confiável de repetir).
        // O dispositivo virtual de loopback por processo é exigente e varia entre versões do Windows.
        const int L = AUDCLNT_STREAMFLAGS_LOOPBACK, E = AUDCLNT_STREAMFLAGS_EVENTCALLBACK, N = AUDCLNT_STREAMFLAGS_NOPERSIST,
            C = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        var attempts = new (string Name, int Flags, long Duration)[]
        {
            ("loopback+evento+nopersist", L | E | N, 200_000),
            ("loopback+evento", L | E, 0),
            ("loopback+evento+conversão", L | E | C, 0),
            ("loopback+nopersist (sem evento)", L | N, 200_000),
            ("loopback (sem evento)", L, 0),
            ("loopback+conversão (sem evento)", L | C, 0),
        };

        var errors = new List<string>();
        foreach (var (name, flags, duration) in attempts)
        {
            IAudioClient? client = null;
            IntPtr fmt = IntPtr.Zero;
            try
            {
                client = Activate(processId);

                // PCM 16 bits, 48 kHz, estéreo (GetMixFormat não existe neste dispositivo virtual: o formato é escolhido aqui).
                fmt = Marshal.AllocHGlobal(18);
                Marshal.WriteInt16(fmt, 0, 1);                       // WAVE_FORMAT_PCM
                Marshal.WriteInt16(fmt, 2, Channels);
                Marshal.WriteInt32(fmt, 4, SampleRate);
                Marshal.WriteInt32(fmt, 8, SampleRate * BytesPerFrame);
                Marshal.WriteInt16(fmt, 12, BytesPerFrame);
                Marshal.WriteInt16(fmt, 14, 16);
                Marshal.WriteInt16(fmt, 16, 0);

                int hr = client.Initialize(0, flags, duration, 0, fmt, IntPtr.Zero);
                if (hr < 0)
                {
                    errors.Add($"{name}: 0x{hr:X8}");
                    continue;
                }

                bool useEvent = (flags & E) != 0;
                EventWaitHandle? ev = null;
                if (useEvent)
                {
                    ev = new EventWaitHandle(false, EventResetMode.AutoReset);
                    hr = client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                    if (hr < 0)
                    {
                        ev.Dispose();
                        errors.Add($"{name} (SetEventHandle): 0x{hr:X8}");
                        continue;
                    }
                }

                var capIid = IidCaptureClient;
                hr = client.GetService(ref capIid, out object cap);
                if (hr < 0)
                {
                    ev?.Dispose();
                    errors.Add($"{name} (GetService): 0x{hr:X8}");
                    continue;
                }

                hr = client.Start();
                if (hr < 0)
                {
                    ev?.Dispose();
                    errors.Add($"{name} (Start): 0x{hr:X8}");
                    continue;
                }

                _client = client;
                _capture = (IAudioCaptureClient)cap;
                _event = ev;
                _started = true;
                Mode = name;
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
            }
        }

        throw new InvalidOperationException("o Windows recusou todas as formas de abrir a captura: " + string.Join("; ", errors));
    }

    /// <summary>Combinação de flags que funcionou (para o log).</summary>
    public string Mode { get; private set; } = "";

    private static IAudioClient Activate(int processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { type = PROCESS_LOOPBACK(1); pid; mode = INCLUDE_TARGET_PROCESS_TREE(0) }
        IntPtr prm = Marshal.AllocHGlobal(12);
        IntPtr pv = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(prm, 0, 1);
            Marshal.WriteInt32(prm, 4, processId);
            Marshal.WriteInt32(prm, 8, 0);

            // PROPVARIANT VT_BLOB (0x41) apontando para os parâmetros.
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 0x41);
            Marshal.WriteInt32(pv, 8, 12);
            Marshal.WriteIntPtr(pv, 8 + IntPtr.Size, prm);

            var iid = IidAudioClient;
            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevice, ref iid, pv, handler, out var op);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("o Windows não respondeu ao pedido de captura de áudio");

            int hr = op.GetActivateResult(out int activateHr, out object? obj);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (activateHr < 0) Marshal.ThrowExceptionForHR(activateHr);
            return (IAudioClient)obj!;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(prm);
        }
    }

    /// <summary>Espera até <paramref name="timeoutMs"/> por áudio e devolve tudo o que estiver pronto (silêncio vira zeros).</summary>
    public int Read(Stream sink, int timeoutMs)
    {
        if (_event != null) _event.WaitOne(timeoutMs);
        else Thread.Sleep(Math.Min(timeoutMs, 5));
        int total = 0;
        while (true)
        {
            Check(_capture!.GetNextPacketSize(out uint frames));
            if (frames == 0) break;

            Check(_capture.GetBuffer(out IntPtr data, out uint got, out uint flags, out _, out _));
            int bytes = (int)got * BytesPerFrame;
            var chunk = new byte[bytes];
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && bytes > 0) Marshal.Copy(data, chunk, 0, bytes);
            _capture.ReleaseBuffer(got);
            if (bytes > 0 && chunk.AsSpan().IndexOfAnyExcept((byte)0) >= 0) Interlocked.Increment(ref _loud);

            sink.Write(chunk, 0, bytes);
            total += bytes;
        }
        return total;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        try { if (_started) _client?.Stop(); } catch { /* ignora */ }
        _started = false;
        _capture = null;
        _client = null; // o RCW é liberado pelo coletor
        _event?.Dispose();
        _event = null;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    /// <summary>Marca o objeto como "agile": o Windows chama o handler de outra thread.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Done.Set();
            return 0;
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
