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
    /// Som do jogo no celular. Captura SÓ o áudio do processo do jogo (nunca o resto do PC); precisa do Windows 10 build 19041+.
    /// </summary>
    public bool Audio { get; set; } = true;

    /// <summary>
    /// Modo de transmissão: "auto" (padrão: começa no médio e muda sozinho conforme a rede), ou fixo "leve" / "medio" / "pesado".
    /// Pesado = fps e taxa do game.json; médio = até 30 fps e ~55% da taxa; leve = até 24 fps e ~28% da taxa.
    /// </summary>
    public string QualityMode { get; set; } = "auto";

    /// <summary>Taxa do áudio AAC em kbps (32 a 320). 128 é um bom equilíbrio.</summary>
    public int AudioBitrateKbps { get; set; } = 128;

    /// <summary>
    /// false (padrão): com true e o som do celular ligado, o jogo fica MUDO nas caixas do PC do servidor (o celular continua ouvindo).
    /// Só silencia o jogo e os processos filhos dele, e volta ao normal quando a sessão acaba.
    /// Atenção: em testes reais a captura veio MUDA com isto ligado (pico ~2/32768), então o padrão é false.
    /// </summary>
    public bool MuteGameOnServer { get; set; } = false;

    /// <summary>true (padrão por enquanto): ao ligar o som, toca um bipe curto no celular. Serve para separar "o caminho do som funciona" de "o jogo não está sendo capturado". Depois de testar, ponha false.</summary>
    public bool AudioBeep { get; set; } = true;

    /// <summary>
    /// Vazio (padrão): captura só o áudio do processo do jogo. Preenchido (ex.: "S/PDIF"): captura TODO o dispositivo de saída
    /// cujo nome contém esse texto. Aponte o jogo para esse dispositivo no Windows (Som > Volume do aplicativo e preferências
    /// de dispositivo) e o PC fica em silêncio enquanto o celular ouve. Só o jogo deve tocar nesse dispositivo.
    /// </summary>
    /// <summary>Com audioCaptureDevice, troca o dispositivo de saída PADRÃO do Windows para ele enquanto o jogo roda (e devolve depois). Necessário porque muitos jogos ignoram a escolha por aplicativo e tocam no padrão.</summary>
    public bool RouteGameAudio { get; set; } = true;

    /// <summary>
    /// Com audioCaptureDevice: antes de abrir o jogo, desativa os OUTROS dispositivos de saída (exige administrador), espera 3 s e abre o jogo.
    /// Quando o jogo gera 3 s de som no dispositivo certo, os dispositivos voltam. Se não gerar som (ou o jogo fechar antes), o jogo é fechado com uma mensagem de erro.
    /// </summary>
    public bool ExclusiveAudio { get; set; } = true;

    /// <summary>Segundos esperando o jogo tocar (depois de aberto) antes de dar erro.</summary>
    public int AudioStartTimeoutSec { get; set; } = 60;

    public string AudioCaptureDevice { get; set; } = "";

    /// <summary>
    /// "printwindow" (padrão): captura só a área do jogo, mesmo com a janela escondida.
    /// "screen": copia da tela (a janela precisa estar visível e sem nada por cima; use hideWindow false).
    /// "gdigrab" continua aceito como outro nome de "screen".
    /// </summary>
    public string CaptureMode { get; set; } = "printwindow";

    // Tamanho FIXO da transmissão (o jogo é encaixado nele, sem distorcer; sobra faixa preta se a proporção for diferente).
    // 0 ou menos = 1280 x 720.
    public int StreamWidth { get; set; } = 1280;
    public int StreamHeight { get; set; } = 720;

    /// <summary>Tamanho máximo da JANELA do jogo no servidor (px). 0 = o do monitor. Janela maior é reduzida (mesma proporção): a captura (PrintWindow) custa por pixel da janela inteira, então janela menor = mais fps.</summary>
    public int MaxWindowWidth { get; set; }
    public int MaxWindowHeight { get; set; }

    /// <summary>Quadros por segundo desejados (1 a 60). Se o PC não acompanhar, o log mostra FPS_BAIXO com o motivo.</summary>
    public int Fps { get; set; } = 60;

    /// <summary>Taxa MÁXIMA de vídeo (kbps). Com crf &gt; 0 é só o teto (telas paradas gastam bem menos); com crf 0 é a taxa alvo.</summary>
    public int BitrateKbps { get; set; } = 8000;

    /// <summary>
    /// Qualidade constante do x264: menor = mais nítido e mais pesado (18 muito bom, 21 padrão, 26 leve). 0 = usa só a taxa fixa.
    /// </summary>
    public int Crf { get; set; } = 21;

    /// <summary>Velocidade do x264: ultrafast, superfast, veryfast (padrão), faster, fast, medium. Mais lento = mais qualidade e mais CPU.</summary>
    public string Preset { get; set; } = "veryfast";

    /// <summary>Perfil H.264: "main" (padrão), "high" (um pouco mais nítido) ou "baseline" (o mais compatível, usar se algum celular não decodificar).</summary>
    public string H264Profile { get; set; } = "main";

    /// <summary>
    /// Como o ffmpeg ajusta o tamanho do jogo ao da transmissão: "bicubic" (padrão, suave), "lanczos" (mais nítido),
    /// "bilinear" ou "neighbor" (pixel art: sem borrar, bordas duras).
    /// </summary>
    public string ScaleMode { get; set; } = "bicubic";

    /// <summary>Threads do x264. 0 = automático (um por núcleo livre para o ffmpeg, até 4).</summary>
    public int EncoderThreads { get; set; } = 0;

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
