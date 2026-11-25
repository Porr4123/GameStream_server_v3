using System.Net.WebSockets;
using System.Net;
using System.Security.Cryptography;
using GameStream.Server.Config;
using GameStream.Server.Diagnostics;
using GameStream.Server.Net;
using GameStream.Server.Streaming;

// ---- Comando: dotnet run -- novo "Nome do Jogo"  (cria uma pasta esqueleto e sai) ----
if (args.Length >= 1 && args[0].Equals("novo", StringComparison.OrdinalIgnoreCase))
{
    var early = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .Build()
        .GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();

    var gamesRoot = early.ResolveGamesDir();
    Directory.CreateDirectory(gamesRoot);

    var name = args.Length >= 2 ? string.Join(' ', args.Skip(1)) : "novo-jogo";
    var created = GameFolderSkeleton.Create(gamesRoot, name);

    Console.WriteLine($"Pasta criada: {created}");
    Console.WriteLine("Coloque nela os arquivos do jogo ou um atalho (.lnk) para o .exe.");
    return;
}

// ---- Servidor ----
var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
if (string.IsNullOrWhiteSpace(options.Pin))
    options.Pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");

// ---- Log: console com horário + arquivo em diagnostico\ (para mandar inteiro quando precisar de ajuda) ----
var diagOptions = options.Diagnostics;
var diagDir = diagOptions.ResolveDir(builder.Environment.ContentRootPath);
FileLoggerProvider? fileLog = null;

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});
if (diagOptions.Enabled)
{
    DiagnosticsCleaner.Cleanup(diagDir, diagOptions.KeepDays);
    if (diagOptions.FileLog)
    {
        fileLog = new FileLoggerProvider(Path.Combine(diagDir, $"servidor-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        if (fileLog.IsActive) builder.Logging.AddProvider(fileLog);
        else fileLog = null;
    }
}

var app = builder.Build();

var root = app.Environment.ContentRootPath;

var ffmpeg = Path.GetFullPath(Path.Combine(root, options.FfmpegPath));
if (!File.Exists(ffmpeg))
{
    app.Logger.LogWarning("ffmpeg não encontrado em {Path}. Vou tentar o ffmpeg do PATH.", ffmpeg);
    ffmpeg = "ffmpeg";
}

// Sem ffmpeg ou sem libx264 não existe vídeo nenhum: melhor descobrir na partida do que na primeira conexão.
var ffcheck = await FfmpegCheck.RunAsync(ffmpeg);
if (!ffcheck.Found)
    app.Logger.LogError("Não consegui executar o ffmpeg ({Path}): {Error}. Instale-o ou ajuste Server:FfmpegPath no appsettings.json.",
        ffmpeg, ffcheck.Error);
else if (!ffcheck.HasX264)
    app.Logger.LogError("Este ffmpeg NÃO tem o codificador libx264 ({Version}). O servidor precisa dele para gerar o vídeo: use um build \"full\" ou \"gpl\".",
        ffcheck.Version);
else
    app.Logger.LogInformation("ffmpeg: {Version} (libx264: sim)", ffcheck.Version);

app.Logger.LogInformation("Ambiente: {Os}, .NET {Net}, {Cpus} núcleo(s) lógico(s); sessão do Windows interativa: {Interactive}",
    System.Runtime.InteropServices.RuntimeInformation.OSDescription, Environment.Version, Environment.ProcessorCount,
    Environment.UserInteractive);

var gamesDir = options.ResolveGamesDir();
GameFolderSkeleton.EnsureRoot(gamesDir);
var profiles = new ProfileStore(gamesDir);
profiles.Load();

// Aviso se o limite de RAM por jogo não cabe na máquina.
long totalMb = GameStream.Server.Sandbox.SystemMemory.TotalMb();
if (options.GameMemoryMb > 0 && options.GameMemoryMb > totalMb * 0.75)
    Console.WriteLine($"[aviso] Limite de {options.GameMemoryMb} MB por jogo, mas a máquina tem ~{totalMb} MB de RAM. " +
                      "Windows, servidor e ffmpeg também usam memória: reduza GameMemoryMb ou o jogo vai travar/paginar.");

var gate = new SemaphoreSlim(1, 1); // MVP: um jogador por vez

app.UseDefaultFiles();
app.UseStaticFiles();   // wwwroot/test.html
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });

// ---- Diagnóstico ao vivo: só abre no próprio PC (a imagem do jogo não deve vazar para a rede) ----
static bool IsLocalRequest(HttpContext ctx)
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return false;
    if (IPAddress.IsLoopback(remote)) return true;
    var local = ctx.Connection.LocalIpAddress;
    return local is not null && remote.Equals(local); // o PC acessando pelo próprio IP da rede
}

if (diagOptions.Enabled && diagOptions.DebugPage)
{
    app.MapGet("/debug", (HttpContext ctx) =>
        IsLocalRequest(ctx) ? Results.Content(DebugHub.Page, "text/html; charset=utf-8") : Results.NotFound());

    app.MapGet("/debug/status", (HttpContext ctx) =>
        IsLocalRequest(ctx) ? Results.Text(DebugHub.Status, "text/plain; charset=utf-8") : Results.NotFound());

    app.MapGet("/debug/frame.bmp", (HttpContext ctx) =>
    {
        if (!IsLocalRequest(ctx)) return Results.NotFound();
        var frame = DebugHub.Frame;
        if (frame is null) return Results.NoContent();

        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(GameStream.Server.Streaming.FrameInspector.ToBmp24(frame.Value.Bgra, frame.Value.W, frame.Value.H), "image/bmp");
    });
}

// Lista de jogos e layouts padrão: GET /games?pin=123456  (relê as pastas: jogos novos aparecem sozinhos)
app.MapGet("/games", (HttpContext ctx) =>
{
    switch (Auth.Evaluate(ctx, options, app.Logger))
    {
        case AuthResult.Locked: return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        case AuthResult.Denied: return Results.Unauthorized();
    }
    profiles.Load();
    return Results.Ok(profiles.All.Select(p => new
    {
        p.Id, p.Name, p.StreamWidth, p.StreamHeight, p.Fps, p.AllowedKeys, p.Layout,
        SafeKeys = GameStream.Server.Input.KeyMap.Names
    }));
});

// Som em soquete próprio: /audio?pin=123456&token=<do hello>. O vídeo e o som não dividem mais a mesma fila TCP.
app.MapGet("/audio", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    var auth = Auth.Evaluate(ctx, options, app.Logger);
    if (auth != AuthResult.Ok)
    {
        ctx.Response.StatusCode = auth == AuthResult.Locked ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
        return;
    }
    if (!AudioLink.TryGet(ctx.Request.Query["token"].ToString(), out var audioSender))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    using var audioWs = await ctx.WebSockets.AcceptWebSocketAsync();
    audioSender.AttachAudioSocket(audioWs);
    try
    {
        // Só lê para perceber quando o cliente fecha (o cliente não manda nada por aqui).
        var sink = new byte[256];
        while (audioWs.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
        {
            var r = await audioWs.ReceiveAsync(sink, ctx.RequestAborted);
            if (r.MessageType == WebSocketMessageType.Close) break;
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
    {
        // caiu ou a sessão acabou
    }
    finally
    {
        audioSender.DetachAudioSocket(audioWs);
    }
});

// Transmissão + comandos: WebSocket em /stream?game=<id>&pin=123456
app.MapGet("/stream", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    var auth = Auth.Evaluate(ctx, options, app.Logger);
    if (auth != AuthResult.Ok)
    {
        app.Logger.LogWarning("Conexão de {Ip} recusada ({Code}): {Why}", ctx.Connection.RemoteIpAddress,
            auth == AuthResult.Locked ? 429 : 401,
            auth == AuthResult.Locked ? "endereço bloqueado por PINs errados em excesso." : "PIN incorreto ou ausente.");
        ctx.Response.StatusCode = auth == AuthResult.Locked ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
        return;
    }

    profiles.Load(); // pega edições do game.json sem reiniciar
    var profile = profiles.Find(ctx.Request.Query["game"].ToString());
    if (profile is null)
    {
        app.Logger.LogWarning("Conexão de {Ip} recusada (404): jogo \"{Game}\" não existe. Jogos prontos: {Ready}",
            ctx.Connection.RemoteIpAddress, ctx.Request.Query["game"].ToString(), string.Join(", ", profiles.All.Select(p => p.Id)));
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var clientInfo = $"{ctx.Connection.RemoteIpAddress} ({ctx.Request.Headers.UserAgent.ToString().Trim()})";

    if (!await gate.WaitAsync(0))
    {
        app.Logger.LogWarning("Cliente {Client} recusado (409): já existe uma sessão em andamento. Se ela já terminou, " +
                              "veja se o log mostra \"Sessão ... encerrada\"; se não mostra, o encerramento anterior ficou preso.", clientInfo);
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        return;
    }
    try
    {
        // Tela do cliente (opcional): ?sw=2400&sh=1080&fit=fit|fill|stretch. Sem isso, vale o tamanho do perfil.
        var q = ctx.Request.Query;
        int.TryParse(q["sw"].ToString(), out int sw);
        int.TryParse(q["sh"].ToString(), out int sh);
        if (sw < 0 || sw > 10000 || sh < 0 || sh > 10000) sw = sh = 0;
        var display = new ClientDisplay(sw, sh, StreamGeometry.ParseFit(q["fit"]));

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await new StreamSession(profile, options, ffmpeg, app.Logger, clientInfo, root, display).RunAsync(ws, ctx.RequestAborted);
    }
    finally
    {
        gate.Release();
    }
});

Console.WriteLine("=== GameStream.Server ===");
Console.WriteLine($"PIN de pareamento : {options.Pin}");
Console.WriteLine($"Pasta dos jogos   : {gamesDir}");
Console.WriteLine($"Jogos prontos     : {profiles.All.Count} ({string.Join(", ", profiles.All.Select(p => p.Id))})");
Console.WriteLine($"Limites por jogo  : {(options.GameCores > 0 ? options.GameCores + " núcleo(s)" : $"núcleos dinâmicos ({GameStream.Server.Sandbox.ResourceLimits.AutoCores()} de {Environment.ProcessorCount})")}, " +
                  (options.GameMemoryMb > 0 ? $"{options.GameMemoryMb} MB fixos" : $"RAM dinâmica (livre na hora de abrir - {options.MemoryReserveMb} MB de reserva)"));
Console.WriteLine($"Teste no navegador: http://localhost:{options.Port}/test.html");
if (diagOptions.Enabled)
{
    if (diagOptions.DebugPage) Console.WriteLine($"Diagnóstico ao vivo: http://localhost:{options.Port}/debug  (veja o que está sendo transmitido)");
    Console.WriteLine($"Arquivos de log   : {(fileLog is not null ? fileLog.Path : diagDir)}");
}

GameStream.Server.Audio.ExclusiveDevices.RecoverPending(app.Logger);
var tuning = GameStream.Server.Interop.ProcessTuning.Apply();
Console.WriteLine(tuning);
app.Logger.LogInformation("{Tuning}", tuning);

app.Run();
