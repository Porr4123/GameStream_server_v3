namespace GameStream.Server.Diagnostics;

/// <summary>
/// O que o celular mandou ao servidor. O cliente só envia teclas; o ping dele é um frame de controle do
/// WebSocket, que o servidor responde sozinho e não enxerga. Por isso "nenhuma tecla" não quer dizer que o
/// app travou: serve para saber se as teclas CHEGAM e o que foi feito com elas.
/// </summary>
internal sealed class ClientActivity
{
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, int> _otherTypes = new();
    private DateTime? _lastKey;
    private DateTime? _lastAny;
    private int _firstKeysLogged;

    public ClientActivity(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public long KeysAccepted { get; private set; }
    public long KeysRejected { get; private set; }
    public long KeysBeforeGame { get; private set; }
    public long KeysNoFocus { get; private set; }
    public long OtherMessages { get; private set; }
    public long BadMessages { get; private set; }
    public long TextPings { get; private set; }

    public enum KeyResult { Accepted, Rejected, GameNotReady, NoFocus }

    /// <returns>true nas primeiras teclas: vale a pena escrever cada uma no log.</returns>
    public bool CountKey(KeyResult result)
    {
        lock (_lock)
        {
            switch (result)
            {
                case KeyResult.Accepted: KeysAccepted++; break;
                case KeyResult.Rejected: KeysRejected++; break;
                case KeyResult.NoFocus: KeysNoFocus++; break;
                default: KeysBeforeGame++; break;
            }
            _lastKey = _lastAny = _now();
            return _firstKeysLogged++ < 12;
        }
    }

    public void CountPing() { lock (_lock) { TextPings++; _lastAny = _now(); } }
    public void CountBad() { lock (_lock) { BadMessages++; _lastAny = _now(); } }

    /// <returns>true na PRIMEIRA vez que aparece este tipo de mensagem (para avisar uma vez só).</returns>
    public bool CountOther(string type)
    {
        lock (_lock)
        {
            OtherMessages++;
            _lastAny = _now();
            _otherTypes.TryGetValue(type, out int n);
            _otherTypes[type] = n + 1;
            return n == 0;
        }
    }

    /// <summary>Segundos desde a última tecla; null se nunca chegou nenhuma.</summary>
    public double? SecondsSinceLastKey
    {
        get { lock (_lock) return _lastKey is null ? null : (_now() - _lastKey.Value).TotalSeconds; }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            string last = _lastKey is null ? "nenhuma tecla ainda" : $"última há {(_now() - _lastKey.Value).TotalSeconds:0} s";
            string other = _otherTypes.Count == 0 ? "" : ", outras: " + string.Join(" ", _otherTypes.Select(kv => $"{kv.Key}×{kv.Value}"));
            return $"teclas {KeysAccepted} aceitas / {KeysRejected} recusadas / {KeysNoFocus} descartadas sem foco / {KeysBeforeGame} antes do jogo abrir ({last}){other}";
        }
    }
}
