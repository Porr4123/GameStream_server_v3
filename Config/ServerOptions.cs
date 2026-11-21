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

    /// <summary>Núcleos (lógicos) reservados para cada jogo. O i5-3470 tem 4.</summary>
    public int GameCores { get; set; } = 2;

    /// <summary>Limite de memória (MB) para todos os processos de um jogo.</summary>
    public int GameMemoryMb { get; set; } = 4096;

    /// <summary>Primeiro núcleo do jogo. 0 = núcleos 0 e 1; o resto fica para servidor e ffmpeg.</summary>
    public int CoreStart { get; set; } = 0;

    public string ResolveGamesDir() =>
        string.IsNullOrWhiteSpace(GamesDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "jogos_servidor")
            : Environment.ExpandEnvironmentVariables(GamesDir);
}
