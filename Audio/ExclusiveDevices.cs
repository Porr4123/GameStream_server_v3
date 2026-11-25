using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GameStream.Server.Audio;

/// <summary>
/// Esconde (desativa) todos os dispositivos de saída ativos, menos o escolhido, para o jogo não ter onde tocar além dele.
/// Os dispositivos escondidos são gravados em disco: se o servidor fechar de repente, <see cref="RecoverPending"/> os devolve na próxima partida.
/// </summary>
internal sealed class ExclusiveDevices : IDisposable
{
    private static string StatePath => Path.Combine(AppContext.BaseDirectory, "dispositivos-escondidos.json");

    private readonly List<AudioDeviceInfo> _hidden = new();
    private bool _restored;

    public IReadOnlyList<string> HiddenNames => _hidden.Select(d => d.Name).ToList();

    public static ExclusiveDevices Hide(string keepNameFragment)
    {
        var devices = Wasapi.ListRenderDevices();
        var keep = devices.FirstOrDefault(d => d.IsActive && d.Name.Contains(keepNameFragment.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"o dispositivo \"{keepNameFragment}\" não está ativo.");

        var guard = new ExclusiveDevices();
        var toHide = devices.Where(d => d.IsActive && d.Id != keep.Id).ToList();
        // Grava ANTES de esconder: se algo travar no meio, o próximo início do servidor sabe o que devolver.
        Save(toHide);
        foreach (var d in toHide)
        {
            int hr = DefaultRenderDevice.SetVisibility(d.Id, false);
            if (hr < 0)
            {
                guard.Restore();
                Save(Array.Empty<AudioDeviceInfo>());
                throw new InvalidOperationException(
                    $"não consegui desativar \"{d.Name}\" (erro 0x{hr:X8}). Execute o servidor como administrador.");
            }
            guard._hidden.Add(d);
        }
        return guard;
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        foreach (var d in _hidden) DefaultRenderDevice.SetVisibility(d.Id, true);
        _hidden.Clear();
        Save(Array.Empty<AudioDeviceInfo>());
    }

    public void Dispose() => Restore();

    /// <summary>Devolve dispositivos que um servidor anterior escondeu e não chegou a restaurar.</summary>
    public static void RecoverPending(ILogger log)
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var list = JsonSerializer.Deserialize<List<AudioDeviceInfo>>(File.ReadAllText(StatePath)) ?? new();
            foreach (var d in list)
            {
                int hr = DefaultRenderDevice.SetVisibility(d.Id, true);
                log.LogWarning("Som: devolvendo o dispositivo \"{Name}\", que ficou escondido por uma sessão anterior (resultado 0x{Hr:X8}).", d.Name, hr);
            }
            Save(Array.Empty<AudioDeviceInfo>());
        }
        catch (Exception ex)
        {
            log.LogWarning("Som: não consegui devolver dispositivos escondidos: {Msg}", ex.Message);
        }
    }

    private static void Save(IEnumerable<AudioDeviceInfo> list)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(list.ToList())); }
        catch (Exception) { /* sem permissão de escrita: segue sem a rede de segurança */ }
    }
}
