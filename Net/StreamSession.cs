using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using GameStream.Server.Config;
using GameStream.Server.Diagnostics;
using GameStream.Server.Input;
using GameStream.Server.Interop;
using GameStream.Server.Sandbox;
using GameStream.Server.Streaming;

namespace GameStream.Server.Net;

/// <summary>
/// Uma sessão = um cliente jogando um jogo.
///
/// Servidor -> cliente:
///   texto  {"t":"hello","game","w","h","fps"}   (enviado assim que conecta)
///   texto  {"t":"pong", ...}                    (resposta a {"t":"ping", ...}; devolve os mesmos campos)
///   binário [1][flags][quadro H.264 Annex B]    (SEMPRE: tela de espera enquanto o jogo abre, jogo depois)
/// Cliente -> servidor:
///   texto  {"t":"key","k":"Z","d":1}            (d: 1 = apertou, 0 = soltou)
///   texto  {"t":"ping", ...}  ou  ping
/// </summary>
public sealed class StreamSession
{
    private enum GameState { NotStarted, Launching, Running, Exited }

    private readonly GameProfile _profile;
    private readonly ServerOptions _options;
    private readonly string _ffmpegPath;
    private readonly ILogger _log;
    private readonly string _clientInfo;
    private readonly string _contentRoot;

    private volatile InputInjector? _input;
    private LaunchedGame? _game;

    // Estado que o monitor (diagnóstico) lê de outras tarefas.
    private volatile GameProbe? _gameProbe;
    private volatile int _gameState = (int)GameState.NotStarted;
    private Stopwatch? _gameShownWatch;
    private int _rehides;
    private readonly ClientActivity _client = new();
    private volatile string _statusText = "Aguardando as primeiras medidas (a cada 5 s)...";
    private volatile string _findingsText = "";
    private string _sessionId = "";
    private string _endReason = "sessão ainda em andamento";
    private readonly Stopwatch _sessionWatch = Stopwatch.StartNew();

    public StreamSession(GameProfile profile, ServerOptions options, string ffmpegPath, ILogger log,
        string clientInfo = "", string? contentRoot = null)
    {
        _profile = profile;
        _options = options;
        _ffmpegPath = ffmpegPath;
        _log = log;
        _clientInfo = clientInfo;
        _contentRoot = contentRoot ?? AppContext.BaseDirectory;
    }

    public async Task RunAsync(WebSocket ws, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = cts.Token;

        // Tamanho fixo da transmissão (par, exigência do H.264 yuv420p). 0 no game.json = 640x480.
        int width = (_profile.StreamWidth > 0 ? _profile.StreamWidth : 1280) & ~1;
        int height = (_profile.StreamHeight > 0 ? _profile.StreamHeight : 720) & ~1;
        int fps = Math.Clamp(_profile.Fps, 1, 60);

        // Fila de envio curta (~0,5 s): se o celular/Wi-Fi não acompanharem, descarta até o próximo keyframe
        // em vez de parar o ffmpeg e acumular atraso.
        var sender = new WsSender(ws, Math.Max(4, fps / 2));

        var limits = ResourceLimits.Compute(
            _profile.Cores ?? _options.GameCores,
            _profile.MemoryMb ?? _options.GameMemoryMb,
            _options.CoreStart,
            _options.MemoryReserveMb);

        // ---- diagnóstico desta sessão ----
        var diag = _options.Diagnostics;
        _sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var probe = new VideoProbe(width, height);
        SessionRecorder? recorder = diag.Enabled
            ? new SessionRecorder(diag.ResolveDir(_contentRoot), _sessionId, diag, _log)
            : null;
        using var debugPage = diag.Enabled && diag.DebugPage
            ? DebugHub.Begin(BuildStatusText, () => probe.Latest() is { } f ? (f.Bgra, f.W, f.H) : null)
            : null;

        _log.LogInformation("===== Sessão {Id}: jogo {Game}, cliente {Client} =====", _sessionId, _profile.Id,
            string.IsNullOrEmpty(_clientInfo) ? "?" : _clientInfo);
        _log.LogInformation(
            "Transmissão {W}x{H} a {Fps} fps, {Kbps} kbps; captureMode={Mode}, hideWindow={Hide}; {Keys} teclas permitidas; " +
            "limites do jogo: {Cores} núcleo(s) (máscara 0x{GameMask:X}), {Mem} MB{Dyn}; ffmpeg nos núcleos 0x{ServerMask:X}",
            width, height, fps, _profile.BitrateKbps, _profile.CaptureMode, _profile.HideWindow, _profile.AllowedKeys.Count,
            limits.Cores, limits.GameMask, limits.MemoryMb, limits.MemoryIsDynamic ? " (dinâmico: RAM livre agora - reserva)" : " (fixo)", limits.ServerMask);

        // O 1º keyframe enviado mostra o que o celular de fato recebeu: perfil, nível e tamanho que o decodificador vai ver.
        bool spsLogged = false;
        sender.OnVideoSent = message =>
        {
            if (!spsLogged && (message[1] & 1) != 0)
            {
                spsLogged = true;
                LogSps(message[2..], width, height);
            }
            recorder?.WriteVideo(message);
        };

        FrameComposer? composer = null;
        FfmpegEncoder? encoder = null;
        Task encoderTask = Task.CompletedTask, videoTask = Task.CompletedTask, receiveTask = Task.CompletedTask,
            gameTask = Task.CompletedTask, statsTask = Task.CompletedTask;

        try
        {
            composer = new FrameComposer(width, height, FindLogo());
            composer.ShowStatus("Iniciando o jogo...");

            // O cliente recebe algo imediatamente: "hello" agora e vídeo logo em seguida, mesmo sem jogo.
            await sender.SendJsonAsync(new { t = "hello", game = _profile.Id, w = width, h = height, fps }, ct);
            _log.LogInformation("hello enviado ao cliente ({W}x{H} @ {Fps} fps)", width, height, fps);

            videoTask = sender.RunVideoAsync(ct);

            encoder = new FfmpegEncoder(_ffmpegPath, composer, _profile, fps, limits.ServerMask, _log, probe, width, height);
            encoderTask = encoder.RunAsync(sender.EnqueueVideo, ct);

            // Recebe desde já: sem isso, o ping do cliente ficava sem resposta enquanto o jogo abria.
            receiveTask = ReceiveLoopAsync(ws, sender, ct);

            // O jogo abre em segundo plano; a tela de espera cobre o tempo de carregamento.
            gameTask = RunGameAsync(composer, probe, limits, ct);

            statsTask = MonitorAsync(composer, sender, probe, recorder, width, height, ct);

            // Termina quando o cliente sai ou o codificador/envio para. O fim do jogo NÃO encerra a sessão.
            var finished = await Task.WhenAny(encoderTask, receiveTask, videoTask);
            _endReason = finished == receiveTask ? "o cliente fechou a conexão"
                : finished == encoderTask ? "o codificador (ffmpeg) parou"
                : "o envio de vídeo parou";
            await finished; // repassa erro, se houver
        }
        catch (OperationCanceledException)
        {
            _endReason = "a conexão foi cancelada (cliente saiu ou o servidor está parando)";
        }
        catch (WebSocketException ex)
        {
            // O celular fechou o app/perdeu o Wi-Fi sem despedida: é o fim normal de uma sessão, não um erro do servidor.
            _endReason = $"a conexão caiu sem despedida ({ex.WebSocketErrorCode})";
            _log.LogInformation("Conexão com o cliente encerrada: {Code} - {Reason}", ex.WebSocketErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            _endReason = $"erro: {ex.GetType().Name}: {ex.Message}";
            _log.LogError(ex, "Erro na sessão de {Game}", _profile.Id);
        }
        finally
        {
            cts.Cancel();

            // 1º o jogo. Uma janela que parou de responder pode deixar a captura (PrintWindow) presa dentro do
            // compositor; ela só solta quando o jogo morre. Esperar o codificador antes de matar o jogo travava
            // a sessão para sempre e o servidor ficava "ocupado" para os próximos clientes.
            Interlocked.Exchange(ref _game, null)?.Dispose();
            encoder?.Dispose();

            bool clean = await WaitAllAsync(TimeSpan.FromSeconds(5),
                encoderTask, videoTask, receiveTask, gameTask, statsTask);

            if (composer is not null)
            {
                if (clean)
                {
                    composer.Dispose();
                }
                else
                {
                    // Algo ainda está preso dentro do compositor: libera em segundo plano para não segurar o servidor.
                    _log.LogWarning("A sessão de {Game} demorou para encerrar; liberando recursos em segundo plano.", _profile.Id);
                    var c = composer;
                    _ = Task.Run(() => { try { c.Dispose(); } catch { /* ignora */ } });
                }
            }

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", closeTimeout.Token);
                }
                catch { /* ignora */ }
            }

            LogSummary(sender, probe);
            recorder?.Dispose();
            _log.LogInformation("===== Sessão {Id} de {Game} encerrada =====", _sessionId, _profile.Id);
        }
    }

    /// <summary>Resumo de uma linha do que aconteceu: serve para entender a sessão sem ler o log inteiro.</summary>
    private void LogSummary(WsSender sender, VideoProbe probe)
    {
        var t = sender.Totals;
        var vp = probe.Take();
        _log.LogInformation(
            "Resumo da sessão {Id}: durou {Dur:mm\\:ss}; terminou porque {Reason}; vídeo: {Frames} quadros enviados " +
            "({Kb} KB, {Keys} keyframes), {Dropped} descartados ({Over} estouros da fila); imagem: mudou em {Changes} de {Total} quadros; " +
            "{Client}",
            _sessionId, _sessionWatch.Elapsed, _endReason, t.FramesSent, t.BytesSent / 1024, t.KeyframesSent, t.FramesDropped,
            t.Overflows, vp.TotalChanges, vp.TotalFrames, _client);
    }

    private void LogSps(byte[] firstKeyframe, int width, int height)
    {
        var sps = H264Info.ParseSps(firstKeyframe);
        if (sps is null)
        {
            _log.LogWarning("O primeiro keyframe enviado NÃO tem um SPS legível: o decodificador do celular não consegue iniciar sem ele.");
            return;
        }

        var info = sps.Value;
        if (info.Width != width || info.Height != height)
            _log.LogError("O fluxo H.264 declara {SpsW}x{SpsH}, mas o hello avisou {W}x{H}: o decodificador do celular vai configurar o tamanho errado.",
                info.Width, info.Height, width, height);
        else
            _log.LogInformation("Fluxo enviado ao celular: {Sps}", info);
    }

    private string BuildStatusText() =>
        $"Sessão {_sessionId} ({_sessionWatch.Elapsed:mm\\:ss}) - jogo {_profile.Id} - cliente {_clientInfo}\n" +
        $"captureMode={_profile.CaptureMode} hideWindow={_profile.HideWindow}\n\n" +
        _statusText +
        (_findingsText.Length > 0 ? "\n\n" + _findingsText : "\n\nNenhum problema detectado até agora.");

    /// <summary>
    /// A cada 5 s mede tudo (rede, imagem, captura, jogo, teclas), escreve no log e aplica as regras do diagnóstico.
    /// A cada 1 s salva os quadros pedidos e atualiza a página /debug.
    /// </summary>
    private async Task MonitorAsync(FrameComposer composer, WsSender sender, VideoProbe probe, SessionRecorder? recorder,
        int width, int height, CancellationToken ct)
    {
        const int window = 5;
        var tracker = new DiagnosisTracker();
        var frameTimes = new Queue<int>(new[] { 2, 10, 30, 90 });
        double lastCpu = 0;
        int tick = 0;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                tick++;

                // Quadros .bmp depois que o jogo aparece: abra e veja o que o servidor está mandando.
                if (_gameShownWatch is { } w && frameTimes.Count > 0 && w.Elapsed.TotalSeconds >= frameTimes.Peek())
                {
                    int sec = frameTimes.Dequeue();
                    var path = recorder?.SaveFrame($"jogo+{sec}s", probe.LatestFrame(), probe.Width, probe.Height);
                    if (path is not null) _log.LogInformation("Quadro salvo ({Sec} s depois do jogo abrir): {Path}", sec, path);
                }

                if (tick % window != 0) continue;

                var vs = sender.TakeStats();
                var vp = probe.Take();
                var cap = composer.TakeCaptureCounters();
                var gs = _gameProbe?.Sample();
                if (gs is { } g0 && !double.IsNaN(g0.CpuPercent)) lastCpu = g0.CpuPercent;
                var inp = _input?.TakeStats();

                bool exited = _gameState == (int)GameState.Exited;
                bool launched = _gameState >= (int)GameState.Running;
                double fpsSent = vs.FramesSent / (double)window;

                // ---- linhas de estado (log + página /debug) ----
                string video =
                    $"Vídeo: {fpsSent:0.#} quadros/s ({vs.KeyframesSent} keyframes), {vs.BytesSent * 8 / 1000 / window} kbps, " +
                    $"descartados {vs.FramesDropped} ({vs.Overflows} estouros da fila), envio mais lento {vs.MaxSendMs} ms, fila {vs.Queued}";

                string image = composer.HasGame
                    ? $"Imagem do jogo: mudou em {vp.Changes} de {vp.Frames} quadros, " +
                      (vp.StillSeconds >= 2 ? $"IGUAL há {vp.StillSeconds:0} s, " : "") +
                      $"preto {vp.BlackRatio * 100:0}%, brilho {vp.MeanLuma:0}/255"
                    : $"Imagem: tela de espera ({(launched ? "jogo não está sendo mostrado" : "jogo ainda abrindo")})";

                string capture = composer.HasGame
                    ? $"Captura: média {vp.AvgCaptureMs:0.0} ms (pior {vp.MaxCaptureMs:0} ms), {cap.Failures} falha(s) em {cap.Attempts}, " +
                      $"janela travada em {cap.HungSkips} quadro(s)"
                    : "Captura: -";

                string game = gs is { } g
                    ? $"Jogo: {(g.ProcessAlive ? "vivo" : "SEM PROCESSO")} ({g.ActiveProcesses} proc.), janela {(g.WindowExists ? "existe" : "SUMIU")}" +
                      $"{(g.Hung ? ", NÃO RESPONDE" : "")}{(g.Minimized ? ", MINIMIZADA" : "")}, " +
                      $"{GameProbe.Sim(g.Foreground).Replace("sim", "em primeiro plano").Replace("não", "sem foco")}, " +
                      $"{g.ClientWidth}x{g.ClientHeight} em ({g.WindowX},{g.WindowY}), CPU {lastCpu:0}% de 1 núcleo" +
                      (_rehides > 0 ? $", reescondida {_rehides}x" : "")
                    : exited ? "Jogo: encerrado" : "Jogo: ainda abrindo";

                string keys = $"Cliente: {_client}" + (inp is { } i0 ? $"; enviadas ao jogo {i0.Sent}, foco recusado {i0.FocusFailures}x" : "");

                _statusText = string.Join('\n', video, image, capture, game, keys);

                var level = fpsSent == 0 ? LogLevel.Warning : LogLevel.Information;
                _log.Log(level, "{Video} | {Image} | {Capture} | {Game} | {Keys}", video, image, capture, game, keys);

                // ---- regras do diagnóstico ----
                var input = new DiagInput
                {
                    FramesSentTotal = sender.Totals.FramesSent,
                    DroppedInWindow = vs.FramesDropped,
                    GameLaunched = launched,
                    GameShown = composer.HasGame,
                    ProcessAlive = !exited && (gs?.ProcessAlive ?? true),
                    WindowExists = !exited && (gs?.WindowExists ?? true),
                    WindowHung = gs?.Hung ?? false,
                    WindowMinimized = gs?.Minimized ?? false,
                    WindowForeground = gs?.Foreground ?? false,
                    GameCpuPercent = lastCpu,
                    StillSeconds = vp.StillSeconds,
                    BlackRatio = vp.BlackRatio,
                    AvgCaptureMs = vp.AvgCaptureMs,
                    CaptureAttempts = cap.Attempts,
                    CaptureFailures = cap.Failures,
                    KeysAccepted = _client.KeysAccepted,
                    KeysRejected = _client.KeysRejected,
                    SecondsSinceLastKey = _client.SecondsSinceLastKey,
                    FocusFailures = inp?.FocusFailures ?? 0,
                    ForegroundWindow = inp?.Foreground ?? "",
                    PrintWindowMode = MethodOf(_profile) == CaptureMethod.PrintWindow,
                    HideWindow = _profile.HideWindow,
                    HideMode = _profile.HideMode,
                    TargetFps = Math.Clamp(_profile.Fps, 1, 60),
                    FpsSent = fpsSent
                };

                var findings = Diagnosis.Evaluate(input);
                _findingsText = string.Join('\n', findings.Select(f => $"DIAGNÓSTICO {Label(f.Level)} [{f.Code}] {f.Message}"));

                tracker.Update(findings, DateTime.UtcNow, (f, resolved) =>
                {
                    if (resolved)
                    {
                        _log.LogInformation("DIAGNÓSTICO resolvido [{Code}]: voltou ao normal", f.Code);
                        return;
                    }

                    _log.Log(f.Level == FindingLevel.Error ? LogLevel.Error : LogLevel.Warning,
                        "DIAGNÓSTICO {Level} [{Code}] {Msg}", Label(f.Level), f.Code, f.Message);

                    if (f.Code is "IMAGEM_PARADA" or "JANELA_TRAVADA" or "CAPTURA_FALHA")
                    {
                        var path = recorder?.SaveFrame("problema-" + f.Code.ToLowerInvariant(), probe.LatestFrame(), probe.Width, probe.Height);
                        if (path is not null)
                            _log.LogInformation("Quadro do momento do problema salvo em {Path}: abra para ver o que está sendo transmitido.", path);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // sessão encerrada
        }
    }

    private static string Label(FindingLevel level) => level == FindingLevel.Error ? "ERRO" : "AVISO";

    /// <summary>Espera todas as tarefas, engolindo erros, por no máximo <paramref name="timeout"/>. true = todas terminaram.</summary>
    private static async Task<bool> WaitAllAsync(TimeSpan timeout, params Task[] tasks)
    {
        var all = Task.WhenAll(tasks.Select(SwallowAsync));
        return await Task.WhenAny(all, Task.Delay(timeout)) == all;
    }

    /// <summary>Abre o jogo, esconde a janela (se configurado) e passa a transmitir a imagem dele.</summary>
    private async Task RunGameAsync(FrameComposer composer, VideoProbe probe, ResourceLimits limits, CancellationToken ct)
    {
        try
        {
            _gameState = (int)GameState.Launching;
            _log.LogInformation("Iniciando o jogo {Game} (exe \"{Exe}\", windowTitle \"{Title}\")", _profile.Id, _profile.Exe,
                _profile.WindowTitle ?? "");
            var launchWatch = Stopwatch.StartNew();

            var game = await GameLauncher.LaunchAsync(_profile, limits, ct);
            _game = game;
            _log.LogInformation("A janela do jogo apareceu após {Ms} ms: {Desc}", launchWatch.ElapsedMilliseconds,
                GameProbe.Describe(game));

            var method = MethodOf(_profile);

            WindowHider? hider = null;
            if (_profile.HideWindow && method == CaptureMethod.PrintWindow)
            {
                var mode = string.Equals(_profile.HideMode, "offscreen", StringComparison.OrdinalIgnoreCase)
                    ? HideMode.Offscreen
                    : HideMode.Transparent;
                hider = new WindowHider(game.WindowHandle, mode);
                hider.Hide();
                _log.LogInformation("Janela do jogo escondida (hideMode={Mode}): {Desc}. Se a imagem congelar só quando escondida, " +
                                    "tente hideMode \"offscreen\" ou hideWindow=false.", mode, hider.Describe());
                if (hider.TakeWarning() is { } hideWarn) _log.LogWarning("{Warn}", hideWarn);
            }
            else if (_profile.HideWindow)
            {
                _log.LogWarning("hideWindow ignorado: captureMode \"screen\" precisa da janela visível.");
            }

            var framing = composer.ShowGame(game.WindowHandle, method);
            probe.ResetStill();
            _gameShownWatch = Stopwatch.StartNew();
            _gameProbe = new GameProbe(game);
            _gameState = (int)GameState.Running;
            _log.LogInformation("Mostrando o jogo no vídeo: {Framing}", framing);

            _input = new InputInjector(game.WindowHandle, hider is null ? null : hider.KeepAtBottom);
            _log.LogInformation("Entrada de teclas pronta: só letras, números, setas, CTRL, SHIFT e ESC ({N} teclas), " +
                                "e só com o jogo em primeiro plano.", KeyMap.Names.Count);

            // Vigia a janela: se o jogo fechar, volta para a tela de espera.
            while (!ct.IsCancellationRequested && Native.IsWindow(game.WindowHandle))
            {
                if (hider?.EnsureHidden() == true) Interlocked.Increment(ref _rehides);
                if (hider?.TakeWarning() is { } w) _log.LogWarning("{Warn}", w);
                // No modo transparente confere a pilha de janelas com mais frequência: "abaixo de todas" não pode demorar.
                await Task.Delay(hider?.Mode == HideMode.Transparent ? 250 : 1000, ct);
            }

            if (!ct.IsCancellationRequested)
            {
                string exit = "?";
                try { exit = game.Process.HasExited ? game.Process.ExitCode.ToString() : "ainda rodando (só a janela fechou)"; } catch { /* ignora */ }
                _log.LogWarning("O jogo encerrou: a janela sumiu após {Sec:0} s de transmissão (código de saída do processo: {Exit}).",
                    _gameShownWatch.Elapsed.TotalSeconds, exit);

                _gameState = (int)GameState.Exited;
                _input?.ReleaseAll();
                _input = null;
                _gameProbe = null;
                Interlocked.Exchange(ref _game, null)?.Dispose();
                composer.ShowStatus("Jogo encerrado.");
            }
        }
        catch (OperationCanceledException)
        {
            // sessão encerrada
        }
        catch (Exception ex)
        {
            _gameState = (int)GameState.Exited;
            _log.LogError(ex, "Erro ao rodar o jogo {Game}: {Msg}", _profile.Id, FriendlyError(ex));
            composer.ShowStatus(FriendlyError(ex));
        }
        finally
        {
            _input?.ReleaseAll();
            _input = null;
        }
    }

    private static CaptureMethod MethodOf(GameProfile p) =>
        p.CaptureMode.Equals("screen", StringComparison.OrdinalIgnoreCase) ||
        p.CaptureMode.Equals("gdigrab", StringComparison.OrdinalIgnoreCase)
            ? CaptureMethod.Screen
            : CaptureMethod.PrintWindow;

    private static string FriendlyError(Exception ex) => ex switch
    {
        FileNotFoundException => "Não encontrei o executável do jogo.",
        TimeoutException => "A janela do jogo não apareceu a tempo.",
        InvalidOperationException => ex.Message,
        _ => "Erro ao iniciar o jogo."
    };

    /// <summary>Logo da tela de espera: dados\logo.bmp do jogo, depois LogoPath do appsettings, senão o logo padrão.</summary>
    private string? FindLogo()
    {
        var perGame = Path.Combine(_profile.Folder, "dados", "logo.bmp");
        if (File.Exists(perGame)) return perGame;

        if (!string.IsNullOrWhiteSpace(_options.LogoPath))
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_options.LogoPath), AppContext.BaseDirectory);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private async Task ReceiveLoopAsync(WebSocket ws, WsSender sender, CancellationToken ct)
    {
        var buffer = new byte[2048];
        using var message = new MemoryStream();
        bool discard = false;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _log.LogInformation("O cliente fechou a conexão (código {Status}, motivo \"{Reason}\")",
                    result.CloseStatus?.ToString() ?? "nenhum", result.CloseStatusDescription ?? "");
                return;
            }

            if (!discard)
            {
                if (message.Length + result.Count > 4096) { discard = true; message.SetLength(0); }
                else message.Write(buffer, 0, result.Count);
            }

            if (!result.EndOfMessage) continue;

            if (!discard && result.MessageType == WebSocketMessageType.Text)
                await HandleControlAsync(message.ToArray(), sender, ct);

            message.SetLength(0);
            discard = false;
        }
    }

    private async Task HandleControlAsync(byte[] data, WsSender sender, CancellationToken ct)
    {
        // "ping" em texto puro.
        var text = Encoding.UTF8.GetString(data).Trim();
        if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            _client.CountPing();
            await sender.SendTextAsync("pong", ct);
            return;
        }

        JsonObject? obj;
        try { obj = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException)
        {
            _client.CountBad();
            if (_client.BadMessages <= 3)
                _log.LogWarning("Mensagem do cliente que não é JSON válido: {Text}", Trim(text, 120));
            return;
        }
        if (obj is null)
        {
            _client.CountBad();
            return;
        }

        var type = Str(obj["t"]);
        switch (type)
        {
            case "key":
                var key = Str(obj["k"]);
                bool down = IsTrue(obj["d"]);
                if (key is null)
                {
                    _client.CountBad();
                    break;
                }

                var input = _input; // ainda null enquanto o jogo abre: teclas são ignoradas
                ClientActivity.KeyResult result;
                if (input is null) result = ClientActivity.KeyResult.GameNotReady;
                else result = input.Handle(key, down) switch
                {
                    KeyOutcome.Rejected => ClientActivity.KeyResult.Rejected,
                    KeyOutcome.NoFocus => ClientActivity.KeyResult.NoFocus,
                    _ => ClientActivity.KeyResult.Accepted
                };

                if (_client.CountKey(result))
                    _log.LogInformation("Tecla {Key} {Dir} recebida do cliente: {Result}", key, down ? "apertada" : "solta",
                        result switch
                        {
                            ClientActivity.KeyResult.Accepted => "enviada ao jogo",
                            ClientActivity.KeyResult.Rejected => "RECUSADA (fora da lista segura: letras, números, setas, CTRL, SHIFT, ESC; ou combinação bloqueada)",
                            ClientActivity.KeyResult.NoFocus => "DESCARTADA: o jogo não está em primeiro plano (o servidor nunca injeta tecla fora do jogo)",
                            _ => "ignorada: o jogo ainda não abriu"
                        });
                break;

            case "ping":
                _client.CountPing();
                obj["t"] = "pong"; // devolve os mesmos campos (id, hora etc.)
                await sender.SendTextAsync(obj.ToJsonString(), ct);
                break;

            case "log":
                // Reservado: o app pode relatar problemas do lado dele (ex.: falha do decodificador) e eles caem no log do servidor.
                _log.LogWarning("[cliente] {Msg}", Trim(Str(obj["msg"]) ?? text, 500));
                break;

            default:
                if (_client.CountOther(type ?? "(sem tipo)"))
                    _log.LogWarning("O cliente enviou uma mensagem desconhecida (tipo \"{Type}\"): {Text}", type ?? "(sem tipo)", Trim(text, 160));
                break;
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool IsTrue(JsonNode? node)
    {
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<int>(out var i)) return i != 0;
        if (v.TryGetValue<bool>(out var b)) return b;
        return false;
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task; } catch { /* já tratado */ }
    }
}
