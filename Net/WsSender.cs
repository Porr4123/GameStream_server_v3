using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GameStream.Server.Streaming;

namespace GameStream.Server.Net;

/// <summary>Contadores do vídeo desde a última leitura (para o log de diagnóstico).</summary>
internal readonly record struct VideoStats(
    long FramesSent, long BytesSent, long FramesDropped, long Overflows, long MaxSendMs, int Queued, long KeyframesSent = 0);

/// <summary>Totais da sessão inteira (para o resumo no fim).</summary>
internal readonly record struct VideoTotals(long FramesSent, long BytesSent, long FramesDropped, long Overflows, long KeyframesSent);

/// <summary>
/// Um WebSocket só aceita um envio por vez. Este envelope serializa vídeo, "hello" e "pong",
/// que saem de tarefas diferentes.
///
/// Vídeo: os quadros entram numa fila CURTA e uma tarefa própria os envia. Antes, o codificador esperava
/// cada envio terminar; com Wi-Fi lento isso parava o ffmpeg, o atraso crescia sem limite e a imagem "congelava".
/// Agora, se a fila encher, os quadros pendentes são descartados e o envio só recomeça no próximo keyframe
/// (um quadro P solto, sem a referência, só produz lixo no decodificador). O ffmpeg nunca é bloqueado.
/// </summary>
internal sealed class WsSender
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Channel<byte[]> _video;
    private readonly int _maxQueued;

    // Áudio: fila própria e curta (~0,5 s). Se a rede não acompanha, descarta o mais antigo: som atrasado é pior que um estalo.
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(24)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private long _audioSent, _audioBytes;
    private long _recentMaxSendMs, _sampledOverflows;
    private volatile int _softMax;

    private bool _waitKey = true; // só a thread do codificador mexe aqui
    private long _sent, _bytes, _dropped, _overflows, _maxSendMs, _keys;
    private long _totSent, _totBytes, _totDropped, _totOverflows, _totKeys;

    /// <summary>Chamado com cada mensagem de vídeo DEPOIS de enviada (usado para gravar em disco o que o celular recebeu).</summary>
    public Action<byte[]>? OnVideoSent { get; set; }

    /// <param name="maxQueuedFrames">Quadros que podem esperar para sair. Passou disso, descarta até o próximo keyframe.</param>
    public WsSender(WebSocket ws, int maxQueuedFrames = 15)
    {
        _ws = ws;
        _maxQueued = Math.Max(2, maxQueuedFrames);
        _softMax = _maxQueued;
        _video = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_maxQueued)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite devolve false quando cheia
            SingleWriter = true,
            SingleReader = false                    // o produtor também esvazia a fila ao descartar
        });
    }

    public Task SendJsonAsync(object payload, CancellationToken ct) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, ct);

    public Task SendTextAsync(string text, CancellationToken ct) =>
        SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct);

    /// <summary>
    /// Chamado pela tarefa do codificador, uma vez por quadro. Nunca bloqueia.
    /// Mensagem binária: [1][flags (bit0 = keyframe)][quadro H.264 Annex B, sem AUD].
    /// </summary>
    public void EnqueueVideo(byte[] accessUnit)
    {
        bool key = AnnexBSplitter.IsKeyFrame(accessUnit);

        if (_waitKey)
        {
            if (!key)
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Increment(ref _totDropped);
                return;
            }
            _waitKey = false;
        }

        // _softMax: com fps menor (modos médio/leve) a fila cheia teria mais de 1 s de atraso; o limite acompanha o fps.
        if (_video.Reader.Count >= _softMax || !_video.Writer.TryWrite(BuildVideoMessage(accessUnit, key)))
        {
            // Fila cheia: a rede/o celular não acompanham. Esvazia e recomeça de um quadro que se decodifica sozinho.
            int flushed = 0;
            while (_video.Reader.TryRead(out _)) flushed++;
            Interlocked.Add(ref _dropped, flushed);
            Interlocked.Add(ref _totDropped, flushed);
            Interlocked.Increment(ref _overflows);
            Interlocked.Increment(ref _totOverflows);

            if (key)
            {
                _video.Writer.TryWrite(BuildVideoMessage(accessUnit, key));
            }
            else
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Increment(ref _totDropped);
                _waitKey = true;
            }
        }
    }

    /// <summary>Quadro AAC cru (sem cabeçalho ADTS). Mensagem binária: [2][0][quadro AAC]. Nunca bloqueia.</summary>
    public void EnqueueAudio(byte[] aacFrame)
    {
        var message = new byte[aacFrame.Length + 2];
        message[0] = 2;
        Buffer.BlockCopy(aacFrame, 0, message, 2, aacFrame.Length);
        _audio.Writer.TryWrite(message);
    }

    /// <summary>Envia a fila de áudio até a sessão acabar.</summary>
    public async Task RunAudioAsync(CancellationToken ct)
    {
        await foreach (var message in _audio.Reader.ReadAllAsync(ct))
        {
            await SendAsync(message, WebSocketMessageType.Binary, ct);
            Interlocked.Increment(ref _audioSent);
            Interlocked.Add(ref _audioBytes, message.Length);
        }
    }

    private async Task FlushAudioAsync(CancellationToken ct)
    {
        while (_audio.Reader.TryRead(out var pending))
        {
            await SendAsync(pending, WebSocketMessageType.Binary, ct);
            Interlocked.Increment(ref _audioSent);
            Interlocked.Add(ref _audioBytes, pending.Length);
        }
    }

    public (long Frames, long Bytes) AudioTotals => (Interlocked.Read(ref _audioSent), Interlocked.Read(ref _audioBytes));

    /// <summary>Envia a fila de vídeo até a sessão acabar.</summary>
    public async Task RunVideoAsync(CancellationToken ct)
    {
        var sw = new Stopwatch();
        await foreach (var message in _video.Reader.ReadAllAsync(ct))
        {
            // O som tem prioridade: o que já está na fila de áudio sai ANTES do próximo quadro de vídeo (no máximo um
            // quadro de vídeo grande, ex.: keyframe de 8 Mbps, atrasa o som; ele nunca espera a fila de vídeo inteira).
            await FlushAudioAsync(ct);

            sw.Restart();
            await SendAsync(message, WebSocketMessageType.Binary, ct);
            long ms = sw.ElapsedMilliseconds;

            Interlocked.Increment(ref _sent);
            Interlocked.Increment(ref _totSent);
            Interlocked.Add(ref _bytes, message.Length);
            Interlocked.Add(ref _totBytes, message.Length);
            if ((message[1] & 1) != 0)
            {
                Interlocked.Increment(ref _keys);
                Interlocked.Increment(ref _totKeys);
            }

            try { OnVideoSent?.Invoke(message); }
            catch { /* gravação de diagnóstico nunca derruba o vídeo */ }

            long max;
            while (ms > (max = Interlocked.Read(ref _maxSendMs)) &&
                   Interlocked.CompareExchange(ref _maxSendMs, ms, max) != max) { }
            while (ms > (max = Interlocked.Read(ref _recentMaxSendMs)) &&
                   Interlocked.CompareExchange(ref _recentMaxSendMs, ms, max) != max) { }
        }
    }

    /// <summary>Quantos quadros podem esperar na fila (acompanha o fps do modo de transmissão atual).</summary>
    public void SetQueueLimit(int frames) => _softMax = Math.Clamp(frames, 2, _maxQueued);

    /// <summary>Medição do último intervalo para o controle de qualidade (independente de <see cref="TakeStats"/>).</summary>
    public NetSample SampleNetwork()
    {
        long overflows = Interlocked.Read(ref _totOverflows);
        long delta = overflows - Interlocked.Exchange(ref _sampledOverflows, overflows);
        return new NetSample(delta, _video.Reader.Count, Interlocked.Exchange(ref _recentMaxSendMs, 0));
    }

    /// <summary>Lê e zera os contadores.</summary>
    public VideoStats TakeStats() => new(
        Interlocked.Exchange(ref _sent, 0),
        Interlocked.Exchange(ref _bytes, 0),
        Interlocked.Exchange(ref _dropped, 0),
        Interlocked.Exchange(ref _overflows, 0),
        Interlocked.Exchange(ref _maxSendMs, 0),
        _video.Reader.Count,
        Interlocked.Exchange(ref _keys, 0));

    public VideoTotals Totals => new(
        Interlocked.Read(ref _totSent), Interlocked.Read(ref _totBytes), Interlocked.Read(ref _totDropped),
        Interlocked.Read(ref _totOverflows), Interlocked.Read(ref _totKeys));

    internal static byte[] BuildVideoMessage(byte[] accessUnit, bool key)
    {
        int skip = AnnexBSplitter.SkipAud(accessUnit);
        int len = accessUnit.Length - skip;

        var message = new byte[len + 2];
        message[0] = 1;
        message[1] = (byte)(key ? 1 : 0);
        Buffer.BlockCopy(accessUnit, skip, message, 2, len);
        return message;
    }

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(new ReadOnlyMemory<byte>(data), type, true, ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
