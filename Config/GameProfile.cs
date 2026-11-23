using System.Text.Json.Serialization;

namespace GameStream.Server.Config;

/// <summary>Perfil de um jogo, lido de &lt;jogos_servidor&gt;/&lt;pasta&gt;/game.json (gerado automaticamente se faltar).</summary>
public sealed class GameProfile
{
    /// <summary>Definido pelo nome da pasta (minúsculo).</summary>
    [JsonIgnore] public string Id { get; set; } = "";
    [JsonIgnore] public string Folder { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Executável: caminho relativo à pasta do jogo ou absoluto (atalhos apontando para outra pasta).
    /// Vazio = o servidor procura um atalho (.lnk) ou .exe dentro da pasta.
    /// </summary>
    public string Exe { get; set; } = "";
    public string? Args { get; set; }

    /// <summary>Pasta de trabalho do jogo. Vazio = a pasta do .exe.</summary>
    public string? WorkingDir { get; set; }

    /// <summary>
    /// Trecho do título da janela. Use quando o .exe é só um launcher que abre outro processo.
    /// Vazio = usa a janela principal do processo iniciado.
    /// </summary>
    public string? WindowTitle { get; set; }

    // Limites por jogo (vazio = valores padrão do appsettings.json).
    public int? Cores { get; set; }
    public int? MemoryMb { get; set; }

    /// <summary>
    /// Redireciona APPDATA/LOCALAPPDATA do jogo para &lt;pasta do jogo&gt;\dados\appdata.
    /// Assim os saves de jogos que gravam em AppData (ex.: Godot) ficam na pasta do jogo.
    /// Não funciona com jogos que ignoram a variável de ambiente.
    /// </summary>
    public bool RedirectAppData { get; set; } = true;

    /// <summary>
    /// true = esconde a janela do jogo (fora da tela, fora do Alt+Tab e da barra de tarefas).
    /// Só funciona com captureMode "printwindow".
    /// </summary>
    public bool HideWindow { get; set; } = true;

    /// <summary>
    /// Como esconder a janela (vale com hideWindow true):
    /// "transparent" (padrão): a janela fica DENTRO do monitor, quase 100% transparente, sem receber clique e sempre abaixo
    ///   de todas as outras janelas. O Windows só desenha o que está dentro da área de trabalho, então isso mantém o jogo
    ///   renderizando.
    /// "offscreen": fora de todos os monitores (alguns jogos param de desenhar assim).
    /// </summary>
    public string HideMode { get; set; } = "transparent";

    /// <summary>
    /// "printwindow" (padrão): captura só a área do jogo, mesmo com a janela escondida.
    /// "screen": copia da tela (a janela precisa estar visível e sem nada por cima; use hideWindow false).
    /// "gdigrab" continua aceito como outro nome de "screen".
    /// </summary>
    public string CaptureMode { get; set; } = "printwindow";

    // Tamanho FIXO da transmissão (o jogo é encaixado nele, sem distorcer). 0 ou menos = 640 x 480.
    public int StreamWidth { get; set; } = 640;
    public int StreamHeight { get; set; } = 480;
    public int Fps { get; set; } = 30;
    public int BitrateKbps { get; set; } = 2000;

    /// <summary>
    /// Teclas usadas pelo layout padrão deste jogo (informativo, enviado ao cliente). O servidor NÃO usa esta lista para
    /// decidir o que injetar: aceita sempre e somente letras, números, setas, CTRL, SHIFT e ESC (veja KeyMap).
    /// </summary>
    public List<string> AllowedKeys { get; set; } = new();

    /// <summary>Layout padrão dos botões; o cliente pode personalizar e salvar o seu.</summary>
    public List<ButtonLayout> Layout { get; set; } = new();

    public static GameProfile CreateDefault(string name) => new()
    {
        Name = name,
        AllowedKeys = new List<string>
        {
            "W", "A", "S", "D", "UP", "DOWN", "LEFT", "RIGHT",
            "Z", "X", "C", "V", "B", "N", "ESC", "SHIFT"
        },
        Layout = new List<ButtonLayout>
        {
            // No dpad a ordem é: cima, esquerda, baixo, direita.
            // Se o jogo só lê as setas, troque por UP, LEFT, DOWN, RIGHT.
            new() { Type = "dpad", Keys = new List<string> { "W", "A", "S", "D" }, X = 0.14, Y = 0.68, Size = 0.30 },
            new() { Type = "button", Key = "Z", Label = "Z", X = 0.90, Y = 0.28 },
            new() { Type = "button", Key = "X", Label = "X", X = 0.90, Y = 0.44 },
            new() { Type = "button", Key = "C", Label = "C", X = 0.90, Y = 0.60 },
            new() { Type = "button", Key = "V", Label = "V", X = 0.90, Y = 0.76 },
            new() { Type = "button", Key = "ESC", Label = "Esc", X = 0.45, Y = 0.04, Size = 0.09 }
        }
    };
}

public sealed class ButtonLayout
{
    /// <summary>"button" (uma tecla) ou "dpad" (4 teclas: cima, esquerda, baixo, direita).</summary>
    public string Type { get; set; } = "button";
    public string? Key { get; set; }
    public List<string>? Keys { get; set; }
    public string? Label { get; set; }

    // Posição e tamanho como fração da tela (0 a 1).
    public double X { get; set; }
    public double Y { get; set; }
    public double Size { get; set; } = 0.12;
}
