namespace GameStream.Server.Streaming;

/// <summary>Os três modos de transmissão. Leve = rede ruim, Pesado = rede boa.</summary>
internal enum QualityLevel { Leve = 0, Medio = 1, Pesado = 2 }

/// <summary>O que cada modo pede ao codificador. O tamanho do quadro NÃO muda (o celular não precisa trocar de decodificador).</summary>
internal readonly record struct QualityPlan(QualityLevel Level, int Fps, int BitrateKbps)
{
    /// <param name="profileFps">fps do game.json (teto do modo Pesado).</param>
    /// <param name="baseKbps">Taxa do modo Pesado (game.json, ajustada ao tamanho do quadro).</param>
    public static QualityPlan For(QualityLevel level, int profileFps, int baseKbps)
    {
        profileFps = Math.Clamp(profileFps, 1, 60);
        baseKbps = Math.Max(400, baseKbps);
        return level switch
        {
            QualityLevel.Pesado => new(level, profileFps, baseKbps),
            QualityLevel.Medio => new(level, Math.Min(profileFps, 30), Math.Max(400, baseKbps * 55 / 100)),
            _ => new(level, Math.Min(profileFps, 24), Math.Max(300, baseKbps * 28 / 100))
        };
    }

    public static string Name(QualityLevel level) => level switch { QualityLevel.Leve => "leve", QualityLevel.Medio => "médio", _ => "pesado" };
}

/// <summary>O que o envio de vídeo mediu no último segundo.</summary>
/// <param name="Overflows">Vezes que a fila encheu e foi descartada neste segundo.</param>
/// <param name="Queued">Quadros esperando para sair agora.</param>
/// <param name="MaxSendMs">Maior tempo de um envio neste segundo (o Wi-Fi/celular demorando para aceitar).</param>
internal readonly record struct NetSample(long Overflows, int Queued, long MaxSendMs);

/// <summary>
/// Escolhe o modo de transmissão pela qualidade da rede. Chame <see cref="Update"/> uma vez por segundo.
/// Desce rápido (2 segundos ruins nos últimos 4) e sobe devagar (20 s seguidos bons, e cada vez que a subida falha o
/// tempo de espera dobra, até 2 min), para não ficar alternando entre dois modos.
/// </summary>
internal sealed class QualityController
{
    private const int DownWindow = 4;
    private const int DownBadSeconds = 2;
    private const int DownCooldown = 4; // entre duas descidas seguidas (depois de subir, pode descer logo)
    private const int UpWaitBase = 20;
    private const int UpWaitMax = 120;

    private readonly bool _adaptive;
    private readonly int _queueLimit;
    private readonly Queue<bool> _recent = new();
    private int _now;
    private int _lastChange = -1000;
    private int _goodStreak;
    private int _upWait = UpWaitBase;
    private int _lastUp = -1000;
    private int _lastDown = -1000;

    public QualityController(QualityLevel start, bool adaptive, int queueLimit = 6)
    {
        Level = start;
        _adaptive = adaptive;
        _queueLimit = Math.Max(2, queueLimit);
    }

    public QualityLevel Level { get; private set; }
    public int Changes { get; private set; }

    /// <summary>"auto", "leve", "medio" ou "pesado" (aceita acento e maiúsculas). Auto começa no médio e sobe se a rede aguentar.</summary>
    public static QualityController FromMode(string? mode, int queueLimit)
    {
        string m = (mode ?? "auto").Trim().ToLowerInvariant().Replace("é", "e");
        return m switch
        {
            "leve" => new(QualityLevel.Leve, false, queueLimit),
            "medio" => new(QualityLevel.Medio, false, queueLimit),
            "pesado" => new(QualityLevel.Pesado, false, queueLimit),
            _ => new(QualityLevel.Medio, true, queueLimit)
        };
    }

    public bool IsAdaptive => _adaptive;

    /// <returns>O modo novo, ou null se continua o mesmo. <paramref name="reason"/> explica a troca.</returns>
    public QualityLevel? Update(NetSample s, out string reason)
    {
        reason = "";
        _now++;
        bool bad = s.Overflows > 0 || s.Queued >= _queueLimit || s.MaxSendMs >= 300;

        _recent.Enqueue(bad);
        while (_recent.Count > DownWindow) _recent.Dequeue();
        _goodStreak = bad ? 0 : _goodStreak + 1;

        if (!_adaptive) return null;

        if (Level > QualityLevel.Leve && _recent.Count(b => b) >= DownBadSeconds && _now - _lastDown >= DownCooldown)
        {
            // Se acabamos de subir e já deu problema, a subida foi cedo demais: espera mais antes da próxima.
            if (_now - _lastUp < 45) _upWait = Math.Min(_upWait * 2, UpWaitMax);
            Level--;
            _lastDown = _now;
            return Changed(s, "a rede não acompanhou (fila/descartes/envio lento)", out reason);
        }

        if (Level < QualityLevel.Pesado && _goodStreak >= _upWait && _now - _lastChange >= _upWait)
        {
            Level++;
            _lastUp = _now;
            return Changed(s, $"rede estável há {_goodStreak} s", out reason);
        }

        if (Level == QualityLevel.Pesado && _goodStreak >= 120) _upWait = UpWaitBase; // rede boa por muito tempo: esquece o histórico
        return null;
    }

    private QualityLevel? Changed(NetSample s, string why, out string reason)
    {
        reason = $"{why} [descartes {s.Overflows}, fila {s.Queued}, envio mais lento {s.MaxSendMs} ms]";
        _lastChange = _now;
        _goodStreak = 0;
        _recent.Clear();
        Changes++;
        return Level;
    }
}
