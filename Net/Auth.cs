using System.Security.Cryptography;
using System.Text;
using GameStream.Server.Config;

namespace GameStream.Server.Net;

public static class Auth
{
    /// <summary>
    /// PIN simples via query string (?pin=123456). Só serve para rede local:
    /// o tráfego é HTTP puro. TODO: TLS, limite de tentativas e token por dispositivo.
    /// </summary>
    public static bool Check(HttpContext ctx, ServerOptions options)
    {
        var pin = ctx.Request.Query["pin"].ToString();
        if (pin.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(options.Pin));
    }
}
