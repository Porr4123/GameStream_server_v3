using System.Diagnostics;
using GameStream.Server.Config;
using GameStream.Server.Streaming;

namespace GameStream.Server.Diagnostics;

/// <summary>
/// Guarda em disco provas do que o servidor transmitiu:
///  - quadros .bmp (a imagem exata que foi para o codificador): abra e veja se está preta, parada ou normal;
///  - o fluxo H.264 .h264 exatamente como foi enviado ao celular: abra no PC (ffplay -f h264 arquivo.h264).
/// Nunca derruba a sessão: qualquer erro de disco só desliga a gravação.
/// </summary>
internal sealed class SessionRecorder : IDisposable
{
    private readonly string _dir;
    private readonly string _id;
    private readonly int _dumpSeconds;
    private readonly bool _saveFrames;
    private readonly ILogger _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private FileStream? _video;
    private bool _videoDone;
    private long _videoBytes;
    private int _framesSaved;

    public string VideoPath { get; }

    public SessionRecorder(string dir, string sessionId, DiagnosticsOptions options, ILogger log)
    {
        _dir = dir;
        _id = sessionId;
        _dumpSeconds = Math.Max(0, options.DumpH264Seconds);
        _saveFrames = options.SaveFrames;
        _log = log;
        VideoPath = Path.Combine(dir, $"video-{sessionId}.h264");

        try
        {
            Directory.CreateDirectory(dir);
            if (_dumpSeconds > 0) _video = new FileStream(VideoPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex)
        {
            _video = null;
            _log.LogWarning("Não consegui criar arquivos de diagnóstico em {Dir}: {Msg}", dir, ex.Message);
        }
    }

    /// <summary>Chamado com cada mensagem de vídeo que FOI enviada ([1][flags][H.264]). Grava só o H.264.</summary>
    public void WriteVideo(byte[] message)
    {
        lock (_lock)
        {
            if (_video is null || _videoDone) return;
            try
            {
                if (_clock.Elapsed.TotalSeconds >= _dumpSeconds)
                {
                    FinishVideoLocked();
                    return;
                }
                _video.Write(message, 2, message.Length - 2);
                _videoBytes += message.Length - 2;
            }
            catch
            {
                _videoDone = true;
                try { _video?.Dispose(); } catch { /* ignora */ }
                _video = null;
            }
        }
    }

    /// <summary>Salva um quadro BGRA como .bmp. Devolve o caminho, ou null se desligado/falhou.</summary>
    public string? SaveFrame(string tag, byte[]? bgra, int width, int height)
    {
        if (!_saveFrames || bgra is null || _framesSaved >= 12) return null;
        try
        {
            var path = Path.Combine(_dir, $"quadro-{_id}-{tag}.bmp");
            File.WriteAllBytes(path, FrameInspector.ToBmp24(bgra, width, height));
            _framesSaved++;
            return path;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não consegui salvar o quadro de diagnóstico: {Msg}", ex.Message);
            return null;
        }
    }

    private void FinishVideoLocked()
    {
        _videoDone = true;
        try { _video?.Dispose(); } catch { /* ignora */ }
        _video = null;
        _log.LogInformation("Gravei {Kb} KB do fluxo H.264 enviado ao celular em {Path} (teste no PC: ffplay -f h264 \"{Path}\")",
            _videoBytes / 1024, VideoPath, VideoPath);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_video is not null && !_videoDone) FinishVideoLocked();
        }
    }
}
