using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameStream.Server.Config;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Recebe avisos do carregamento (vão para o log do servidor). Sem isso, vão só para o console.</summary>
    public Action<string>? Log { get; set; }

    private void Warn(string msg)
    {
        Console.WriteLine(msg);
        Log?.Invoke(msg);
    }

    private readonly string _dir;
    private readonly object _lock = new();
    private IReadOnlyList<GameProfile> _profiles = Array.Empty<GameProfile>();

    public ProfileStore(string dir) => _dir = dir;

    public IReadOnlyList<GameProfile> All
    {
        get { lock (_lock) return _profiles; }
    }

    /// <summary>Relê as pastas. Pode ser chamado a qualquer momento (jogos novos aparecem sem reiniciar).</summary>
    public void Load()
    {
        var found = new List<GameProfile>();

        if (Directory.Exists(_dir))
        {
            foreach (var folder in Directory.GetDirectories(_dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(folder);
                if (name.StartsWith('_') || name.StartsWith('.')) continue;

                try
                {
                    var profile = LoadOrCreate(folder, Warn);
                    if (profile is not null) found.Add(profile);
                }
                catch (JsonException ex)
                {
                    Warn($"[jogos] '{name}': o game.json está inválido (linha {(ex.LineNumber ?? 0) + 1}, coluna {(ex.BytePositionInLine ?? 0) + 1}): {ex.Message} " +
                         "Dica: em caminhos do Windows use barra dupla (C:\\Program Files\\...) ou barra normal (C:/Program Files/...). O jogo fica FORA da lista até corrigir.");
                }
                catch (Exception ex)
                {
                    Warn($"[jogos] erro na pasta '{name}': {ex.Message}");
                }
            }
        }

        lock (_lock) _profiles = found;
    }

    public GameProfile? Find(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    internal static GameProfile? LoadOrCreate(string folder, Action<string>? warn = null)
    {
        var folderName = Path.GetFileName(folder);
        var file = Path.Combine(folder, "game.json");

        GameProfile profile;
        bool generated = false;

        if (File.Exists(file))
        {
            profile = ParseProfile(File.ReadAllText(file), warn, folderName);
        }
        else
        {
            profile = GameProfile.CreateDefault(folderName);
            generated = true;
        }

        // game.json enxuto (só os campos que o dono quis mudar): o que faltar usa os padrões.
        var defaults = GameProfile.CreateDefault(folderName);
        if (profile.AllowedKeys.Count == 0) profile.AllowedKeys = defaults.AllowedKeys;
        if (profile.Layout.Count == 0) profile.Layout = defaults.Layout;

        profile.Id = folderName.ToLowerInvariant();
        profile.Folder = folder;
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = folderName;

        if (string.IsNullOrWhiteSpace(profile.Exe))
        {
            var found = ExeDiscovery.Find(folder);
            if (found is null)
            {
                Console.WriteLine($"[jogos] '{folderName}': ainda sem .exe ou atalho do jogo.");
                return null;
            }

            profile.Exe = ToStoredPath(folder, found.ExePath);
            profile.Args ??= found.Args;
            profile.WorkingDir ??= found.WorkingDir;
            Console.WriteLine($"[jogos] '{folderName}': {found.Source} -> {profile.Exe}");
        }

        // Só grava game.json quando ele não existia: nunca sobrescreve edições do dono.
        if (generated) File.WriteAllText(file, JsonSerializer.Serialize(profile, WriteOptions));

        return profile;
    }

    /// <summary>Lê o JSON; se falhar por causa de barras simples em caminhos do Windows (C:\Program Files), conserta e tenta de novo.</summary>
    internal static GameProfile ParseProfile(string json, Action<string>? warn = null, string folderName = "")
    {
        try
        {
            return JsonSerializer.Deserialize<GameProfile>(json, ReadOptions) ?? new GameProfile();
        }
        catch (JsonException)
        {
            // \ seguido de qualquer coisa que não seja escape válido de JSON vira \\ (ex.: \P de "\Program Files").
            string fixedJson = System.Text.RegularExpressions.Regex.Replace(json, @"\\(?![""\\/bfnrtu])", @"\\");
            if (fixedJson == json) throw;
            var profile = JsonSerializer.Deserialize<GameProfile>(fixedJson, ReadOptions) ?? new GameProfile();
            warn?.Invoke($"[jogos] '{folderName}': o game.json tinha barras simples em caminhos; li assim mesmo. Prefira C:\\\\Pasta\\\\arquivo ou C:/Pasta/arquivo.");
            return profile;
        }
    }

    private static string ToStoredPath(string folder, string exePath)
    {
        var full = Path.GetFullPath(exePath);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, full)
            : full;
    }
}
