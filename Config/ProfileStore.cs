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
                    var profile = LoadOrCreate(folder);
                    if (profile is not null) found.Add(profile);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[jogos] erro na pasta '{name}': {ex.Message}");
                }
            }
        }

        lock (_lock) _profiles = found;
    }

    public GameProfile? Find(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static GameProfile? LoadOrCreate(string folder)
    {
        var folderName = Path.GetFileName(folder);
        var file = Path.Combine(folder, "game.json");

        GameProfile profile;
        bool generated = false;

        if (File.Exists(file))
        {
            profile = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(file), ReadOptions) ?? new GameProfile();
        }
        else
        {
            profile = GameProfile.CreateDefault(folderName);
            generated = true;
        }

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

    private static string ToStoredPath(string folder, string exePath)
    {
        var full = Path.GetFullPath(exePath);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, full)
            : full;
    }
}
