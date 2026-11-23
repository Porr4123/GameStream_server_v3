namespace GameStream.Server.Config;

public sealed class ServerOptions
{
    /// <summary>Porta HTTP/WebSocket.</summary>
    public int Port { get; set; } = 5055;

    /// <summary>PIN de pareamento. Vazio = gera um PIN aleatório a cada inicialização.</summary>
    public string Pin { get; set; } = "";

    /// <summary>
    /// Pasta com um subdiretório por jogo. Vazio = Documentos\jogos_servidor
    /// do usuário que está rodando o servidor (ex.: C:\Users\usuario\Documents\jogos_servidor).
    /// </summary>
    public string GamesDir { get; set; } = "";

    /// <summary>Caminho do ffmpeg.exe (relativo à pasta do projeto ou absoluto).</summary>
    public string FfmpegPath { get; set; } = "tools/ffmpeg.exe";

    /// <summary>
    /// Logo da tela de espera: caminho de um arquivo .bmp (use caminho absoluto). Vazio = logo padrão.
    /// Cada jogo pode ter o seu em &lt;pasta do jogo&gt;\dados\logo.bmp (tem prioridade).
    /// </summary>
    public string LogoPath { get; set; } = "";

    /// <summary>Núcleos (lógicos) reservados para cada jogo. Padrão 1; o resto fica para servidor e ffmpeg.</summary>
    public int GameCores { get; set; } = 1;

    /// <summary>
    /// Limite de memória (MB) para todos os processos de um jogo.
    /// 0 = DINÂMICO (padrão): a cada jogo que abre, usa a RAM livre naquele momento menos <see cref="MemoryReserveMb"/>.
    /// Um número maior que 0 = limite fixo.
    /// </summary>
    public int GameMemoryMb { get; set; } = 0;

    /// <summary>No modo dinâmico, RAM (MB) deixada livre para Windows, servidor e ffmpeg.</summary>
    public int MemoryReserveMb { get; set; } = 768;

    /// <summary>Primeiro núcleo do jogo. 0 = o jogo começa no núcleo 0; o resto fica para servidor e ffmpeg.</summary>
    public int CoreStart { get; set; } = 0;

    /// <summary>Diagnóstico: log em arquivo, página /debug, quadros e vídeo salvos para análise.</summary>
    public DiagnosticsOptions Diagnostics { get; set; } = new();

    public string ResolveGamesDir() =>
        string.IsNullOrWhiteSpace(GamesDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "jogos_servidor")
            : Environment.ExpandEnvironmentVariables(GamesDir);
}

public sealed class DiagnosticsOptions
{
    /// <summary>Liga tudo abaixo. Desligado, o servidor só escreve no console.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pasta dos arquivos de diagnóstico (relativa à pasta do projeto, ou absoluta).</summary>
    public string Dir { get; set; } = "diagnostico";

    /// <summary>Grava o log também em &lt;Dir&gt;\servidor-AAAAMMDD-HHMMSS.log (com horário). Para pedir ajuda, mande este arquivo.</summary>
    public bool FileLog { get; set; } = true;

    /// <summary>Salva alguns quadros (.bmp) do que o servidor está transmitindo: 2, 10, 30 e 90 s depois do jogo abrir.</summary>
    public bool SaveFrames { get; set; } = true;

    /// <summary>
    /// Salva os primeiros N segundos do fluxo H.264 EXATAMENTE como foi enviado ao celular (.h264).
    /// Dá para abrir no PC (ffplay -f h264 arquivo.h264, ou VLC) e ver o que o celular recebeu. 0 = desligado.
    /// </summary>
    public int DumpH264Seconds { get; set; } = 20;

    /// <summary>Página http://localhost:porta/debug (só abre no próprio PC): mostra a imagem enviada e o estado, ao vivo.</summary>
    public bool DebugPage { get; set; } = true;

    /// <summary>Apaga arquivos de diagnóstico mais velhos que isso (dias).</summary>
    public int KeepDays { get; set; } = 7;

    public string ResolveDir(string contentRoot) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(Dir), contentRoot);
}
