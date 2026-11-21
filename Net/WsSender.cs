using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GameStream.Server.Streaming;

namespace GameStream.Server.Net;

/// <summary>Contadores do vídeo desde a última leitura (para o log de diagnóstico).</summary>
internal readonly record struct VideoStats(
    long FramesSent, long BytesSent, long FramesDropped, long Overflows, long MaxSendMs, int Queued);

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

    private bool _waitKey = true; // só a thread do codificador mexe aqui
    private long _sent, _bytes, _dropped, _overflows, _maxSendMs;

    /// <param name="maxQueuedFrames">Quadros que podem esperar para sair. Passou disso, descarta até o próximo keyframe.</param>
    public WsSender(WebSocket ws, int maxQueuedFrames = 15)
    {
        _ws = ws;
        _maxQueued = Math.Max(2, maxQueuedFrames);
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
                return;
            }
            _waitKey = false;
        }

        if (!_video.Writer.TryWrite(BuildVideoMessage(accessUnit, key)))
        {
            // Fila cheia: a rede/o celular não acompanham. Esvazia e recomeça de um quadro que se decodifica sozinho.
            int flushed = 0;
            while (_video.Reader.TryRead(out _)) flushed++;
            Interlocked.Add(ref _dropped, flushed);
            Interlocked.Increment(ref _overflows);

            if (key)
            {
                _video.Writer.TryWrite(BuildVideoMessage(accessUnit, key));
            }
            else
            {
                Interlocked.Increment(ref _dropped);
                _waitKey = true;
            }
        }
    }

    /// <summary>Envia a fila de vídeo até a sessão acabar.</summary>
    public async Task RunVideoAsync(CancellationToken ct)
    {
        var sw = new Stopwatch();
        await foreach (var message in _video.Reader.ReadAllAsync(ct))
        {
            sw.Restart();
            await SendAsync(message, WebSocketMessageType.Binary, ct);
            long ms = sw.ElapsedMilliseconds;

            Interlocked.Increment(ref _sent);
            Interlocked.Add(ref _bytes, message.Length);
            long max;
            while (ms > (max = Interlocked.Read(ref _maxSendMs)) &&
                   Interlocked.CompareExchange(ref _maxSendMs, ms, max) != max) { }
        }
    }

    /// <summary>Lê e zera os contadores.</summary>
    public VideoStats TakeStats() => new(
        Interlocked.Exchange(ref _sent, 0),
        Interlocked.Exchange(ref _bytes, 0),
        Interlocked.Exchange(ref _dropped, 0),
        Interlocked.Exchange(ref _overflows, 0),
        Interlocked.Exchange(ref _maxSendMs, 0),
        _video.Reader.Count);

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
