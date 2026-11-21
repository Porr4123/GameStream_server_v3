using System.Security.Cryptography;
using GameStream.Server.Config;
using GameStream.Server.Net;

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
var app = builder.Build();

var root = app.Environment.ContentRootPath;

var ffmpeg = Path.GetFullPath(Path.Combine(root, options.FfmpegPath));
if (!File.Exists(ffmpeg))
{
    app.Logger.LogWarning("ffmpeg não encontrado em {Path}. Vou tentar o ffmpeg do PATH.", ffmpeg);
    ffmpeg = "ffmpeg";
}

var gamesDir = options.ResolveGamesDir();
GameFolderSkeleton.EnsureRoot(gamesDir);
var profiles = new ProfileStore(gamesDir);
profiles.Load();

// Aviso se o limite de RAM por jogo não cabe na máquina.
long totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
if (options.GameMemoryMb > totalMb * 0.75)
    Console.WriteLine($"[aviso] Limite de {options.GameMemoryMb} MB por jogo, mas a máquina tem ~{totalMb} MB de RAM. " +
                      "Windows, servidor e ffmpeg também usam memória: reduza GameMemoryMb ou o jogo vai travar/paginar.");

var gate = new SemaphoreSlim(1, 1); // MVP: um jogador por vez

app.UseDefaultFiles();
app.UseStaticFiles();   // wwwroot/test.html
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });

// Lista de jogos e layouts padrão: GET /games?pin=123456  (relê as pastas: jogos novos aparecem sozinhos)
app.MapGet("/games", (HttpContext ctx) =>
{
    if (!Auth.Check(ctx, options)) return Results.Unauthorized();
    profiles.Load();
    return Results.Ok(profiles.All.Select(p => new
    {
        p.Id, p.Name, p.StreamWidth, p.StreamHeight, p.Fps, p.AllowedKeys, p.Layout
    }));
});

// Transmissão + comandos: WebSocket em /stream?game=<id>&pin=123456
app.MapGet("/stream", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    if (!Auth.Check(ctx, options)) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

    profiles.Load(); // pega edições do game.json sem reiniciar
    var profile = profiles.Find(ctx.Request.Query["game"].ToString());
    if (profile is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

    if (!await gate.WaitAsync(0)) { ctx.Response.StatusCode = StatusCodes.Status409Conflict; return; }
    try
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await new StreamSession(profile, options, ffmpeg, app.Logger).RunAsync(ws, ctx.RequestAborted);
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
Console.WriteLine($"Limites por jogo  : {options.GameCores} núcleo(s), {options.GameMemoryMb} MB");
Console.WriteLine($"Teste no navegador: http://localhost:{options.Port}/test.html");

app.Run();
