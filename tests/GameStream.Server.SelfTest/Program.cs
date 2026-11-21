using System.Diagnostics;
using System.Net.WebSockets;
using GameStream.Server.Config;
using GameStream.Server.Net;
using GameStream.Server.Streaming;

// Autoteste do caminho do vídeo (ffmpeg -> divisor de quadros -> fila de envio -> WebSocket), sem celular e sem jogo.
// Não usa GDI nem janelas: roda no Windows e também em Linux/macOS. Precisa do ffmpeg (tools\ffmpeg.exe ou PATH).
//
//   dotnet run --project tests/GameStream.Server.SelfTest
//
// Sai com código 0 se tudo passou, 1 se algo falhou.

var failures = new List<string>();
int checks = 0;

void Check(bool ok, string what)
{
    checks++;
    if (!ok) failures.Add(what);
    Console.WriteLine($"  [{(ok ? " ok " : "FALHA")}] {what}");
}

void Section(string title) => Console.WriteLine($"\n== {title}");

TestAnnexB();
TestLevels();
await TestSlowConsumerAsync();
await TestRealEncoderAsync();

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"Tudo certo: {checks} verificações passaram.");
    return 0;
}
Console.WriteLine($"{failures.Count} de {checks} verificações FALHARAM:");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;

// ---------------------------------------------------------------------------------------------

void TestAnnexB()
{
    Section("AnnexBSplitter / SkipAud");

    byte[] aud4 = { 0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x65, 0x88, 0x84 };
    byte[] aud3 = { 0, 0, 1, 0x09, 0x10, 0, 0, 1, 0x41, 0x9A };
    byte[] noAud = { 0, 0, 0, 1, 0x65, 0x88 };
    byte[] onlyAud = { 0, 0, 0, 1, 0x09, 0xF0 };

    Check(AnnexBSplitter.SkipAud(aud4) == 6, "AUD com start code de 4 bytes é pulado (6 bytes)");
    Check(AnnexBSplitter.SkipAud(aud3) == 5, "AUD com start code de 3 bytes é pulado (5 bytes)");
    Check(AnnexBSplitter.SkipAud(noAud) == 0, "quadro sem AUD fica como está");
    Check(AnnexBSplitter.SkipAud(onlyAud) == 0, "quadro só com AUD não é cortado até o fim");

    // O divisor tem que devolver os mesmos quadros qualquer que seja o tamanho dos pedaços que chegam do pipe.
    var rnd = new Random(7);
    var stream = new List<byte>();
    var expected = new List<byte[]>();
    for (int i = 0; i < 40; i++)
    {
        var au = new List<byte> { 0, 0, 0, 1, 0x09, 0xF0 };
        if (i % 10 == 0)
        {
            au.AddRange(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80 });
            au.AddRange(new byte[] { 0, 0, 0, 1, 0x65 });
        }
        else
        {
            au.AddRange(new byte[] { 0, 0, 1, 0x41 }); // start code de 3 bytes, como o x264 usa nos quadros P
        }
        int n = rnd.Next(1, 300);
        for (int k = 0; k < n; k++) au.Add((byte)rnd.Next(1, 256)); // sem zeros: não forma start code por acaso
        expected.Add(au.ToArray());
        stream.AddRange(au);
    }

    var data = stream.ToArray();
    var splitter = new AnnexBSplitter();
    var got = new List<byte[]>();
    for (int pos = 0; pos < data.Length;)
    {
        int chunk = Math.Min(rnd.Next(1, 7), data.Length - pos); // pedaços minúsculos: start codes cortados no meio
        got.AddRange(splitter.Push(data[pos..(pos + chunk)], chunk));
        pos += chunk;
    }

    // O último quadro só sai quando o AUD do próximo chega (comportamento conhecido do divisor).
    bool same = got.Count == expected.Count - 1;
    for (int i = 0; same && i < got.Count; i++) same = got[i].AsSpan().SequenceEqual(expected[i]);
    Check(same, $"divisor remonta {expected.Count - 1} quadros idênticos com pedaços de 1 a 6 bytes");

    Check(AnnexBSplitter.IsKeyFrame(expected[0]), "quadro com IDR é keyframe");
    Check(!AnnexBSplitter.IsKeyFrame(expected[1]), "quadro P não é keyframe");
}

void TestLevels()
{
    Section("Nível H.264");
    Check(H264Level.For(640, 480, 30) == "3.1", "640x480 @30 -> 3.1 (igual ao avc1.42001f do test.html)");
    Check(H264Level.For(1280, 720, 30) == "3.1", "1280x720 @30 -> 3.1");
    Check(H264Level.For(1280, 720, 60) == "3.2", "1280x720 @60 -> 3.2 (3.1 não comporta 216000 macroblocos/s)");
    Check(H264Level.For(1920, 1080, 30) == "4.0", "1920x1080 @30 -> 4.0");
    Check(H264Level.For(1920, 1080, 60) == "4.2", "1920x1080 @60 -> 4.2");
}

async Task TestSlowConsumerAsync()
{
    Section("Celular/Wi-Fi lento: a fila descarta até o próximo keyframe");

    // Entram 30 quadros/s, mas o "Wi-Fi" só escoa ~16/s (60 ms por envio). Keyframe a cada 30 quadros.
    var ws = new FakeWebSocket { SendDelay = TimeSpan.FromMilliseconds(60) };
    const int max = 10;
    var sender = new WsSender(ws, max);
    using var cts = new CancellationTokenSource();
    var videoTask = sender.RunVideoAsync(cts.Token);

    long dropped = 0, overflows = 0, worstEnqueueMs = 0;
    int peakQueued = 0;
    var sw = new Stopwatch();
    for (int i = 0; i < 120; i++)
    {
        sw.Restart();
        sender.EnqueueVideo(FakeAu(i, key: i % 30 == 0));
        worstEnqueueMs = Math.Max(worstEnqueueMs, sw.ElapsedMilliseconds);

        var st = sender.TakeStats();
        dropped += st.FramesDropped;
        overflows += st.Overflows;
        peakQueued = Math.Max(peakQueued, st.Queued);
        await Task.Delay(33);
    }

    // Deixa o que sobrou na fila sair.
    for (int i = 0; i < 50 && sender.TakeStats().Queued > 0; i++) await Task.Delay(100);
    await Task.Delay(200);
    cts.Cancel();
    try { await videoTask; } catch (OperationCanceledException) { }

    var sent = ws.Sent.Where(m => m.Type == WebSocketMessageType.Binary).Select(m => m.Data).ToList();
    Console.WriteLine($"  enviados {sent.Count} de 120, descartados {dropped}, estouros da fila {overflows}");

    Check(worstEnqueueMs < 50, $"enfileirar nunca bloqueia o codificador (pior caso {worstEnqueueMs} ms)");
    Check(peakQueued <= max, $"a fila nunca passa do limite (pico {peakQueued} <= {max})");
    Check(dropped > 0 && overflows > 0, "a rede lenta provocou descarte, como esperado");
    Check(sent.Count >= 20, "mesmo com a rede lenta o vídeo continua andando");
    Check(sent.Count > 0 && (sent[0][1] & 1) == 1, "o primeiro quadro enviado é keyframe");

    int resumes = 0;
    bool gapsOk = true;
    for (int i = 1; i < sent.Count; i++)
    {
        if (IndexOf(sent[i]) == IndexOf(sent[i - 1]) + 1) continue;
        resumes++;
        if ((sent[i][1] & 1) == 0) gapsOk = false; // pulou quadros e não recomeçou num keyframe
    }
    Check(resumes > 0 && gapsOk, $"depois de cada descarte ({resumes}) o envio recomeça num keyframe");
    Check(sent.All(m => m[0] == 1 && !ContainsNal(m, 2, 9)), "nenhuma mensagem leva AUD");
}

async Task TestRealEncoderAsync()
{
    Section("ffmpeg de verdade -> divisor -> fila -> WebSocket falso");

    var ffmpeg = FindTool("ffmpeg");
    if (ffmpeg is null)
    {
        Console.WriteLine("  ffmpeg não encontrado (tools\\ffmpeg.exe ou PATH): parte ignorada.");
        return;
    }

    const int w = 640, h = 480, fps = 30;
    var ws = new FakeWebSocket();
    var sender = new WsSender(ws, 30);
    var source = new SyntheticSource(w, h);
    var encoder = new FfmpegEncoder(ffmpeg, source, new GameProfile { BitrateKbps = 2000 }, fps, 0);

    using var cts = new CancellationTokenSource();
    var videoTask = sender.RunVideoAsync(cts.Token);
    var encTask = encoder.RunAsync(sender.EnqueueVideo, cts.Token);

    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(10) && ws.Sent.Count == 0) await Task.Delay(10);
    int firstOutMs = (int)sw.ElapsedMilliseconds;
    await Task.Delay(TimeSpan.FromSeconds(4)); // ~4 s de vídeo
    cts.Cancel();
    encoder.Dispose();
    foreach (var t in new[] { encTask, videoTask })
        try { await t; } catch (OperationCanceledException) { }

    var msgs = ws.Sent.Where(m => m.Type == WebSocketMessageType.Binary).Select(m => m.Data).ToList();
    Console.WriteLine($"  recebi {msgs.Count} mensagens de vídeo");
    Check(msgs.Count >= 3 * fps, $"o vídeo flui em ritmo normal (>= {3 * fps} quadros em ~4 s)");
    if (msgs.Count == 0) return;

    var types = msgs.Select(m => NalTypes(m, 2)).ToList();
    var keyIdx = Enumerable.Range(0, msgs.Count).Where(i => (msgs[i][1] & 1) == 1).ToList();

    Check(msgs.All(m => m[0] == 1), "toda mensagem começa com o tipo 1 (vídeo)");
    Check((msgs[0][1] & 1) == 1, "a primeira mensagem é keyframe (o celular consegue começar a decodificar)");
    Check(types.All(t => !t.Contains(9)), "nenhum quadro leva AUD");
    Check(types.All(t => !t.Contains(6)), "nenhum quadro leva SEI");
    Check(types.All(t => t.Count(x => x is 1 or 5) == 1), "um único slice de imagem por quadro");
    Check(Enumerable.Range(0, msgs.Count).All(i => ((msgs[i][1] & 1) == 1) == types[i].Contains(5)),
        "o bit de keyframe confere com a presença do IDR");
    Check(keyIdx.All(i => types[i].Contains(7) && types[i].Contains(8)), "todo keyframe traz SPS e PPS dentro");
    Check(keyIdx.Count >= 3 && keyIdx.Zip(keyIdx.Skip(1), (a, b) => b - a).All(d => d == fps),
        $"keyframe a cada {FfmpegEncoder.KeyframeIntervalSeconds} s ({fps} quadros): posições {string.Join(",", keyIdx.Take(5))}...");

    // O primeiro SPS: perfil/nível que o MediaCodec do celular vai ver.
    var sps = FindNal(msgs[0], 2, 7);
    if (sps is { Length: >= 4 })
        Check(sps[1] == 66 && sps[3] == 31,
            $"SPS: profile_idc={sps[1]} (66 = baseline), level_idc={sps[3]} (31 = 3.1)");

    // Decodificação independente com o próprio ffmpeg, como um decodificador "de verdade".
    var dir = Path.Combine(Path.GetTempPath(), "gamestream-selftest-" + Environment.ProcessId);
    Directory.CreateDirectory(dir);
    try
    {
        var all = Path.Combine(dir, "all.h264");
        await File.WriteAllBytesAsync(all, msgs.SelectMany(m => m.AsSpan(2).ToArray()).ToArray());
        var (code, err) = await RunAsync(ffmpeg, "-v", "error", "-f", "h264", "-i", all, "-f", "null", "-");
        Check(code == 0 && err.Trim().Length == 0, "o fluxo inteiro decodifica sem erros" + (err.Trim().Length > 0 ? $": {err.Trim()}" : ""));

        // Entrando no meio (como o celular depois de um descarte): a partir do 2º keyframe tem que decodificar limpo.
        var late = Path.Combine(dir, "late.h264");
        await File.WriteAllBytesAsync(late, msgs.Skip(keyIdx[1]).SelectMany(m => m.AsSpan(2).ToArray()).ToArray());
        (code, err) = await RunAsync(ffmpeg, "-v", "error", "-f", "h264", "-i", late, "-f", "null", "-");
        Check(code == 0 && err.Trim().Length == 0, "entrando num keyframe do meio, decodifica sem erros" + (err.Trim().Length > 0 ? $": {err.Trim()}" : ""));

        var ffprobe = FindTool("ffprobe");
        if (ffprobe is not null)
        {
            var (pc, pout) = await RunAsync(ffprobe, "-v", "error", "-count_frames", "-select_streams", "v:0",
                "-show_entries", "stream=nb_read_frames,width,height,profile", "-of", "default=nw=1", all);
            var text = pout.Replace("\r", "");
            Check(pc == 0 && text.Contains($"nb_read_frames={msgs.Count}"), $"o decodificador conta {msgs.Count} quadros ({text.Replace("\n", " ").Trim()})");
            Check(text.Contains($"width={w}") && text.Contains($"height={h}"), $"tamanho do vídeo é {w}x{h}");
        }
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { /* ignora */ }
    }

    Check(firstOutMs < 2000, $"o primeiro quadro sai {firstOutMs} ms depois de ligar o codificador");
}

// ---------------------------------------------------------------------------------------------

static string? FindTool(string name)
{
    var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;

    // 1) tools\ do repositório (sobe a partir da pasta do teste)
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "tools", exe);
        if (File.Exists(candidate)) return candidate;
    }

    // 2) PATH
    foreach (var part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        try
        {
            var candidate = Path.Combine(part, exe);
            if (File.Exists(candidate)) return candidate;
        }
        catch { /* entrada inválida no PATH */ }
    }
    return null;
}

static async Task<(int Code, string Output)> RunAsync(string file, params string[] args)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEndAsync();
    var e = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    return (p.ExitCode, await o + await e);
}

/// <summary>Quadro falso: [AUD] (+ SPS + PPS + IDR nos keyframes), terminando com o número do quadro.</summary>
static byte[] FakeAu(int index, bool key)
{
    var au = new List<byte> { 0, 0, 0, 1, 0x09, 0xF0 };
    if (key)
    {
        au.AddRange(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F });
        au.AddRange(new byte[] { 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80 });
        au.AddRange(new byte[] { 0, 0, 0, 1, 0x65 });
    }
    else
    {
        au.AddRange(new byte[] { 0, 0, 0, 1, 0x41 });
    }
    au.Add(0xAA);
    au.Add((byte)(index + 1));
    return au.ToArray();
}

static int IndexOf(byte[] message) => message[^1] - 1;

static List<int> NalTypes(byte[] data, int offset)
{
    var list = new List<int>();
    for (int i = offset; i + 3 < data.Length; i++)
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
        {
            list.Add(data[i + 3] & 0x1F);
            i += 3;
        }
    return list;
}

static bool ContainsNal(byte[] data, int offset, int type) => NalTypes(data, offset).Contains(type);

/// <summary>Conteúdo (a partir do cabeçalho) do primeiro NAL do tipo pedido.</summary>
static byte[]? FindNal(byte[] data, int offset, int type)
{
    for (int i = offset; i + 3 < data.Length; i++)
    {
        if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1) continue;
        if ((data[i + 3] & 0x1F) != type) continue;

        int start = i + 3, end = data.Length;
        for (int j = start + 1; j + 2 < data.Length; j++)
            if (data[j] == 0 && data[j + 1] == 0 && (data[j + 2] == 1 || data[j + 2] == 0)) { end = j; break; }
        return data[start..end];
    }
    return null;
}

/// <summary>Quadros BGRA com um quadrado branco andando: gera quadros P de verdade no x264.</summary>
sealed class SyntheticSource : IFrameSource
{
    private readonly byte[] _frame;
    private int _n;

    public SyntheticSource(int width, int height)
    {
        Width = width;
        Height = height;
        _frame = new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }

    public byte[] NextFrame()
    {
        Array.Clear(_frame);
        int x = (_n++ * 7) % (Width - 60);
        for (int y = 100; y < 160; y++)
            for (int k = 0; k < 60; k++)
            {
                int o = (y * Width + x + k) * 4;
                _frame[o] = _frame[o + 1] = _frame[o + 2] = 255;
            }
        return _frame;
    }
}

/// <summary>WebSocket de mentira: guarda o que foi enviado e pode demorar para "enviar" (celular lento).</summary>
sealed class FakeWebSocket : WebSocket
{
    private readonly object _lock = new();
    private readonly List<(WebSocketMessageType Type, byte[] Data)> _sent = new();

    public TimeSpan SendDelay { get; init; }

    public List<(WebSocketMessageType Type, byte[] Data)> Sent
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => WebSocketState.Open;

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, cancellationToken);
        lock (_lock) _sent.Add((messageType, buffer.ToArray()));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken) =>
        SendAsync((ReadOnlyMemory<byte>)buffer, messageType, endOfMessage, cancellationToken).AsTask();

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Dispose() { }
}
