using System.Text;

namespace GameStream.Server.Config;

/// <summary>Cria a pasta raiz dos jogos e as pastas esqueleto onde o dono coloca cada jogo.</summary>
public static class GameFolderSkeleton
{
    public static void EnsureRoot(string root)
    {
        Directory.CreateDirectory(root);

        var readme = Path.Combine(root, "LEIA-ME.txt");
        if (!File.Exists(readme)) File.WriteAllText(readme, RootReadme, Encoding.UTF8);

        bool hasGames = Directory.GetDirectories(root).Any(d =>
        {
            var name = Path.GetFileName(d);
            return !name.StartsWith('_') && !name.StartsWith('.');
        });

        if (!hasGames) Create(root, "meu-primeiro-jogo");
    }

    /// <returns>Caminho da pasta criada (ou já existente).</returns>
    public static string Create(string root, string name)
    {
        var safe = Sanitize(name);
        var folder = Path.Combine(root, safe);

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "dados"));

        var readme = Path.Combine(folder, "LEIA-ME.txt");
        if (!File.Exists(readme)) File.WriteAllText(readme, GameReadme, Encoding.UTF8);

        return folder;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('.', '_', ' ');
        return clean.Length == 0 ? "novo-jogo" : clean;
    }

    private const string RootReadme =
@"PASTA DOS JOGOS DO GAMESTREAM.SERVER

Cada jogo fica na sua própria pasta aqui dentro.
Para criar uma pasta esqueleto nova:   dotnet run -- novo ""Nome do Jogo""
Pastas que começam com _ ou . são ignoradas pelo servidor.
";

    private const string GameReadme =
@"COMO COLOCAR O JOGO AQUI

1. Copie os arquivos do jogo para esta pasta (pode ser dentro de subpastas),
   OU coloque aqui um atalho (.lnk) para o .exe do jogo.
2. O servidor procura sozinho o que executar: primeiro atalhos, depois .exe
   (prefere Game.exe do RPG Maker; ignora instaladores e desinstaladores).
3. Na primeira vez ele gera o arquivo game.json com os ajustes do jogo
   (executavel, resolucao, teclas permitidas, layout dos botoes, limites de CPU/RAM).
   Edite esse arquivo se quiser mudar algo. Se o servidor escolher o .exe errado,
   corrija o campo ""exe"".

Pasta ""dados"": informacoes do servidor sobre este jogo
(inclui dados\appdata, para onde vao os saves de jogos que gravam em AppData, como os de Godot).
Os saves de jogos RPG Maker ficam na propria pasta do jogo.
";
}
