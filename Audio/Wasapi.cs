using System.Runtime.InteropServices;

namespace GameStream.Server.Audio;

/// <summary>Formato do PCM que a captura entrega (e que o ffmpeg do áudio deve esperar).</summary>
internal sealed record PcmFormat(int Rate, int Channels, int Bits, bool IsFloat)
{
    public static readonly PcmFormat S16Stereo48k = new(48000, 2, 16, false);

    public int BytesPerSample => Bits / 8;
    public int BytesPerFrame => BytesPerSample * Channels;

    /// <summary>Nome do formato cru no ffmpeg (-f).</summary>
    public string FfmpegName => IsFloat
        ? (Bits == 32 ? "f32le" : throw new NotSupportedException($"PCM float de {Bits} bits não suportado"))
        : Bits switch
        {
            16 => "s16le",
            24 => "s24le",
            32 => "s32le",
            _ => throw new NotSupportedException($"PCM de {Bits} bits não suportado")
        };

    public override string ToString() => $"{Rate} Hz, {Channels} canais, {FfmpegName}";

    /// <summary>Lê um WAVEFORMATEX / WAVEFORMATEXTENSIBLE.</summary>
    public static PcmFormat FromWaveFormat(IntPtr p)
    {
        int tag = (ushort)Marshal.ReadInt16(p, 0);
        int channels = Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int bits = Marshal.ReadInt16(p, 14);
        bool isFloat = tag == 3;
        if (tag == 0xFFFE) isFloat = Marshal.ReadInt32(p, 24) == 3; // SubFormat: 1 = PCM, 3 = IEEE float
        return new PcmFormat(rate, channels, bits, isFloat);
    }

    /// <summary>Maior amplitude (0 a 32768) de um buffer neste formato.</summary>
    public int Peak(byte[] buf, int count)
    {
        int bps = BytesPerSample, peak = 0;
        for (int i = 0; i + bps <= count; i += bps)
        {
            int v;
            if (IsFloat)
            {
                float f = Math.Abs(BitConverter.ToSingle(buf, i));
                v = float.IsNaN(f) ? 0 : (int)Math.Min(f * 32768f, 32768f);
            }
            else if (Bits == 16) v = Math.Abs((short)(buf[i] | (buf[i + 1] << 8)));
            else if (Bits == 24) v = Math.Abs(((buf[i] << 8) | (buf[i + 1] << 16) | (buf[i + 2] << 24)) >> 16);
            else v = Math.Abs(BitConverter.ToInt32(buf, i) >> 16);
            if (v > peak) peak = v;
        }
        return peak;
    }

    /// <summary>Escreve uma amostra (-1..1) neste formato.</summary>
    public void WriteSample(byte[] dst, int offset, double value)
    {
        value = Math.Clamp(value, -1.0, 1.0);
        if (IsFloat)
        {
            BitConverter.GetBytes((float)value).CopyTo(dst, offset);
        }
        else if (Bits == 16)
        {
            short s = (short)(value * 32767);
            dst[offset] = (byte)(s & 0xFF);
            dst[offset + 1] = (byte)((s >> 8) & 0xFF);
        }
        else if (Bits == 24)
        {
            int s = (int)(value * 8388607);
            dst[offset] = (byte)(s & 0xFF);
            dst[offset + 1] = (byte)((s >> 8) & 0xFF);
            dst[offset + 2] = (byte)((s >> 16) & 0xFF);
        }
        else
        {
            BitConverter.GetBytes((int)(value * 2147483647.0)).CopyTo(dst, offset);
        }
    }
}

internal sealed record AudioDeviceInfo(string Id, string Name, int State)
{
    public bool IsActive => State == 1;
    public string StateText => State switch { 1 => "ativo", 2 => "desativado", 4 => "ausente", 8 => "DESCONECTADO (sem cabo/jack)", _ => "estado " + State };
}

/// <summary>Lista e abre dispositivos de reprodução (saída) do Windows para captura por loopback.</summary>
internal static class Wasapi
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid PkeyFriendlyNameGuid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    public static List<AudioDeviceInfo> ListRenderDevices()
    {
        var result = new List<AudioDeviceInfo>();
        var enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 0xF, out var collection)); // saída; ativos, desativados, ausentes, desconectados
        Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out var device) < 0) continue;
            device.GetId(out string id);
            device.GetState(out int state);
            result.Add(new AudioDeviceInfo(id, FriendlyName(device), state));
        }
        return result;
    }

    /// <summary>Abre o IAudioClient do dispositivo com o id dado.</summary>
    public static IAudioClient OpenClient(string deviceId)
    {
        var enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
        Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out var device));
        var iid = IidAudioClient;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out object obj)); // CLSCTX_ALL
        return (IAudioClient)obj;
    }

    private static string FriendlyName(IAudioDevice device)
    {
        IntPtr pv = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
            if (device.OpenPropertyStore(0, out var store) < 0) return "(sem nome)";
            var key = new PropertyKey { Fmtid = PkeyFriendlyNameGuid, Pid = 14 }; // PKEY_Device_FriendlyName
            if (store.GetValue(ref key, pv) < 0) return "(sem nome)";
            int vt = (ushort)Marshal.ReadInt16(pv, 0);
            string name = vt == 31 ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) ?? "(sem nome)" : "(sem nome)";
            PropVariantClear(pv);
            return name;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr pv);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IAudioDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IAudioDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IAudioDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
    }
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
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
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

/// <summary>Fonte de áudio do jogo (por processo ou por dispositivo). Entrega PCM no <see cref="Format"/>.</summary>
internal interface IAudioCapture : IDisposable
{
    PcmFormat Format { get; }
    string Mode { get; }
    long LoudPackets { get; }
    int TakePeak();
    void Open(int processId);
    int Read(Stream sink, int timeoutMs);
}
