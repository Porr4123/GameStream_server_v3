using System.Runtime.InteropServices;

namespace GameStream.Server.Audio;

/// <summary>
/// Captura (loopback) de um dispositivo de reprodução inteiro, escolhido pelo nome. O jogo é apontado para esse dispositivo
/// no Windows (Configurações > Som > Volume do aplicativo e preferências de dispositivo), que pode ser uma saída que ninguém
/// ouve (ex.: "Digital Audio (S/PDIF)" sem nada ligado). Assim o PC fica em silêncio e o celular ouve o jogo, sem mexer no
/// volume do aplicativo. TUDO o que tocar nesse dispositivo vai para o celular: ponha só o jogo nele.
/// </summary>
internal sealed class DeviceLoopbackCapture : IAudioCapture
{
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly string _nameFragment;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private bool _started;
    private long _loud;
    private int _peak;

    public DeviceLoopbackCapture(string nameFragment) => _nameFragment = nameFragment.Trim();

    public PcmFormat Format { get; private set; } = PcmFormat.S16Stereo48k;
    public string Mode { get; private set; } = "";
    public long LoudPackets => Interlocked.Read(ref _loud);
    public int TakePeak() => Interlocked.Exchange(ref _peak, 0);

    public void Open(int processId)
    {
        var devices = Wasapi.ListRenderDevices();
        var matches = devices.Where(d => d.Name.Contains(_nameFragment, StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = matches.FirstOrDefault(d => d.IsActive);

        if (chosen is null)
        {
            string list = string.Join("; ", devices.Select(d => $"\"{d.Name}\" [{d.StateText}]"));
            if (matches.Count > 0)
                throw new InvalidOperationException(
                    $"o dispositivo \"{matches[0].Name}\" existe mas está {matches[0].StateText}: o Windows só permite capturar dispositivos ATIVOS. " +
                    $"Ative-o em Som > Reprodução. Dispositivos: {list}");
            throw new InvalidOperationException($"nenhum dispositivo de saída tem \"{_nameFragment}\" no nome. Dispositivos: {list}");
        }

        var client = Wasapi.OpenClient(chosen.Id);
        Marshal.ThrowExceptionForHR(client.GetMixFormat(out IntPtr fmt));
        try
        {
            Format = PcmFormat.FromWaveFormat(fmt);
            _ = Format.FfmpegName; // lança se o formato não for suportado

            // Loopback compartilhado no formato de mistura do dispositivo, lido por polling (compatível com todo Windows 10).
            Marshal.ThrowExceptionForHR(client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 1_000_000, 0, fmt, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeCoTaskMem(fmt);
        }

        var capIid = IidCaptureClient;
        Marshal.ThrowExceptionForHR(client.GetService(ref capIid, out object cap));
        _capture = (IAudioCaptureClient)cap;
        Marshal.ThrowExceptionForHR(client.Start());
        _client = client;
        _started = true;
        Mode = $"dispositivo \"{chosen.Name}\", {Format}";
    }

    public int Read(Stream sink, int timeoutMs)
    {
        Thread.Sleep(Math.Min(timeoutMs, 5));
        int total = 0;
        while (true)
        {
            Marshal.ThrowExceptionForHR(_capture!.GetNextPacketSize(out uint frames));
            if (frames == 0) break;

            Marshal.ThrowExceptionForHR(_capture.GetBuffer(out IntPtr data, out uint got, out uint flags, out _, out _));
            int bytes = (int)got * Format.BytesPerFrame;
            var chunk = new byte[bytes];
            if ((flags & 0x2) == 0 && bytes > 0) Marshal.Copy(data, chunk, 0, bytes); // 0x2 = SILENT
            _capture.ReleaseBuffer(got);

            if (bytes > 0 && chunk.AsSpan().IndexOfAnyExcept((byte)0) >= 0) Interlocked.Increment(ref _loud);
            int peak = Format.Peak(chunk, bytes);
            if (peak > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, peak);

            sink.Write(chunk, 0, bytes);
            total += bytes;
        }
        return total;
    }

    public void Dispose()
    {
        try { if (_started) _client?.Stop(); } catch { /* ignora */ }
        _started = false;
        _capture = null;
        _client = null;
    }
}
