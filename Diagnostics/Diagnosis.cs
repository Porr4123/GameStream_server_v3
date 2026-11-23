namespace GameStream.Server.Diagnostics;

internal enum FindingLevel { Info, Warning, Error }

/// <summary>Um achado do diagnóstico automático: código estável (para o teste e para buscar no log) + explicação em português.</summary>
internal readonly record struct Finding(string Code, FindingLevel Level, string Message);

/// <summary>Tudo o que as regras precisam saber. Valores simples: dá para testar sem Windows.</summary>
internal sealed record DiagInput
{
    // vídeo / rede
    public long FramesSentTotal { get; init; }
    public long DroppedInWindow { get; init; }

    // jogo
    public bool GameLaunched { get; init; }
    public bool GameShown { get; init; }
    public bool ProcessAlive { get; init; } = true;
    public bool WindowExists { get; init; } = true;
    public bool WindowHung { get; init; }
    public bool WindowMinimized { get; init; }
    public bool WindowForeground { get; init; }
    public double GameCpuPercent { get; init; }

    // imagem
    public double StillSeconds { get; init; }
    public double BlackRatio { get; init; }

    // captura
    public double AvgCaptureMs { get; init; }
    public long CaptureAttempts { get; init; }
    public long CaptureFailures { get; init; }

    // teclas
    public long KeysAccepted { get; init; }
    public long KeysRejected { get; init; }
    public double? SecondsSinceLastKey { get; init; }
    public long FocusFailures { get; init; }
    public string ForegroundWindow { get; init; } = "";

    // configuração
    public bool PrintWindowMode { get; init; } = true;
    public bool HideWindow { get; init; } = true;
    public string HideMode { get; init; } = "";
}

/// <summary>
/// Transforma números em hipóteses. Cada regra diz o que foi visto e o que testar a seguir.
/// Não adivinha sozinha a causa: aponta a mais provável com base no que o servidor consegue medir.
/// </summary>
internal static class Diagnosis
{
    public const double StillThresholdSeconds = 8;

    public static List<Finding> Evaluate(DiagInput i)
    {
        var f = new List<Finding>();

        if (i.FramesSentTotal == 0)
            f.Add(new("SEM_VIDEO", FindingLevel.Error,
                "Nenhum quadro de vídeo foi enviado ao cliente ainda. Se isso persiste, o ffmpeg não está produzindo vídeo " +
                "(veja as linhas [ffmpeg] acima) ou a conexão não escoa dados."));

        if (i.DroppedInWindow > 0)
            f.Add(new("REDE_LENTA", FindingLevel.Warning,
                $"{i.DroppedInWindow} quadro(s) descartado(s) porque o celular/Wi-Fi não acompanhou. A imagem pausa até 1 s e segue. " +
                "Se for constante: aproxime-se do roteador, use Wi-Fi 5 GHz ou reduza bitrateKbps/fps no game.json."));

        if (!i.GameLaunched) return f;

        if (!i.ProcessAlive && !i.WindowExists)
            f.Add(new("JOGO_FECHOU", FindingLevel.Warning,
                "O jogo não está mais aberto (processo e janela sumiram). O vídeo mostra a tela de espera."));

        if (!i.GameShown) return f;

        if (i.WindowHung)
            f.Add(new("JANELA_TRAVADA", FindingLevel.Error,
                "O Windows marcou a janela do jogo como \"Não está respondendo\": o jogo travou DE VERDADE. " +
                "O servidor repete a última imagem para o vídeo não parar. Abra o jogo no PC, fora do servidor, e veja se ele trava igual " +
                "(pode ser o limite de memória/CPU do jogo: GameMemoryMb / GameCores)."));

        if (i.WindowMinimized)
            f.Add(new("JANELA_MINIMIZADA", FindingLevel.Error,
                "A janela do jogo está minimizada: não existe imagem para capturar. Algo no jogo (ou um atalho) a minimizou."));

        if (i.CaptureAttempts > 0 && i.CaptureFailures >= i.CaptureAttempts)
            f.Add(new("CAPTURA_FALHA", FindingLevel.Error,
                $"A captura falhou em todas as {i.CaptureAttempts} tentativas (o Windows devolveu erro). " +
                (i.PrintWindowMode
                    ? "Tente \"captureMode\": \"screen\" com \"hideWindow\": false no game.json."
                    : "Com captureMode \"screen\" a janela precisa estar visível e sem nada por cima.")));
        else if (i.CaptureFailures > 0)
            f.Add(new("CAPTURA_FALHA_PARCIAL", FindingLevel.Warning,
                $"A captura falhou em {i.CaptureFailures} de {i.CaptureAttempts} tentativas."));

        if (i.AvgCaptureMs > 40)
            f.Add(new("CAPTURA_LENTA", FindingLevel.Warning,
                $"Cada captura leva em média {i.AvgCaptureMs:0} ms (o orçamento é ~33 ms a 30 fps): o vídeo não chega a 30 quadros/s. " +
                "O jogo está pesado ou a janela está tão grande que redimensionar custa caro."));

        if (i.FocusFailures > 0)
            f.Add(new("SEM_FOCO", FindingLevel.Error,
                $"O Windows RECUSOU dar foco à janela do jogo {i.FocusFailures} vez(es). Por segurança o servidor DESCARTA a tecla em vez de " +
                $"mandá-la a outra janela; quem está em primeiro plano é {(i.ForegroundWindow.Length > 0 ? i.ForegroundWindow : "outra janela")}. " +
                "Deixe o PC desbloqueado, sem outro programa pedindo atenção, e rode o servidor como o mesmo usuário que está logado."));

        if (i.KeysAccepted == 0 && i.KeysRejected > 0)
            f.Add(new("TECLAS_RECUSADAS", FindingLevel.Warning,
                $"O celular mandou {i.KeysRejected} tecla(s) e todas foram recusadas: fora da lista segura (só letras, números, setas, CTRL, SHIFT e ESC; CTRL+ESC é bloqueado) ou com nome inexistente."));

        bool still = i.StillSeconds >= StillThresholdSeconds && !i.WindowHung;
        if (still)
        {
            var hints = new List<string>();
            string what;
            if (i.BlackRatio >= 0.98)
            {
                what = "PRETA e parada";
                hints.Add("A captura devolve só preto. É típico de PrintWindow com jogo DirectX/OpenGL/Vulkan ou com janela fora da tela.");
            }
            else if (i.GameCpuPercent < 1)
            {
                what = "parada, e o jogo está ocioso (CPU ≈ 0%)";
                hints.Add("O jogo não está fazendo nada: carregamento terminado esperando tecla, ou PAUSADO por estar sem foco " +
                          "(muitos jogos pausam quando a janela não é a ativa).");
            }
            else
            {
                what = $"parada, mas o jogo está ATIVO (CPU {i.GameCpuPercent:0}% de um núcleo)";
                hints.Add("O jogo roda, mas a captura sempre devolve a mesma imagem: o PrintWindow está entregando um quadro velho. " +
                          "Comum em jogos baseados em Chromium (RPG Maker MV/MZ, NW.js, Electron), que param de desenhar quando a janela está fora da tela/oculta.");
            }

            if (!i.WindowForeground)
                hints.Add("A janela do jogo NÃO está em primeiro plano.");

            if (i.PrintWindowMode && i.HideWindow)
                hints.Add((i.HideMode.Length > 0 ? $"Modo de ocultar atual: hideMode \"{i.HideMode}\" (existem \"transparent\" e \"offscreen\"). " : "") +
                          "Teste 1: no game.json use \"hideWindow\": false (a janela fica visível no PC). Teste 2, se continuar: também \"captureMode\": \"screen\".");
            else if (i.PrintWindowMode)
                hints.Add("Teste: \"captureMode\": \"screen\" (e deixe a janela do jogo visível, sem nada por cima).");
            else
                hints.Add("Com captureMode \"screen\" a janela precisa estar visível e sem nada por cima (inclusive outras janelas).");

            hints.Add("Se esta é uma tela realmente estática (menu parado), aperte uma tecla no celular: se nada mudar, o problema é real.");

            f.Add(new("IMAGEM_PARADA", FindingLevel.Warning,
                $"Há {i.StillSeconds:0} s a imagem enviada está {what}. " + string.Join(" ", hints)));

            if (i.KeysAccepted > 0 && i.SecondsSinceLastKey is { } sk && i.StillSeconds > sk + 2 && sk < 60)
                f.Add(new("TECLA_SEM_EFEITO", FindingLevel.Error,
                    $"O servidor enviou teclas ao jogo (a última há {sk:0} s) e a imagem NÃO mudou depois disso. " +
                    "O jogo não está reagindo: janela sem foco, jogo pausado ou travado."));
        }

        return f;
    }
}

/// <summary>
/// Evita encher o log: escreve um achado na 1ª vez que aparece, repete a cada <c>repeatEvery</c> enquanto
/// continuar, e avisa quando ele some.
/// </summary>
internal sealed class DiagnosisTracker
{
    private readonly TimeSpan _repeatEvery;
    private readonly Dictionary<string, DateTime> _active = new();

    public DiagnosisTracker(TimeSpan? repeatEvery = null) => _repeatEvery = repeatEvery ?? TimeSpan.FromSeconds(30);

    public IReadOnlyCollection<string> ActiveCodes => _active.Keys;

    /// <param name="emit">(achado, resolvido). resolvido = true quando o código parou de aparecer.</param>
    public void Update(IEnumerable<Finding> findings, DateTime now, Action<Finding, bool> emit)
    {
        var current = new Dictionary<string, Finding>();
        foreach (var f in findings) current[f.Code] = f;

        foreach (var (code, finding) in current)
        {
            if (!_active.TryGetValue(code, out var last) || now - last >= _repeatEvery)
            {
                _active[code] = now;
                emit(finding, false);
            }
        }

        foreach (var code in _active.Keys.Where(c => !current.ContainsKey(c)).ToList())
        {
            _active.Remove(code);
            emit(new Finding(code, FindingLevel.Info, "voltou ao normal"), true);
        }
    }
}
