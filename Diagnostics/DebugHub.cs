namespace GameStream.Server.Diagnostics;

/// <summary>Ponte entre a sessão em andamento e as páginas /debug (que só abrem no próprio PC).</summary>
internal static class DebugHub
{
    private static volatile Func<string>? _status;
    private static volatile Func<(byte[] Bgra, int W, int H)?>? _frame;
    private static volatile string _lastStatus = "Nenhuma sessão ainda. Conecte o celular e atualize.";

    public static string Status => _status?.Invoke() ?? _lastStatus;

    public static (byte[] Bgra, int W, int H)? Frame => _frame?.Invoke();

    public static IDisposable Begin(Func<string> status, Func<(byte[] Bgra, int W, int H)?> frame)
    {
        _status = status;
        _frame = frame;
        return new End(status);
    }

    private sealed class End : IDisposable
    {
        private readonly Func<string> _mine;
        public End(Func<string> mine) => _mine = mine;

        public void Dispose()
        {
            if (!ReferenceEquals(_status, _mine)) return; // outra sessão já assumiu
            try { _lastStatus = "(sessão encerrada)\n\n" + _mine(); } catch { /* ignora */ }
            _status = null;
            _frame = null;
        }
    }

    public const string Page = """
<!doctype html>
<html lang="pt-br">
<head>
<meta charset="utf-8">
<title>GameStream - diagnóstico</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
  body { font: 14px/1.4 system-ui, sans-serif; background: #111; color: #ddd; margin: 16px; }
  h1 { font-size: 18px; margin: 0 0 4px; }
  p.sub { color: #999; margin: 0 0 12px; }
  .row { display: flex; gap: 16px; flex-wrap: wrap; align-items: flex-start; }
  img { max-width: 640px; width: 100%; background: #000; border: 1px solid #333; image-rendering: auto; }
  pre { background: #1b1b1b; border: 1px solid #333; padding: 10px; white-space: pre-wrap; flex: 1; min-width: 320px; margin: 0; }
  .warn { color: #ffb454; } .err { color: #ff6b6b; }
</style>
</head>
<body>
<h1>GameStream - o que o servidor está transmitindo</h1>
<p class="sub">Esta é a imagem EXATA que vai para o celular (atualiza a cada segundo). Se aqui está preta ou parada, o problema é do servidor/jogo, não do celular.</p>
<div class="row">
  <img id="f" alt="quadro">
  <pre id="s">carregando...</pre>
</div>
<script>
  const img = document.getElementById('f'), box = document.getElementById('s');
  async function tick() {
    img.src = '/debug/frame.bmp?' + Date.now();
    try {
      const t = await (await fetch('/debug/status', { cache: 'no-store' })).text();
      box.innerHTML = t.replace(/&/g,'&amp;').replace(/</g,'&lt;')
        .replace(/^(.*DIAGNÓSTICO.*(ERRO).*)$/gm,'<span class="err">$1</span>')
        .replace(/^(.*DIAGNÓSTICO.*)$/gm,'<span class="warn">$1</span>');
    } catch (e) { box.textContent = 'sem resposta do servidor'; }
  }
  tick(); setInterval(tick, 1000);
</script>
</body>
</html>
""";
}
