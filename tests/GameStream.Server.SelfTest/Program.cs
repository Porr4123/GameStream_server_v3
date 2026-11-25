using System.Diagnostics;
using System.Net.WebSockets;
using GameStream.Server.Audio;
using GameStream.Server.Config;
using GameStream.Server.Diagnostics;
using Microsoft.Extensions.Logging;
using GameStream.Server.Input;
using GameStream.Server.Net;
using GameStream.Server.Sandbox;
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
TestEncoderSettings();
TestStreamGeometry();
await TestFitModesAsync();
await TestQualityEncoderAsync();
TestKeyPolicy();
TestResourceLimits();
TestFrameInspector();
TestVideoProbe();
TestDiagnosis();
TestClientActivity();
await TestFileLoggerAndRecorderAsync();
await TestSpsAndFfmpegCheckAsync();
await TestFrozenGameScenarioAsync();
TestDebugHub();
await TestAudioAsync();

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
    var encoder = new FfmpegEncoder(ffmpeg, source,
        new GameProfile { BitrateKbps = 2000, Crf = 0, Preset = "ultrafast", H264Profile = "baseline", EncoderThreads = 1 }, fps, 0);

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

void TestEncoderSettings()
{
    Section("Qualidade: parâmetros do x264 e ajuste de tamanho");

    var bad = new GameProfile
    {
        Preset = "veryfast -x264-params evil=1", H264Profile = "../../x", ScaleMode = "bicubic,drawtext=file=/etc/passwd", Crf = 999,
        BitrateKbps = 1
    };
    var s = EncoderSettings.From(bad, 1280, 720, 60, 99);
    Check(s.Preset == "veryfast" && s.Profile == "main" && s.ScaleFlags == "bicubic", "texto estranho no game.json cai no padrão (não vira argumento do ffmpeg)");
    Check(s.Crf == 40 && s.BitrateKbps == 200 && s.Threads == 8, "crf, taxa e threads ficam dentro de limites");
    Check(EncoderSettings.From(new GameProfile { Crf = 0 }, 1280, 720, 60, 2).Crf == 0, "crf 0 desliga o modo de qualidade constante");
    Check(EncoderSettings.From(new GameProfile { Preset = " ULTRAFAST ", ScaleMode = "Lanczos" }, 1280, 720, 30, 1) is { Preset: "ultrafast", ScaleFlags: "lanczos" },
        "maiúsculas e espaços são aceitos");

    var d = new GameProfile();
    Check(d.StreamWidth == 1280 && d.StreamHeight == 720 && d.Fps == 60 && d.Crf == 21 && d.Preset == "veryfast" && d.H264Profile == "main",
        "padrões novos: 1280x720, 60 fps, CRF 21, veryfast, perfil Main");

    Check(EncoderSettings.Fit(640, 480, 1280, 720) == (960, 720), "640x480 em 1280x720 = 960x720 (faixas pretas dos lados)");
    Check(EncoderSettings.Fit(1920, 1080, 1280, 720) == (1280, 720), "1920x1080 em 1280x720 = 1280x720");
    Check(EncoderSettings.Fit(1280, 720, 1280, 720) == (1280, 720), "tamanho igual: sem ajuste");
    var odd = EncoderSettings.Fit(817, 625, 1280, 720);
    Check(odd.W % 2 == 0 && odd.H % 2 == 0 && odd.W <= 1280 && odd.H <= 720, $"tamanho ímpar vira par ({odd.W}x{odd.H})");

    var args = FfmpegEncoder.BuildArgs(EncoderSettings.From(d, 1280, 720, 60, 3), 816, 624);
    string line = string.Join(' ', args);
    Check(line.Contains("-video_size 816x624"), "a entrada é do tamanho do jogo");
    Check(line.Contains("scale=942:720") && line.Contains("pad=1280:720:(ow-iw)/2:(oh-ih)/2"), "816x624 vira 942x720 (proporção mantida) e ganha faixas até 1280x720");
    Check(line.Contains("-crf 21") && line.Contains("-maxrate 8000k") && !line.Contains("-b:v"), "CRF 21 com teto de 8000 kbps");
    Check(line.Contains("-profile:v main") && line.Contains("-level:v 3.2") && line.Contains("-preset veryfast"), "perfil Main, nível 3.2 (720p60), preset veryfast");
    Check(line.Contains("out_color_matrix=bt709") && line.Contains("-colorspace bt709") && line.Contains("-color_range tv"), "cor BT.709 na conversão e marcada no fluxo");
    Check(!line.Contains("slices=1"), "com várias threads o x264 pode dividir o quadro (mais rápido)");
    Check(FfmpegEncoder.BuildArgs(EncoderSettings.From(d, 1280, 720, 60, 1), 1280, 720).Contains("aud=1:repeat-headers=1:slices=1:colorprim=bt709:transfer=bt709:colormatrix=bt709"), "com 1 thread: 1 slice por quadro e VUI BT.709");
    Check(string.Join(' ', FfmpegEncoder.BuildArgs(EncoderSettings.From(new GameProfile { Crf = 0, BitrateKbps = 3000 }, 1280, 720, 30, 1), 1280, 720)).Contains("-b:v 3000k"),
        "crf 0 = taxa fixa");
}

async Task TestQualityEncoderAsync()
{
    Section("Qualidade: vídeo real com os padrões novos");
    var ffmpeg = FindTool("ffmpeg");
    if (ffmpeg is null) { Console.WriteLine("  ffmpeg não encontrado: parte ignorada."); return; }
    var ffprobe = FindTool("ffprobe");

    // ---- 1) O jogo muda de tamanho no meio da sessão (espera 1280x720 -> jogo 816x624 -> jogo 640x480): a saída não muda ----
    const int fps = 60;
    var ws = new FakeWebSocket();
    var sender = new WsSender(ws, 120);
    var source = new ResizingSource((1280, 720), (816, 624), (640, 480));
    var profile = new GameProfile();
    var encoder = new FfmpegEncoder(ffmpeg, source, profile, fps, 0, null, null, 1280, 720);

    using var cts = new CancellationTokenSource();
    var videoTask = sender.RunVideoAsync(cts.Token);
    var encTask = encoder.RunAsync(sender.EnqueueVideo, cts.Token);

    var perPhase = new List<int>();
    for (int phase = 0; phase < 3; phase++)
    {
        int before = ws.Sent.Count;
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        perPhase.Add(ws.Sent.Count - before);
        source.Next();
    }
    cts.Cancel();
    encoder.Dispose();
    foreach (var t in new[] { encTask, videoTask })
        try { await t; } catch (OperationCanceledException) { }

    var msgs = ws.Sent.Where(m => m.Type == WebSocketMessageType.Binary).Select(m => m.Data).ToList();
    Console.WriteLine($"  quadros por fase (2,5 s cada, alvo {fps} fps): {string.Join(" / ", perPhase)}  -> {perPhase.Sum() / 7.5:0} quadros/s no total, neste computador");
    Check(perPhase.All(n => n >= 2.5 * 30), "o vídeo continua fluindo (>= 30 quadros/s) em cada fase, inclusive depois de cada mudança de tamanho");
    Check(msgs.Count > 0 && (msgs[0][1] & 1) == 1, "começa num keyframe");

    var keys = Enumerable.Range(0, msgs.Count).Where(i => (msgs[i][1] & 1) == 1).ToList();
    var spsList = keys.Select(i => FindNal(msgs[i], 2, 7)).ToList();
    Check(spsList.All(x => x is { Length: >= 4 }), "todo keyframe traz SPS");
    var infos = keys.Select(i => GameStream.Server.Diagnostics.H264Info.ParseSps(msgs[i])).ToList();
    Check(infos.All(i => i is { Width: 1280, Height: 720 }), $"a saída é sempre 1280x720, mesmo com o jogo em 3 tamanhos ({infos.Count} keyframes conferidos)");
    Check(infos.All(i => i is { ProfileIdc: 77, LevelIdc: 32 }), "sempre perfil Main nível 3.2: o decodificador do celular não precisa ser reconfigurado");
    Check(infos.Select(i => i?.ToString()).Distinct().Count() == 1, "SPS idêntico antes e depois de cada reinício do ffmpeg");

    var dir = Path.Combine(Path.GetTempPath(), "gamestream-selftest-q-" + Environment.ProcessId);
    Directory.CreateDirectory(dir);
    try
    {
        var all = Path.Combine(dir, "all.h264");
        await File.WriteAllBytesAsync(all, msgs.SelectMany(m => m.AsSpan(2).ToArray()).ToArray());
        var (code, err) = await RunAsync(ffmpeg, "-v", "error", "-f", "h264", "-i", all, "-f", "null", "-");
        Check(code == 0 && err.Trim().Length == 0, "o fluxo inteiro (com as 2 trocas de tamanho) decodifica sem erros" + (err.Trim().Length > 0 ? $": {err.Trim()}" : ""));

        if (ffprobe is not null)
        {
            var (pc, pout) = await RunAsync(ffprobe, "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height,profile,color_space,color_range,color_primaries,color_transfer", "-of", "default=nw=1", all);
            var text = pout.Replace("\r", "").Replace("\n", " ");
            Check(pc == 0 && text.Contains("width=1280") && text.Contains("height=720") && text.Contains("profile=Main"), $"ffprobe: {text.Trim()}");
            Check(text.Contains("color_space=bt709") && text.Contains("color_primaries=bt709") && text.Contains("color_transfer=bt709"),
                "o fluxo declara BT.709 (o celular reproduz as cores certas)");
        }

        // ---- 2) Nitidez medida: o MESMO conteúdo (1280x720) com a configuração antiga e com a nova ----
        const int frames = 90;
        var rich = new RichSource(1280, 720);
        var srcRaw = Path.Combine(dir, "src.raw");
        await using (var fs = File.Create(srcRaw))
            for (int i = 0; i < frames; i++) await fs.WriteAsync(rich.FrameAt(i));

        async Task<double> SsimAsync(GameProfile p, string tag)
        {
            var wsq = new FakeWebSocket();
            var snd = new WsSender(wsq, 400);
            var src = new ReplaySource(rich, frames);
            var enc = new FfmpegEncoder(ffmpeg, src, p, 60, 0, null, null, 1280, 720);
            using var c = new CancellationTokenSource();
            var vt = snd.RunVideoAsync(c.Token);
            var et = enc.RunAsync(snd.EnqueueVideo, c.Token);
            var sw2 = Stopwatch.StartNew();
            while (src.Done == false && sw2.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
            await Task.Delay(500);
            c.Cancel(); enc.Dispose();
            foreach (var t in new[] { et, vt }) try { await t; } catch (OperationCanceledException) { }

            var bytes = wsq.Sent.Where(m => m.Type == WebSocketMessageType.Binary).SelectMany(m => m.Data.AsSpan(2).ToArray()).ToArray();
            var file = Path.Combine(dir, tag + ".h264");
            await File.WriteAllBytesAsync(file, bytes);
            var (c2, e2) = await RunAsync(ffmpeg, "-v", "info", "-f", "rawvideo", "-pixel_format", "bgr0", "-video_size", "1280x720", "-framerate", "60", "-i", srcRaw,
                "-f", "h264", "-i", file, "-lavfi", "[0:v]format=yuv420p[a];[1:v]format=yuv420p[b];[b][a]ssim", "-f", "null", "-");
            var m = System.Text.RegularExpressions.Regex.Match(e2, @"All:([0-9.]+)");
            double kbps = bytes.Length * 8.0 / 1000 / (frames / 60.0);
            Console.WriteLine($"  {tag}: SSIM {(m.Success ? m.Groups[1].Value : "?")}, ~{kbps:0} kbps");
            return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
        }

        double oldQ = await SsimAsync(new GameProfile { BitrateKbps = 2000, Crf = 0, Preset = "ultrafast", H264Profile = "baseline", EncoderThreads = 1 }, "antiga (baseline, ultrafast, 2000 kbps)");
        double newQ = await SsimAsync(new GameProfile(), "nova (main, veryfast, CRF 21, teto 8000 kbps)");
        Check(oldQ > 0 && newQ > 0, "SSIM calculado nas duas configurações");
        Check(newQ > oldQ, $"a configuração nova é mais fiel à imagem original ({newQ:0.0000} > {oldQ:0.0000})");
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { /* ignora */ }
    }
}

// ---------------------------------------------------------------------------------------------

void TestKeyPolicy()
{
    Section("Teclado: só letras, números, setas, CTRL, SHIFT e ESC");

    var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") expected.Add(c.ToString());
    foreach (var n in new[] { "UP", "DOWN", "LEFT", "RIGHT", "CTRL", "SHIFT", "ESC" }) expected.Add(n);

    Check(KeyMap.Names.Count == expected.Count && expected.All(n => KeyMap.TryGet(n, out _)),
        $"a lista tem exatamente as {expected.Count} teclas permitidas (letras, números, setas, CTRL, SHIFT, ESC)");
    Check(KeyMap.Names.All(expected.Contains), "e nenhuma tecla a mais");

    var forbidden = new List<string>
    {
        "PRINTSCREEN", "PRTSC", "PRINT", "SNAPSHOT", "SYSRQ", "WIN", "WINDOWS", "LWIN", "RWIN", "META", "SUPER", "CMD", "APPS", "MENU",
        "ALT", "LALT", "RALT", "ALTGR", "TAB", "ENTER", "RETURN", "SPACE", "BACKSPACE", "DELETE", "DEL", "INSERT", "HOME", "END",
        "PGUP", "PGDN", "CAPSLOCK", "NUMLOCK", "SCROLLLOCK", "PAUSE", "BREAK", "SLEEP", "POWER", "", " ", "A ", "ESC ", "CTRL+ALT"
    };
    for (int i = 1; i <= 24; i++) forbidden.Add($"F{i}");
    Check(forbidden.All(n => !KeyMap.TryGet(n, out _)), $"nenhum destes tem scancode ({forbidden.Count} nomes: F1-F24, PrintScreen, Windows, ALT, TAB, ENTER...)");

    var codes = KeyMap.Names.Select(n => { KeyMap.TryGet(n, out var k); return k; }).ToList();
    Check(codes.All(KeyMap.IsKnown), "IsKnown aceita os scancodes da lista");
    Check(codes.Select(c => (c.Scan, c.Extended)).Distinct().Count() == codes.Count, "nenhuma tecla repete scancode");

    // Scancodes que NUNCA podem sair: F1-F10 (3B-44), F11/F12 (57/58), PrintScreen (37 estendido / 54), Windows (5B/5C estendido), Menu (5D).
    var neverScans = new List<ushort>();
    for (ushort sc = 0x3B; sc <= 0x44; sc++) neverScans.Add(sc);
    neverScans.AddRange(new ushort[] { 0x57, 0x58, 0x37, 0x54, 0x5B, 0x5C, 0x5D, 0x38, 0x0F, 0x1C, 0x39, 0x0E });
    Check(neverScans.All(sc => !KeyMap.IsKnown(new KeyCode(sc, false)) && !KeyMap.IsKnown(new KeyCode(sc, true))),
        "nenhum scancode de F1-F12, PrintScreen, Windows, Menu, ALT, TAB, ENTER, SPACE ou BACKSPACE está cadastrado");

    // Setas são estendidas; as mesmas posições SEM "estendida" seriam o teclado numérico: não podem valer.
    Check(KeyMap.TryGet("UP", out var up) && up.Extended && !KeyMap.IsKnown(new KeyCode(up.Scan, false)),
        "seta é tecla estendida (o numpad com o mesmo scancode não é aceito)");

    // Combinações que o Windows trata por fora do jogo.
    Check(KeyPolicy.IsBlockedCombo("ESC", new[] { "CTRL" }), "ESC com CTRL apertado é bloqueado (CTRL+ESC = menu Iniciar)");
    Check(KeyPolicy.IsBlockedCombo("ESC", new[] { "CTRL", "SHIFT" }), "ESC com CTRL+SHIFT é bloqueado (Gerenciador de Tarefas)");
    Check(KeyPolicy.IsBlockedCombo("CTRL", new[] { "ESC" }), "CTRL com ESC já apertado também é bloqueado");
    Check(KeyPolicy.IsBlockedCombo("esc", new[] { "ctrl" }), "sem diferença entre maiúsculas e minúsculas");
    Check(!KeyPolicy.IsBlockedCombo("ESC", new[] { "SHIFT", "W" }), "ESC sozinho (ou com SHIFT/letras) funciona");
    Check(!KeyPolicy.IsBlockedCombo("CTRL", new[] { "SHIFT", "A" }) && !KeyPolicy.IsBlockedCombo("A", new[] { "CTRL" }), "CTRL com letras funciona");

    var def = GameProfile.CreateDefault("x");
    Check(def.AllowedKeys.All(k => KeyMap.TryGet(k, out _)), "o perfil padrão só usa teclas da lista");
    Check(def.Layout.SelectMany(b => (b.Keys ?? new List<string>()).Concat(b.Key is null ? Array.Empty<string>() : new[] { b.Key }))
              .All(k => KeyMap.TryGet(k, out _)), "e o layout padrão também (nenhum botão morto)");
    Check(def.HideMode == "transparent", "ocultar padrão: transparente");
}

void TestStreamGeometry()
{
    Section("Tela do cliente: proporção do quadro (sw/sh/fit)");
    Check(StreamGeometry.CanvasSize(1280, 720, 0, 0) == (1280, 720), "sem tela informada: tamanho do perfil");
    Check(StreamGeometry.CanvasSize(1280, 720, 1920, 1080) == (1280, 720), "tela 16:9: o mesmo 1280x720");

    var wide = StreamGeometry.CanvasSize(1280, 720, 2400, 1080);
    double ratio = (double)wide.Width / wide.Height;
    Check(Math.Abs(ratio - 2400.0 / 1080) < 0.03 && wide.Width % 2 == 0 && wide.Height % 2 == 0, $"tela 20:9 (2400x1080): quadro {wide.Width}x{wide.Height} tem a proporção da tela");
    Check(StreamGeometry.Macroblocks(wide.Width, wide.Height) <= StreamGeometry.Macroblocks(1280, 720), "e não passa da quantidade de pixels do perfil (mesmo custo de CPU/rede)");

    var tablet = StreamGeometry.CanvasSize(1280, 720, 1600, 1200);
    Check(tablet == (960, 720), $"tela 4:3: mantém a altura do perfil ({tablet.Width}x{tablet.Height})");
    var tall = StreamGeometry.CanvasSize(1280, 720, 1080, 2400);
    Check(tall.Width >= tall.Height, "tela em pé é tratada como proporção mínima 1:1 (o cliente joga deitado)");
    var extreme = StreamGeometry.CanvasSize(1280, 720, 5000, 1000);
    Check(Math.Abs((double)extreme.Width / extreme.Height - 2.5) < 0.05, "proporção muito comprida é limitada a 2,5:1");
    Check(StreamGeometry.CanvasSize(640, 480, 2400, 1080) is { Width: > 0 } c && StreamGeometry.Macroblocks(c.Width, c.Height) <= StreamGeometry.Macroblocks(640, 480),
        "perfil pequeno (640x480) também respeita o próprio orçamento de pixels");

    Check(StreamGeometry.ScaleBitrate(8000, 1280 * 720, 640, 360) == 4000, "teto de taxa cai com menos pixels (mínimo 0,5x)");
    Check(StreamGeometry.ScaleBitrate(8000, 1280 * 720, 5000, 3000) == 16000, "e sobe com mais pixels (máximo 2x)");
    Check(StreamGeometry.ParseFit("FILL") == FitMode.Fill && StreamGeometry.ParseFit(" stretch ") == FitMode.Stretch
          && StreamGeometry.ParseFit("fit") == FitMode.Fit && StreamGeometry.ParseFit(null) == FitMode.Fit && StreamGeometry.ParseFit("x; rm") == FitMode.Fit,
        "fit/fill/stretch; qualquer outro texto vira fit");

    Check(StreamGeometry.PreScale(1280, 720, 1920, 1080, FitMode.Fit) is > 0.66 and < 0.67, "jogo 1080p em quadro 720p: reduz para 2/3 antes do ffmpeg");
    Check(StreamGeometry.PreScale(1280, 720, 640, 480, FitMode.Fit) == 1.0, "jogo menor que o quadro: nunca aumenta no Windows (o ffmpeg aumenta)");
    Check(StreamGeometry.PreScale(1280, 720, 1920, 1200, FitMode.Fill) > StreamGeometry.PreScale(1280, 720, 1920, 1200, FitMode.Fit),
        "Fill reduz menos que Fit (precisa cobrir o quadro)");

    var cover = EncoderSettings.Cover(640, 480, 1280, 720);
    Check(cover == (1280, 960), $"Fill de 640x480 em 1280x720 = escala para {cover.W}x{cover.H} e corta");
    var fill = string.Join(' ', FfmpegEncoder.BuildArgs(EncoderSettings.From(new GameProfile(), 1280, 720, 60, 2) with { Mode = FitMode.Fill }, 640, 480));
    Check(fill.Contains("scale=1280:960") && fill.Contains("crop=1280:720") && !fill.Contains("pad="), "Fill: scale + crop, sem faixas");
    var stretch = string.Join(' ', FfmpegEncoder.BuildArgs(EncoderSettings.From(new GameProfile(), 1280, 720, 60, 2) with { Mode = FitMode.Stretch }, 640, 480));
    Check(stretch.Contains("scale=1280:720") && !stretch.Contains("pad=") && !stretch.Contains("crop="), "Stretch: scale direto para o tamanho final");
}

async Task TestFitModesAsync()
{
    Section("Modos fit / fill / stretch com ffmpeg de verdade");
    var ffmpeg = FindTool("ffmpeg");
    if (ffmpeg is null) { Console.WriteLine("  ffmpeg não encontrado: parte ignorada."); return; }

    // Jogo 640x480 todo BRANCO dentro de um quadro 1280x720 (16:9): em Fit sobram faixas pretas dos lados.
    foreach (var mode in new[] { FitMode.Fit, FitMode.Fill, FitMode.Stretch })
    {
        var ws = new FakeWebSocket();
        var sender = new WsSender(ws, 60);
        var src = new SolidSource(640, 480, 255);
        var enc = new FfmpegEncoder(ffmpeg, src, new GameProfile { Fps = 30 }, 30, 0, null, null, 1280, 720, null, mode);
        using var cts = new CancellationTokenSource();
        var vt = sender.RunVideoAsync(cts.Token);
        var et = enc.RunAsync(sender.EnqueueVideo, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        cts.Cancel(); enc.Dispose();
        foreach (var t in new[] { et, vt }) try { await t; } catch (OperationCanceledException) { }

        var bytes = ws.Sent.Where(m => m.Type == WebSocketMessageType.Binary).SelectMany(m => m.Data.AsSpan(2).ToArray()).ToArray();
        var dir = Path.Combine(Path.GetTempPath(), "gamestream-selftest-fit-" + Environment.ProcessId + "-" + mode);
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "x.h264");
            await File.WriteAllBytesAsync(f, bytes);
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "error", "-f", "h264", "-i", f, "-frames:v", "1", "-vf", "format=gray", "-f", "rawvideo", "pipe:1" }) psi.ArgumentList.Add(a);
            using var pr = Process.Start(psi)!;
            using var ms = new MemoryStream();
            await pr.StandardOutput.BaseStream.CopyToAsync(ms);
            await pr.WaitForExitAsync();
            var gray = ms.ToArray();

            Check(gray.Length == 1280 * 720, $"{mode}: o quadro decodificado é 1280x720 ({gray.Length} bytes)");
            if (gray.Length != 1280 * 720) continue;
            byte At(int x, int y) => gray[y * 1280 + x];
            bool leftBlack = At(10, 360) < 40 && At(1269, 360) < 40;
            bool centerWhite = At(640, 360) > 200;
            bool cornerWhite = At(10, 10) > 200 && At(1269, 709) > 200;

            if (mode == FitMode.Fit) Check(leftBlack && centerWhite, "Fit: faixas pretas nas laterais e o jogo inteiro no centro");
            else Check(!leftBlack && cornerWhite && centerWhite, $"{mode}: a imagem ocupa o quadro todo, sem faixas pretas");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* ignora */ } }
    }
}

void TestResourceLimits()
{
    Section("Limites do jogo (1 núcleo, RAM dinâmica)");
    var fixedLimit = ResourceLimits.Compute(1, 2048, 0, 768, () => 9999);
    Check(fixedLimit.MemoryMb == 2048 && !fixedLimit.MemoryIsDynamic, "memoryMb > 0 continua sendo limite fixo");

    var dyn = ResourceLimits.Compute(1, 0, 0, 768, () => 6000);
    Check(dyn.MemoryIsDynamic && dyn.MemoryMb == 6000 - 768, $"dinâmico = RAM livre - reserva ({dyn.MemoryMb} MB)");
    Check(ResourceLimits.Compute(1, 0, 0, 768, () => 8000).MemoryMb > dyn.MemoryMb, "com mais RAM livre o teto sobe");
    Check(ResourceLimits.Compute(1, 0, 0, 768, () => 500).MemoryMb == ResourceLimits.MinAutoMb, "pouca RAM livre: nunca abaixo do mínimo");

    Check(dyn.GameMask == 1UL && dyn.Cores == 1, "1 núcleo: o jogo fica só no núcleo 0 (máscara 0x1)");
    if (Environment.ProcessorCount > 1)
        Check((dyn.ServerMask & 1UL) == 0 && dyn.ServerMask != 0, "o ffmpeg/servidor ficam nos outros núcleos");
    Check(new ServerOptions().GameCores == 1 && new ServerOptions().GameMemoryMb == 0, "padrões novos: 1 núcleo, RAM dinâmica");
}

void TestFrameInspector()
{
    Section("FrameInspector (preto? parado?)");
    const int w = 64, h = 48;
    var black = new byte[w * h * 4];
    var white = new byte[w * h * 4];
    Array.Fill(white, (byte)255);

    var sb = FrameInspector.Analyze(black, w, h);
    var sw = FrameInspector.Analyze(white, w, h);
    Check(sb.BlackRatio > 0.999, $"quadro preto: {sb.BlackRatio:P0} preto");
    Check(sw.BlackRatio < 0.001 && sw.MeanLuma > 250, $"quadro branco: {sw.BlackRatio:P0} preto, brilho {sw.MeanLuma:0}");

    // Um pixel que NÃO está na grade de amostragem (1,1 com passo 2) tem que mudar o hash:
    // senão um jogo que só anima um cantinho seria dado como "parado".
    var a = new byte[w * h * 4];
    var b = (byte[])a.Clone();
    b[(1 * w + 1) * 4 + 2] = 200;
    Check(FrameInspector.Analyze(a, w, h).Hash != FrameInspector.Analyze(b, w, h).Hash,
        "mudar 1 pixel fora da grade de amostragem muda o hash");

    var c = (byte[])a.Clone();
    for (int i = 3; i < c.Length; i += 4) c[i] = 255; // só o canal alfa
    Check(FrameInspector.Analyze(a, w, h).Hash == FrameInspector.Analyze(c, w, h).Hash, "o canal alfa não conta para o hash");

    var odd = new byte[(w * h + 1) * 4];
    odd[^2] = 9;
    Check(FrameInspector.FullHash(odd, odd.Length) != FrameInspector.FullHash(new byte[odd.Length], odd.Length),
        "o hash vê também os bytes da sobra (tamanho não múltiplo de 8)");

    // BMP: cabeçalho e ordem das cores (BGR, linhas de baixo para cima).
    var img = new byte[2 * 2 * 4];
    img[0] = 10; img[1] = 20; img[2] = 30;   // pixel (0,0): B,G,R
    var bmp = FrameInspector.ToBmp24(img, 2, 2);
    int rowSize = (2 * 3 + 3) & ~3;
    Check(bmp[0] == 'B' && bmp[1] == 'M' && bmp.Length == 54 + rowSize * 2, $"BMP tem cabeçalho BM e {bmp.Length} bytes");
    Check(bmp[54 + rowSize] == 10 && bmp[54 + rowSize + 1] == 20 && bmp[54 + rowSize + 2] == 30,
        "BMP guarda o pixel de cima na última linha, em ordem B,G,R");
}

void TestVideoProbe()
{
    Section("VideoProbe (há quanto tempo a imagem está igual)");
    var now = TimeSpan.Zero;
    var probe = new VideoProbe(64, 48, () => now);
    var frame = new byte[64 * 48 * 4];

    for (int i = 0; i < 10; i++)
    {
        now += TimeSpan.FromSeconds(1);
        probe.Record(frame, TimeSpan.FromMilliseconds(4));
    }
    var snap = probe.Take();
    Check(snap.Frames == 10 && snap.Changes == 1, $"10 quadros iguais = 1 mudança (a do primeiro): {snap.Changes}");
    Check(snap.StillSeconds >= 9 && snap.StillSeconds <= 10.1, $"parada há ~9-10 s ({snap.StillSeconds:0.0})");
    Check(Math.Abs(snap.AvgCaptureMs - 4) < 0.01, "tempo médio de captura");
    Check(snap.BlackRatio > 0.99, "quadro todo preto é visto como preto");

    frame[100] = 77;
    now += TimeSpan.FromSeconds(1);
    probe.Record(frame, TimeSpan.Zero);
    Check(probe.StillSeconds < 0.5, "qualquer mudança zera o \"parada há\"");

    var again = probe.Take();
    Check(again.Frames == 1 && again.Changes == 1 && again.TotalFrames == 11 && again.TotalChanges == 2,
        "Take() zera a janela mas mantém os totais (11 quadros, 2 mudanças)");

    probe.ResetStill();
    now += TimeSpan.FromSeconds(3);
    Check(Math.Abs(probe.StillSeconds - 3) < 0.01, "ResetStill recomeça a contagem");

    var latest = probe.LatestFrame();
    Check(latest is not null && latest.Length == frame.Length && !ReferenceEquals(latest, frame), "guarda uma CÓPIA do quadro recente");
}

void TestDiagnosis()
{
    Section("Diagnóstico automático");
    var healthy = new DiagInput
    {
        FramesSentTotal = 500, GameLaunched = true, GameShown = true, WindowForeground = true,
        GameCpuPercent = 40, StillSeconds = 0.1, BlackRatio = 0.1
    };
    Check(Diagnosis.Evaluate(healthy).Count == 0, "jogo saudável: nenhum aviso");

    string Codes(DiagInput d) => string.Join(",", Diagnosis.Evaluate(d).Select(f => f.Code));
    Finding Get(DiagInput d, string code) => Diagnosis.Evaluate(d).First(f => f.Code == code);

    var blackStill = healthy with { StillSeconds = 20, BlackRatio = 1.0, GameCpuPercent = 50 };
    var f1 = Get(blackStill, "IMAGEM_PARADA");
    Check(f1.Message.Contains("PRETA") && f1.Message.Contains("hideWindow"), "preta+parada: diz que é PRETA e sugere hideWindow=false");

    var idle = healthy with { StillSeconds = 20, GameCpuPercent = 0.2, WindowForeground = false };
    var f2 = Get(idle, "IMAGEM_PARADA");
    Check(f2.Message.Contains("ocioso") && f2.Message.Contains("PAUSADO") && f2.Message.Contains("NÃO está em primeiro plano"),
        "parada + jogo ocioso + sem foco: aponta jogo pausado/sem foco");

    var stale = healthy with { StillSeconds = 20, GameCpuPercent = 60, WindowForeground = true };
    var f3 = Get(stale, "IMAGEM_PARADA");
    Check(f3.Message.Contains("ATIVO") && f3.Message.Contains("Chromium"), "parada + jogo ativo: aponta captura devolvendo quadro velho (Chromium/NW.js)");

    Check(!Codes(healthy with { StillSeconds = 3 }).Contains("IMAGEM_PARADA"), "3 s parado ainda não é alarme (tela estática é normal)");
    Check(Codes(healthy with { WindowHung = true, StillSeconds = 30 }) == "JANELA_TRAVADA", "janela sem resposta: só JANELA_TRAVADA (não duplica com imagem parada)");
    Check(Codes(healthy with { WindowMinimized = true }).Contains("JANELA_MINIMIZADA"), "janela minimizada");
    Check(Get(healthy with { TargetFps = 60, FpsSent = 30, AvgCaptureMs = 3 }, "FPS_BAIXO").Message.Contains("CODIFICAÇÃO"), "30 de 60 fps com captura rápida: culpa a codificação/CPU");
    Check(Get(healthy with { TargetFps = 60, FpsSent = 30, AvgCaptureMs = 30 }, "FPS_BAIXO").Message.Contains("CAPTURA"), "30 de 60 fps com captura lenta: culpa a captura");
    Check(!Codes(healthy with { TargetFps = 60, FpsSent = 55 }).Contains("FPS_BAIXO"), "55 de 60 fps não é alarme");
    Check(Codes(healthy with { CaptureAttempts = 100, CaptureFailures = 100 }).Contains("CAPTURA_FALHA"), "captura falhando sempre");
    Check(Codes(healthy with { CaptureAttempts = 100, CaptureFailures = 3 }) == "CAPTURA_FALHA_PARCIAL", "captura falhando às vezes");
    Check(Codes(healthy with { AvgCaptureMs = 90 }) == "CAPTURA_LENTA", "captura lenta");
    Check(Codes(healthy with { FramesSentTotal = 0 }).Contains("SEM_VIDEO"), "nenhum quadro enviado");
    Check(Codes(healthy with { DroppedInWindow = 12 }) == "REDE_LENTA", "descartes por rede lenta");
    Check(Codes(healthy with { KeysRejected = 5 }).Contains("TECLAS_RECUSADAS"), "teclas recusadas (fora de allowedKeys)");

    var focus = Get(healthy with { FocusFailures = 3, ForegroundWindow = "\"Console\" [ConsoleWindowClass]" }, "SEM_FOCO");
    Check(focus.Level == FindingLevel.Error && focus.Message.Contains("ConsoleWindowClass"), "foco recusado: erro que diz para ONDE as teclas estão indo");

    var noEffect = stale with { KeysAccepted = 10, SecondsSinceLastKey = 4, StillSeconds = 20 };
    Check(Codes(noEffect).Contains("TECLA_SEM_EFEITO"), "tecla enviada e imagem não mudou depois: TECLA_SEM_EFEITO");
    Check(!Codes(stale with { KeysAccepted = 10, SecondsSinceLastKey = 15, StillSeconds = 20 }).Contains("TECLA_SEM_EFEITO") == false,
        "tecla enviada antes de a imagem parar ainda conta como sem efeito se nada mudou depois");
    Check(!Codes(stale with { KeysAccepted = 10, SecondsSinceLastKey = 25, StillSeconds = 20 }).Contains("TECLA_SEM_EFEITO"),
        "tecla de ANTES de a imagem parar não é culpada");

    Check(Codes(blackStill with { GameShown = false }) == "", "tela de espera não gera alarme de imagem");
    Check(Codes(healthy with { GameShown = false, ProcessAlive = false, WindowExists = false }).Contains("JOGO_FECHOU"), "jogo fechou");

    var hideOff = Get(blackStill with { HideWindow = false }, "IMAGEM_PARADA");
    Check(hideOff.Message.Contains("captureMode"), "com hideWindow=false já sugere captureMode=screen");

    // ---- tracker: não enche o log ----
    var tracker = new DiagnosisTracker(TimeSpan.FromSeconds(30));
    var events = new List<string>();
    void Emit(Finding fnd, bool resolved) => events.Add((resolved ? "resolvido:" : "novo:") + fnd.Code);
    var t0 = DateTime.UtcNow;
    var bad = Diagnosis.Evaluate(blackStill);
    tracker.Update(bad, t0, Emit);
    tracker.Update(bad, t0.AddSeconds(5), Emit);
    tracker.Update(bad, t0.AddSeconds(10), Emit);
    Check(events.Count == 1 && events[0] == "novo:IMAGEM_PARADA", "mesmo problema 3x seguidas = escrito 1 vez");
    tracker.Update(bad, t0.AddSeconds(35), Emit);
    Check(events.Count == 2, "repete depois de 30 s se continuar");
    tracker.Update(Diagnosis.Evaluate(healthy), t0.AddSeconds(40), Emit);
    Check(events.Last() == "resolvido:IMAGEM_PARADA", "avisa quando o problema some");

    Console.WriteLine("\n  Exemplo do que o log mostrará (jogo ativo, imagem congelada):");
    foreach (var fnd in Diagnosis.Evaluate(stale)) Console.WriteLine($"    DIAGNÓSTICO {fnd.Level} [{fnd.Code}] {fnd.Message}");
}

void TestClientActivity()
{
    Section("ClientActivity (o que o celular manda)");
    var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    var c = new ClientActivity(() => t);
    Check(c.SecondsSinceLastKey is null, "sem tecla: SecondsSinceLastKey é null");

    int logged = 0;
    for (int i = 0; i < 30; i++) if (c.CountKey(ClientActivity.KeyResult.Accepted)) logged++;
    t = t.AddSeconds(7);
    c.CountKey(ClientActivity.KeyResult.Rejected);
    c.CountKey(ClientActivity.KeyResult.GameNotReady);
    t = t.AddSeconds(3);

    Check(logged == 12, "só as 12 primeiras teclas pedem log individual");
    Check(c.KeysAccepted == 30 && c.KeysRejected == 1 && c.KeysBeforeGame == 1, "contagem por resultado");
    Check(Math.Abs((c.SecondsSinceLastKey ?? -1) - 3) < 0.01, "segundos desde a última tecla");
    Check(c.CountOther("xyz") && !c.CountOther("xyz"), "tipo desconhecido avisa só na 1ª vez");
    Check(c.ToString().Contains("30 aceitas") && c.ToString().Contains("xyz×2"), "resumo legível: " + c);
}

async Task TestFileLoggerAndRecorderAsync()
{
    Section("Log em arquivo e gravação de diagnóstico");
    var dir = Path.Combine(Path.GetTempPath(), "gamestream-selftest-diag-" + Environment.ProcessId);
    try
    {
        var logPath = Path.Combine(dir, "sub", "servidor.log");
        using (var provider = new FileLoggerProvider(logPath))
        {
            Check(provider.IsActive, "cria a pasta e abre o arquivo de log");
            var logger = provider.CreateLogger("Teste");
            logger.LogInformation("olá {Nome}", "mundo");
            logger.LogError(new InvalidOperationException("boom"), "falhou");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(n => Task.Run(() =>
            {
                for (int i = 0; i < 50; i++) logger.LogWarning("linha {N}-{I}", n, i);
            })));
        }
        var lines = await File.ReadAllLinesAsync(logPath);
        Check(lines.Any(l => l.Contains("INF olá mundo")), "linha de informação com horário e nível");
        Check(lines.Any(l => l.Contains("ERRO falhou")) && lines.Any(l => l.Contains("InvalidOperationException: boom")), "erro com a exceção");
        Check(lines.Count(l => l.Contains("AVISO linha ")) == 400, "400 linhas de 8 threads sem perder nem misturar");
        Check(System.Text.RegularExpressions.Regex.IsMatch(lines[0], @"^\d\d:\d\d:\d\d\.\d{3} "), "toda linha começa com o horário");

        var broken = new FileLoggerProvider(Path.Combine(dir, new string('x', 400) + "\0", "a.log"));
        broken.CreateLogger("x").LogInformation("não pode derrubar nada");
        Check(!broken.IsActive, "pasta impossível: desliga o log em arquivo sem lançar exceção");

        // gravador
        var log = provider_Null();
        var rec = new SessionRecorder(dir, "t1", new DiagnosticsOptions { DumpH264Seconds = 60, SaveFrames = true }, log);
        rec.WriteVideo(new byte[] { 1, 1, 0, 0, 0, 1, 0x65, 0xAA });
        rec.WriteVideo(new byte[] { 1, 0, 0, 0, 1, 0x41, 0xBB });
        var bgra = new byte[8 * 8 * 4];
        var bmpPath = rec.SaveFrame("teste", bgra, 8, 8);
        rec.Dispose();
        var dumped = await File.ReadAllBytesAsync(rec.VideoPath);
        Check(dumped.SequenceEqual(new byte[] { 0, 0, 0, 1, 0x65, 0xAA, 0, 0, 1, 0x41, 0xBB }),
            "o .h264 contém só o H.264 (sem os 2 bytes de cabeçalho de cada mensagem)");
        Check(bmpPath is not null && File.Exists(bmpPath) && new FileInfo(bmpPath).Length == 54 + ((8 * 3 + 3) & ~3) * 8, "quadro .bmp salvo");

        var off = new SessionRecorder(dir, "t2", new DiagnosticsOptions { DumpH264Seconds = 0, SaveFrames = false }, log);
        off.WriteVideo(new byte[] { 1, 1, 0, 0, 1, 0x65, 1 });
        Check(!File.Exists(off.VideoPath) && off.SaveFrame("x", bgra, 8, 8) is null, "DumpH264Seconds=0 e SaveFrames=false não gravam nada");

        // limpeza
        var old = Path.Combine(dir, "velho.log");
        await File.WriteAllTextAsync(old, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
        Check(DiagnosticsCleaner.Cleanup(dir, 7) == 1 && !File.Exists(old), "apaga só o que é mais velho que KeepDays");
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { /* ignora */ }
    }

    static ILogger provider_Null() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}

async Task TestSpsAndFfmpegCheckAsync()
{
    Section("SPS do fluxo e checagem do ffmpeg");
    var ffmpeg = FindTool("ffmpeg");
    if (ffmpeg is null) { Console.WriteLine("  ffmpeg não encontrado: parte ignorada."); return; }

    var check = await FfmpegCheck.RunAsync(ffmpeg);
    Check(check.Found && check.HasX264 && check.Version.StartsWith("ffmpeg version"), $"ffmpeg ok, com libx264 ({check.Version})");
    var missing = await FfmpegCheck.RunAsync(Path.Combine(Path.GetTempPath(), "nao-existe-ffmpeg"));
    Check(!missing.Found && missing.Error is { Length: > 0 }, "ffmpeg inexistente é reportado com a causa");

    foreach (var (w, h, level) in new[] { (640, 480, 31), (854, 480, 31), (1280, 720, 31), (1920, 1080, 40) })
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-f", "lavfi", "-i", $"testsrc=size={w}x{h}:rate=30", "-frames:v", "1", "-vf", "format=yuv420p", "-c:v", "libx264",
                     "-profile:v", "baseline", "-level:v", level == 31 ? "3.1" : "4.0", "-x264-params", "aud=1:repeat-headers=1", "-f", "h264", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync();

        var sps = H264Info.ParseSps(ms.ToArray());
        Check(sps is { } s && s.Width == w && s.Height == h && s.ProfileIdc == 66 && s.LevelIdc == level,
            $"SPS de {w}x{h}: " + (sps?.ToString() ?? "ilegível"));
    }

    // O mesmo parser tem que achar o SPS dentro de uma mensagem inteira do WebSocket: [1][flags][...]
    var one = new byte[] { 1, 1, 0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x1F, 0xDA, 0x05, 0x07, 0xE8, 0x40, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80 };
    var parsed = H264Info.ParseSps(one);
    Check(parsed is { ProfileIdc: 66, LevelIdc: 31 } && parsed.Value.ToString().Contains("avc1.42C01F"),
        "acha o SPS atrás do cabeçalho da mensagem e monta o codec avc1.42C01F");
    Check(H264Info.ParseSps(new byte[] { 0, 0, 0, 1, 0x67, 0x42 }) is null, "SPS truncado devolve null em vez de quebrar");
    Check(H264Info.ParseSps(new byte[] { 0, 0, 0, 1, 0x65, 0x88 }) is null, "quadro sem SPS devolve null");
}

async Task TestFrozenGameScenarioAsync()
{
    Section("Cenário do seu log: o jogo abre e a imagem congela");
    var ffmpeg = FindTool("ffmpeg");
    if (ffmpeg is null) { Console.WriteLine("  ffmpeg não encontrado: parte ignorada."); return; }

    const int w = 640, h = 480, fps = 30;
    var ws = new FakeWebSocket();
    var sender = new WsSender(ws, 30);
    int hookCalls = 0;
    sender.OnVideoSent = _ => Interlocked.Increment(ref hookCalls);

    var source = new FreezingSource(w, h, TimeSpan.FromSeconds(2));
    var probe = new VideoProbe(w, h);
    var encoder = new FfmpegEncoder(ffmpeg, source, new GameProfile { BitrateKbps = 2000 }, fps, 0, null, probe);

    using var cts = new CancellationTokenSource();
    var videoTask = sender.RunVideoAsync(cts.Token);
    var encTask = encoder.RunAsync(sender.EnqueueVideo, cts.Token);

    await Task.Delay(TimeSpan.FromSeconds(2.2));
    var moving = sender.TakeStats();
    var movingProbe = probe.Take();
    await Task.Delay(TimeSpan.FromSeconds(4));
    var frozen = sender.TakeStats();
    var frozenProbe = probe.Take();

    cts.Cancel();
    encoder.Dispose();
    foreach (var t in new[] { encTask, videoTask })
        try { await t; } catch (OperationCanceledException) { }

    double movingKbps = moving.BytesSent * 8 / 1000 / 2.2;
    double frozenKbps = frozen.BytesSent * 8 / 1000 / 4.0;
    Console.WriteLine($"  com movimento: {movingKbps:0} kbps, mudou em {movingProbe.Changes}/{movingProbe.Frames} quadros");
    Console.WriteLine($"  congelado    : {frozenKbps:0} kbps, mudou em {frozenProbe.Changes}/{frozenProbe.Frames} quadros, igual há {frozenProbe.StillSeconds:0.0} s");

    Check(frozenKbps < 150, $"imagem congelada cai para poucos kbps ({frozenKbps:0}) - a mesma assinatura do seu log (96-196 kbps)");
    Check(frozen.FramesSent >= 4 * fps * 0.8, "mesmo congelado o vídeo continua saindo a ~30 quadros/s (o servidor NÃO parou)");
    Check(movingProbe.Changes > movingProbe.Frames / 2, "a sonda vê a imagem mudando enquanto há movimento");
    Check(frozenProbe.Changes <= 1 && frozenProbe.StillSeconds >= 3, "a sonda percebe que a imagem congelou e há quanto tempo");
    Check(hookCalls == sender.Totals.FramesSent && hookCalls > 0, $"OnVideoSent foi chamado para cada quadro enviado ({hookCalls})");
    Check(sender.Totals.KeyframesSent >= 5 && sender.Totals.BytesSent > 0, $"totais da sessão: {sender.Totals.KeyframesSent} keyframes");

    // Com o tempo que a sonda mediu (escalado para além do limiar), o diagnóstico acusa exatamente isso.
    var verdict = Diagnosis.Evaluate(new DiagInput
    {
        FramesSentTotal = sender.Totals.FramesSent, GameLaunched = true, GameShown = true, GameCpuPercent = 35,
        StillSeconds = Math.Max(frozenProbe.StillSeconds, Diagnosis.StillThresholdSeconds), BlackRatio = frozenProbe.BlackRatio,
        PrintWindowMode = true, HideWindow = true
    });
    Check(verdict.Any(f => f.Code == "IMAGEM_PARADA"), "o diagnóstico emite IMAGEM_PARADA para este cenário");
}

void TestDebugHub()
{
    Section("DebugHub (/debug)");
    Check(DebugHub.Status.Contains("Nenhuma sessão"), "sem sessão: mensagem de espera");

    string a = "sessão A";
    using (DebugHub.Begin(() => a, () => (new byte[16], 2, 2)))
    {
        Check(DebugHub.Status == "sessão A" && DebugHub.Frame is { W: 2, H: 2 }, "durante a sessão devolve o estado e o quadro");
        using var b = DebugHub.Begin(() => "sessão B", () => null);
        Check(DebugHub.Status == "sessão B", "uma sessão nova assume a página");
    }
    Check(DebugHub.Status.Contains("sessão encerrada") == false || DebugHub.Status.Contains("sessão B"), "encerrar a sessão A não apaga a B");
    Check(DebugHub.Frame is null, "sem sessão ativa não há quadro");
}

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

async Task TestAudioAsync()
{
    Section("Som: ADTS -> quadros AAC crus -> mensagem [2][0][quadro]");
    var ffmpeg = FindTool("ffmpeg");
    if (ffmpeg is null) { Console.WriteLine("  (ffmpeg não encontrado: teste do áudio pulado)"); return; }

    var (code, _) = await RunAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2:sample_rate=48000",
        "-ac", "2", "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "128k", "-f", "adts", Path.Combine(Path.GetTempPath(), "gs-audio-test.aac"));
    Check(code == 0, "ffmpeg gera AAC-LC em ADTS");
    var adts = File.ReadAllBytes(Path.Combine(Path.GetTempPath(), "gs-audio-test.aac"));

    var parser = new AdtsParser();
    var frames = new List<byte[]>();
    // entrega em pedaços de tamanho estranho: o divisor tem que juntar quadros partidos
    for (int pos = 0; pos < adts.Length;)
    {
        int n = Math.Min(37 + pos % 211, adts.Length - pos);
        parser.Feed(adts[pos..(pos + n)], n, frames.Add);
        pos += n;
    }
    Check(frames.Count >= 90 && frames.Count <= 100, $"2 s de áudio = ~94 quadros AAC (veio {frames.Count})");
    Check(frames.All(f => f.Length > 0 && f.Length < 2048), "todos os quadros têm tamanho plausível");
    Check(parser.Resyncs == 0, "sem bytes descartados (nenhum ressincronismo)");
    Check(!(frames[5].Length >= 2 && frames[5][0] == 0xFF && (frames[5][1] & 0xF0) == 0xF0), "o cabeçalho ADTS foi retirado");

    // AudioSpecificConfig usado pelo celular: AAC-LC (2), 48 kHz (índice 3), 2 canais = 0x11 0x90
    Check(((2 << 11) | (3 << 7) | (2 << 3)) == 0x1190, "AudioSpecificConfig 0x11 0x90 = AAC-LC 48 kHz estéreo");

    // Formatos de captura de dispositivo: escrever uma amostra e medir o pico tem que concordar em todos os formatos
    foreach (var f in new[] { new PcmFormat(48000, 2, 16, false), new PcmFormat(44100, 2, 24, false), new PcmFormat(48000, 2, 32, false), new PcmFormat(48000, 2, 32, true) })
    {
        var one = new byte[f.BytesPerFrame];
        f.WriteSample(one, 0, 0.5);
        int peak = f.Peak(one, one.Length);
        Check(peak > 16000 && peak < 16500, $"{f}: amostra 0,5 mede pico ~16384 (veio {peak})");
        Check(f.Peak(new byte[f.BytesPerFrame * 4], f.BytesPerFrame * 4) == 0, $"{f}: silêncio mede 0");
    }
    // WAVEFORMATEXTENSIBLE float 48 kHz estéreo (formato típico de mistura do Windows)
    var wf = System.Runtime.InteropServices.Marshal.AllocHGlobal(40);
    try
    {
        for (int i = 0; i < 40; i++) System.Runtime.InteropServices.Marshal.WriteByte(wf, i, 0);
        System.Runtime.InteropServices.Marshal.WriteInt16(wf, 0, unchecked((short)0xFFFE));
        System.Runtime.InteropServices.Marshal.WriteInt16(wf, 2, 2);
        System.Runtime.InteropServices.Marshal.WriteInt32(wf, 4, 48000);
        System.Runtime.InteropServices.Marshal.WriteInt16(wf, 14, 32);
        System.Runtime.InteropServices.Marshal.WriteInt32(wf, 24, 3);
        var parsed = PcmFormat.FromWaveFormat(wf);
        Check(parsed == new PcmFormat(48000, 2, 32, true) && parsed.FfmpegName == "f32le", "WAVEFORMATEXTENSIBLE float é lido como f32le 48000 Hz 2 canais");
    }
    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(wf); }

    var ws = new FakeWebSocket();
    var sender = new WsSender(ws);
    sender.EnqueueAudio(new byte[] { 9, 8, 7 });
    using var cts = new CancellationTokenSource(500);
    try { await sender.RunAudioAsync(cts.Token); } catch (OperationCanceledException) { }
    var msgs = ws.Sent.Where(m => m.Type == WebSocketMessageType.Binary).ToList();
    Check(msgs.Count == 1 && msgs[0].Data.SequenceEqual(new byte[] { 2, 0, 9, 8, 7 }), "mensagem de áudio = [2][0][quadro AAC]");
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

/// <summary>Quadro de uma cor só.</summary>
sealed class SolidSource : IFrameSource
{
    private readonly byte[] _frame;
    public SolidSource(int w, int h, byte value)
    {
        Width = w; Height = h;
        _frame = new byte[w * h * 4];
        Array.Fill(_frame, value);
    }
    public int Width { get; }
    public int Height { get; }
    public byte[] NextFrame() => _frame;
}

/// <summary>Muda de tamanho cada vez que <see cref="Next"/> é chamado (tela de espera -> jogo -> outro tamanho).</summary>
sealed class ResizingSource : IFrameSource
{
    private readonly (int W, int H)[] _sizes;
    private int _i;
    private SyntheticSource _cur;
    private readonly object _lock = new();

    public ResizingSource(params (int W, int H)[] sizes)
    {
        _sizes = sizes;
        _cur = new SyntheticSource(sizes[0].W, sizes[0].H);
    }

    public void Next()
    {
        lock (_lock)
        {
            if (_i + 1 >= _sizes.Length) return;
            _i++;
            _cur = new SyntheticSource(_sizes[_i].W, _sizes[_i].H);
        }
    }

    public int Width { get { lock (_lock) return _cur.Width; } }
    public int Height { get { lock (_lock) return _cur.Height; } }
    public byte[] NextFrame() { lock (_lock) return _cur.NextFrame(); }

    public byte[] NextFrame(out int width, out int height)
    {
        lock (_lock)
        {
            width = _cur.Width;
            height = _cur.Height;
            return _cur.NextFrame();
        }
    }
}

/// <summary>Conteúdo difícil de comprimir (degradê, linhas diagonais finas, quadriculado, bolas em movimento): mostra serrilhado e borrado.</summary>
sealed class RichSource
{
    private readonly int _w, _h;
    public RichSource(int w, int h) { _w = w; _h = h; }

    public byte[] FrameAt(int n)
    {
        var f = new byte[_w * _h * 4];
        int cx = 200 + (n * 9) % (_w - 400), cy = 200 + (n * 5) % (_h - 400);
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                int o = (y * _w + x) * 4;
                int r = x * 255 / _w, g = y * 255 / _h, b = 90;
                if (((x + y) % 23) < 2) { r = g = b = 255; }                     // linha diagonal fina
                if (x > 40 && x < 360 && y > 40 && y < 200 && (((x / 4) + (y / 4)) & 1) == 0) { r = g = b = 235; } // quadriculado (texto/pixel art)
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy < 90 * 90) { r = 230; g = 60; b = 40; }    // bola
                f[o] = (byte)b; f[o + 1] = (byte)g; f[o + 2] = (byte)r; f[o + 3] = 255;
            }
        return f;
    }
}

/// <summary>Entrega uma sequência fixa de quadros do <see cref="RichSource"/> e depois repete o último.</summary>
sealed class ReplaySource : IFrameSource
{
    private readonly byte[][] _frames;
    private int _i;
    public ReplaySource(RichSource src, int count) { _frames = Enumerable.Range(0, count).Select(src.FrameAt).ToArray(); }
    public int Width => 1280;
    public int Height => 720;
    public bool Done => _i >= _frames.Length;
    public byte[] NextFrame() => _frames[Math.Min(_i++, _frames.Length - 1)];
}

/// <summary>Imagem que se mexe por um tempo e depois CONGELA, como um jogo que trava logo depois de abrir.</summary>
sealed class FreezingSource : IFrameSource
{
    private readonly SyntheticSource _inner;
    private readonly TimeSpan _freezeAfter;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public FreezingSource(int width, int height, TimeSpan freezeAfter)
    {
        _inner = new SyntheticSource(width, height);
        _freezeAfter = freezeAfter;
    }

    public int Width => _inner.Width;
    public int Height => _inner.Height;

    private byte[]? _frozen;

    public byte[] NextFrame()
    {
        if (_clock.Elapsed < _freezeAfter) return _inner.NextFrame();
        return _frozen ??= (byte[])_inner.NextFrame().Clone();
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
