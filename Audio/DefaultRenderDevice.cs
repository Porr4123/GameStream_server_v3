using System.Runtime.InteropServices;

namespace GameStream.Server.Audio;

/// <summary>
/// Troca o dispositivo de saída PADRÃO do Windows (ex.: para o S/PDIF) enquanto o jogo roda, e devolve o anterior depois.
/// Muitos jogos ignoram a escolha de dispositivo por aplicativo e sempre tocam no padrão: trocar o padrão ANTES de abrir
/// o jogo é o que realmente os leva ao dispositivo certo. Usa a interface IPolicyConfig (não documentada, mas estável desde o Vista).
/// </summary>
internal sealed class DefaultRenderDevice : IDisposable
{
    private static readonly Guid ClsidPolicyConfig = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private string? _previousId;
    private bool _changed;

    public string DeviceName { get; private set; } = "";
    public string PreviousName { get; private set; } = "";

    /// <summary>Faz o dispositivo cujo nome contém <paramref name="nameFragment"/> virar o padrão (console, multimídia e comunicações).</summary>
    public static DefaultRenderDevice Apply(string nameFragment)
    {
        var devices = Wasapi.ListRenderDevices();
        var chosen = devices.FirstOrDefault(d => d.IsActive && d.Name.Contains(nameFragment.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"nenhum dispositivo de saída ATIVO tem \"{nameFragment}\" no nome. Dispositivos: " +
                string.Join("; ", devices.Select(d => $"\"{d.Name}\" [{d.StateText}]")));

        var result = new DefaultRenderDevice { DeviceName = chosen.Name };
        var enumerator = (Wasapi.IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidEnumerator)!)!;
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out var current) >= 0 && current.GetId(out string curId) >= 0)
        {
            result._previousId = curId;
            result.PreviousName = devices.FirstOrDefault(d => d.Id == curId)?.Name ?? curId;
        }

        if (result._previousId == chosen.Id) return result; // já é o padrão: nada a restaurar

        var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidPolicyConfig)!)!;
        for (int role = 0; role < 3; role++)
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(chosen.Id, role));
        result._changed = true;
        return result;
    }

    /// <summary>Mostra (true) ou esconde/desativa (false) um dispositivo de saída para TODO o Windows. Normalmente exige administrador.</summary>
    public static int SetVisibility(string deviceId, bool visible)
    {
        var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidPolicyConfig)!)!;
        return policy.SetEndpointVisibility(deviceId, visible ? 1 : 0);
    }

    /// <summary>Devolve o dispositivo padrão que era usado antes.</summary>
    public void Dispose()
    {
        if (!_changed || _previousId is null) return;
        _changed = false;
        try
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidPolicyConfig)!)!;
            for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(_previousId, role);
        }
        catch (Exception)
        {
            // melhor esforço
        }
    }

    // Ordem dos métodos da vtable da IPolicyConfig (Windows Vista em diante). Só SetDefaultEndpoint é usado.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int def, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
