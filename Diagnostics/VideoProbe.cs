using System.Diagnostics;
using GameStream.Server.Streaming;

namespace GameStream.Server.Diagnostics;

/// <summary>Resumo do vídeo numa janela de tempo (desde a última leitura).</summary>
internal readonly record struct VideoProbeSnapshot(
    int Frames,
    int Changes,
    double AvgCaptureMs,
    double MaxCaptureMs,
    double BlackRatio,
    double MeanLuma,
    double StillSeconds,
    long TotalFrames,
    long TotalChanges);

/// <summary>
/// Olha cada quadro que vai para o codificador e responde a pergunta central do "congelou":
/// a imagem está MUDANDO? está PRETA? há quanto tempo está IGUAL? Também guarda uma cópia do quadro mais
/// recente (no máximo 2 por segundo) para a página /debug e para salvar em disco.
/// </summary>
internal sealed class VideoProbe
{
    private readonly object _lock = new();
    private readonly Func<TimeSpan> _clock;
    private int _width, _height;

    private bool _hasHash;
    private ulong _lastHash;
    private TimeSpan _lastChange;
    private int _frames, _changes;
    private double _captureSum, _captureMax;
    private FrameStats _lastStats;
    private long _totalFrames, _totalChanges;

    private byte[]? _latest;
    private TimeSpan _latestAt = TimeSpan.MinValue;

    public VideoProbe(int width, int height, Func<TimeSpan>? clock = null)
    {
        _width = width;
        _height = height;

        if (clock is null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed;
        }
        _clock = clock;
        _lastChange = _clock();
    }

    /// <summary>Tamanho do quadro mais recente (muda quando o jogo passa a ser mostrado no tamanho nativo).</summary>
    public int Width { get { lock (_lock) return _width; } }
    public int Height { get { lock (_lock) return _height; } }

    /// <summary>Chamado uma vez por quadro, pela tarefa do codificador, com o último tamanho conhecido.</summary>
    public void Record(byte[] frame, TimeSpan captureTime) => Record(frame, _width, _height, captureTime);

    /// <summary>Chamado uma vez por quadro, pela tarefa do codificador.</summary>
    public void Record(byte[] frame, int width, int height, TimeSpan captureTime)
    {
        // Em quadros grandes olha 1 pixel de cada 4 para o custo ficar em ~1 ms.
        int step = (long)width * height > 1_000_000 ? 4 : 2;
        var stats = FrameInspector.Analyze(frame, width, height, step);
        var now = _clock();

        lock (_lock)
        {
            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                _latest = null; // a cópia guardada era de outro tamanho
                _hasHash = false;
            }

            _frames++;
            _totalFrames++;
            _captureSum += captureTime.TotalMilliseconds;
            _captureMax = Math.Max(_captureMax, captureTime.TotalMilliseconds);
            _lastStats = stats;

            if (!_hasHash || stats.Hash != _lastHash)
            {
                _hasHash = true;
                _lastHash = stats.Hash;
                _lastChange = now;
                _changes++;
                _totalChanges++;
            }

            if (_latest is null || now - _latestAt >= TimeSpan.FromMilliseconds(500))
            {
                int bytes = width * height * 4;
                _latest ??= new byte[bytes];
                Buffer.BlockCopy(frame, 0, _latest, 0, Math.Min(Math.Min(frame.Length, bytes), _latest.Length));
                _latestAt = now;
            }
        }
    }

    /// <summary>Lê e zera os contadores da janela. "Parada há" e os totais continuam acumulando.</summary>
    public VideoProbeSnapshot Take()
    {
        lock (_lock)
        {
            var snap = new VideoProbeSnapshot(
                _frames, _changes,
                _frames == 0 ? 0 : _captureSum / _frames, _captureMax,
                _lastStats.BlackRatio, _lastStats.MeanLuma,
                StillSecondsLocked(), _totalFrames, _totalChanges);

            _frames = _changes = 0;
            _captureSum = _captureMax = 0;
            return snap;
        }
    }

    /// <summary>Há quantos segundos a imagem está exatamente igual.</summary>
    public double StillSeconds
    {
        get { lock (_lock) return StillSecondsLocked(); }
    }

    /// <summary>
    /// Zera a contagem de "parada há" (ex.: no instante em que o jogo passa a ser mostrado,
    /// para a tela de espera anterior não entrar na conta).
    /// </summary>
    public void ResetStill()
    {
        lock (_lock) _lastChange = _clock();
    }

    /// <summary>Cópia do quadro mais recente com o tamanho dele (lidos juntos), ou null.</summary>
    public (byte[] Bgra, int W, int H)? Latest()
    {
        lock (_lock) return _latest is null ? null : ((byte[])_latest.Clone(), _width, _height);
    }

    /// <summary>Cópia (BGRA) do quadro mais recente guardado, ou null se ainda não houve nenhum.</summary>
    public byte[]? LatestFrame()
    {
        lock (_lock) return _latest is null ? null : (byte[])_latest.Clone();
    }

    private double StillSecondsLocked() => Math.Max(0, (_clock() - _lastChange).TotalSeconds);
}
