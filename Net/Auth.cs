using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameStream.Server.Config;

namespace GameStream.Server.Net;

public enum AuthResult { Ok, Denied, Locked }

/// <summary>
/// Limite de tentativas de PIN por endereço: um PIN de 6 dígitos tem só 1 milhão de combinações e a rede local não
/// é confiável (Wi-Fi de visita, celular infectado). 5 erros em 1 minuto bloqueiam aquele endereço por 1 minuto, e cada
/// novo bloqueio dobra o tempo (até 15 min). Acertar o PIN zera a conta.
/// </summary>
public sealed class AuthThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan BaseLockout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public int Failures;
        public DateTime WindowStart;
        public DateTime LockedUntil;
        public int Level;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Func<DateTime> _now;

    public AuthThrottle(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>true se o endereço está bloqueado agora; <paramref name="retryAfter"/> diz quanto falta.</summary>
    public bool IsLocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(key, out var e)) return false;
        lock (e)
        {
            var left = e.LockedUntil - _now();
            if (left <= TimeSpan.Zero) return false;
            retryAfter = left;
            return true;
        }
    }

    /// <summary>Registra um PIN errado. Devolve true se este erro causou um bloqueio.</summary>
    public bool Fail(string key)
    {
        var e = _entries.GetOrAdd(key, _ => new Entry());
        lock (e)
        {
            var now = _now();
            if (e.Failures == 0 || now - e.WindowStart > Window)
            {
                e.Failures = 0;
                e.WindowStart = now;
            }
            e.Failures++;
            if (e.Failures < MaxFailures) return false;

            var lockout = TimeSpan.FromTicks(Math.Min(BaseLockout.Ticks << Math.Min(e.Level, 10), MaxLockout.Ticks));
            e.Level++;
            e.Failures = 0;
            e.LockedUntil = now + lockout;
            return true;
        }
    }

    public void Success(string key) => _entries.TryRemove(key, out _);
}

public static class Auth
{
    private static readonly AuthThrottle Throttle = new();

    /// <summary>
    /// PIN simples via query string (?pin=123456). Só serve para rede local:
    /// o tráfego é HTTP puro. Há limite de tentativas por endereço (<see cref="AuthThrottle"/>). TODO: TLS e token por dispositivo.
    /// </summary>
    public static AuthResult Evaluate(HttpContext ctx, ServerOptions options, ILogger? log = null)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        bool local = ip is not null && IPAddress.IsLoopback(ip); // o próprio PC não é ameaça de rede
        string key = ip?.ToString() ?? "?";

        if (!local && Throttle.IsLocked(key, out var retry))
        {
            ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
            return AuthResult.Locked;
        }

        var pin = ctx.Request.Query["pin"].ToString();
        bool ok = pin.Length > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(options.Pin));

        if (ok)
        {
            Throttle.Success(key);
            return AuthResult.Ok;
        }

        if (!local && Throttle.Fail(key))
            log?.LogWarning("Muitos PINs errados vindos de {Ip}: endereço bloqueado por um tempo (tentativa de adivinhar o PIN?).", key);
        return AuthResult.Denied;
    }

    public static bool Check(HttpContext ctx, ServerOptions options) => Evaluate(ctx, options) == AuthResult.Ok;
}
