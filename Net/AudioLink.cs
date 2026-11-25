using System.Collections.Concurrent;

namespace GameStream.Server.Net;

/// <summary>
/// Liga o 2º soquete (/audio) à sessão certa. Cada sessão com som cria um token aleatório, entregue ao cliente no "hello";
/// só quem tem o token (e o PIN) consegue anexar um soquete de som àquela sessão.
/// </summary>
internal static class AudioLink
{
    private static readonly ConcurrentDictionary<string, WsSender> Sessions = new();

    public static string Register(WsSender sender)
    {
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        Sessions[token] = sender;
        return token;
    }

    public static void Remove(string token) => Sessions.TryRemove(token, out _);

    public static bool TryGet(string? token, out WsSender sender)
    {
        sender = null!;
        return !string.IsNullOrEmpty(token) && Sessions.TryGetValue(token, out sender!);
    }
}
