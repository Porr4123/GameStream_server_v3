using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameStream.Server.Streaming;

namespace GameStream.Server.Net;

/// <summary>
/// Um WebSocket só aceita um envio por vez. Este envelope serializa vídeo, "hello" e "pong",
/// que saem de tarefas diferentes.
/// </summary>
internal sealed class WsSender
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WsSender(WebSocket ws) => _ws = ws;

    public Task SendJsonAsync(object payload, CancellationToken ct) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, ct);

    public Task SendTextAsync(string text, CancellationToken ct) =>
        SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct);

    /// <summary>Mensagem binária: [1][flags (bit0 = keyframe)][quadro H.264 Annex B].</summary>
    public Task SendVideoAsync(byte[] accessUnit, CancellationToken ct)
    {
        var message = new byte[accessUnit.Length + 2];
        message[0] = 1;
        message[1] = (byte)(AnnexBSplitter.IsKeyFrame(accessUnit) ? 1 : 0);
        Buffer.BlockCopy(accessUnit, 0, message, 2, accessUnit.Length);
        return SendAsync(message, WebSocketMessageType.Binary, ct);
    }

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(new ReadOnlyMemory<byte>(data), type, true, ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
