using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameStream.Server.Config;
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
    private readonly GameProfile _profile;
    private readonly ServerOptions _options;
    private readonly string _ffmpegPath;
    private readonly ILogger _log;

    private volatile InputInjector? _input;
    private LaunchedGame? _game;

    public StreamSession(GameProfile profile, ServerOptions options, string ffmpegPath, ILogger log)
    {
        _profile = profile;
        _options = options;
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public async Task RunAsync(WebSocket ws, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = cts.Token;
        var sender = new WsSender(ws);

        // Tamanho fixo da transmissão (par, exigência do H.264 yuv420p). 0 no game.json = 640x480.
        int width = (_profile.StreamWidth > 0 ? _profile.StreamWidth : 640) & ~1;
        int height = (_profile.StreamHeight > 0 ? _profile.StreamHeight : 480) & ~1;
        int fps = Math.Clamp(_profile.Fps, 1, 60);

        var limits = ResourceLimits.Compute(
            _profile.Cores ?? _options.GameCores,
            _profile.MemoryMb ?? _options.GameMemoryMb,
            _options.CoreStart);

        FrameComposer? composer = null;
        FfmpegEncoder? encoder = null;
        Task encoderTask = Task.CompletedTask, receiveTask = Task.CompletedTask, gameTask = Task.CompletedTask;

        try
        {
            composer = new FrameComposer(width, height, FindLogo());
            composer.ShowStatus("Iniciando o jogo...");

            // O cliente recebe algo imediatamente: "hello" agora e vídeo logo em seguida, mesmo sem jogo.
            await sender.SendJsonAsync(new { t = "hello", game = _profile.Id, w = width, h = height, fps }, ct);

            encoder = new FfmpegEncoder(_ffmpegPath, composer, _profile, fps, limits.ServerMask);
            encoderTask = encoder.RunAsync(au => sender.SendVideoAsync(au, ct), ct);

            // Recebe desde já: sem isso, o ping do cliente ficava sem resposta enquanto o jogo abria.
            receiveTask = ReceiveLoopAsync(ws, sender, ct);

            // O jogo abre em segundo plano; a tela de espera cobre o tempo de carregamento.
            gameTask = RunGameAsync(composer, limits, ct);

            // Termina quando o cliente sai ou o codificador para. O fim do jogo NÃO encerra a sessão.
            var finished = await Task.WhenAny(encoderTask, receiveTask);
            await finished; // repassa erro, se houver
        }
        catch (OperationCanceledException)
        {
            // sessão encerrada
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro na sessão de {Game}", _profile.Id);
        }
        finally
        {
            cts.Cancel();
            encoder?.Dispose();

            await SwallowAsync(encoderTask);
            await SwallowAsync(receiveTask);
            await SwallowAsync(gameTask);

            Interlocked.Exchange(ref _game, null)?.Dispose();
            composer?.Dispose();

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None); }
                catch { /* ignora */ }
            }
            _log.LogInformation("Sessão de {Game} encerrada", _profile.Id);
        }
    }

    /// <summary>Abre o jogo, esconde a janela (se configurado) e passa a transmitir a imagem dele.</summary>
    private async Task RunGameAsync(FrameComposer composer, ResourceLimits limits, CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Iniciando jogo {Game}", _profile.Id);
            var game = await GameLauncher.LaunchAsync(_profile, limits, ct);
            _game = game;
            _log.LogInformation("Jogo {Game}: {Cores} núcleo(s), limite de {Mem} MB",
                _profile.Id, game.Limits.Cores, game.Limits.MemoryMb);

            var method = MethodOf(_profile);

            WindowHider? hider = null;
            if (_profile.HideWindow && method == CaptureMethod.PrintWindow)
            {
                hider = new WindowHider(game.WindowHandle);
                hider.Hide();
            }
            else if (_profile.HideWindow)
            {
                _log.LogWarning("hideWindow ignorado: captureMode \"screen\" precisa da janela visível.");
            }

            composer.ShowGame(game.WindowHandle, method);
            _input = new InputInjector(_profile.AllowedKeys, game.WindowHandle);

            // Vigia a janela: se o jogo fechar, volta para a tela de espera.
            while (!ct.IsCancellationRequested && Native.IsWindow(game.WindowHandle))
            {
                hider?.EnsureHidden();
                await Task.Delay(1000, ct);
            }

            if (!ct.IsCancellationRequested)
            {
                _input?.ReleaseAll();
                _input = null;
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
            _log.LogError(ex, "Erro ao rodar o jogo {Game}", _profile.Id);
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
            if (result.MessageType == WebSocketMessageType.Close) return;

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
            await sender.SendTextAsync("pong", ct);
            return;
        }

        JsonObject? obj;
        try { obj = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return; }
        if (obj is null) return;

        switch (Str(obj["t"]))
        {
            case "key":
                var key = Str(obj["k"]);
                var input = _input; // ainda null enquanto o jogo abre: teclas são ignoradas
                if (key is not null && input is not null && !input.Handle(key, IsTrue(obj["d"])))
                    _log.LogDebug("Tecla ignorada: {Key}", key);
                break;

            case "ping":
                obj["t"] = "pong"; // devolve os mesmos campos (id, hora etc.)
                await sender.SendTextAsync(obj.ToJsonString(), ct);
                break;
        }
    }

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
