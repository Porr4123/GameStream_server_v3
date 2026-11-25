namespace GameStream.Server.Audio;

internal enum GateState { Waiting, Confirmed, TimedOut }

/// <summary>
/// Decide se o jogo está mesmo tocando no dispositivo certo: confirma depois de <c>confirmMs</c> de som de verdade
/// (acumulado) e desiste se passar <c>timeoutMs</c> sem confirmar.
/// </summary>
internal sealed class GameAudioGate
{
    private readonly int _confirmMs, _timeoutMs;
    private long _lastLoud, _lastMs;
    private long _loudMs;
    private bool _started;

    public GameAudioGate(int confirmMs, int timeoutMs)
    {
        _confirmMs = confirmMs;
        _timeoutMs = timeoutMs;
    }

    public long LoudMs => _loudMs;

    /// <param name="loudPackets">Contador crescente de pacotes com som (da captura).</param>
    /// <param name="nowMs">Milissegundos desde que o jogo abriu.</param>
    public GateState Update(long loudPackets, long nowMs)
    {
        if (_started && loudPackets > _lastLoud) _loudMs += nowMs - _lastMs;
        _started = true;
        _lastLoud = loudPackets;
        _lastMs = nowMs;

        if (_loudMs >= _confirmMs) return GateState.Confirmed;
        return nowMs >= _timeoutMs ? GateState.TimedOut : GateState.Waiting;
    }
}
