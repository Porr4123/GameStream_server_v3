# Tarefa para o cliente Android (GameStream_cliente_cs): mouse em camadas

O servidor (GameStream_server_v3, branch master) agora aceita **mouse isolado**: o servidor posta mensagens de mouse direto na janela do jogo, sem mexer no mouse do PC. Falta o cliente. Faça `git pull` no cliente e implemente o que está abaixo. Não mexa em nada do servidor.

## Visão geral: duas camadas de toque sobre o vídeo

Hoje a `StreamActivity` empilha, num `FrameLayout`: `AspectSurfaceView` (vídeo) → `ControlsView` (botões) → status. Passa a ser:

1. **Camada 1 (embaixo, logo acima do vídeo): `MouseLayerView`** (nova). Recebe os toques que NÃO caem em área de exclusão e os converte em mouse.
2. **Camada 2 (em cima): `ControlsView`** (a que já existe, com os botões/dpad). Os botões continuam funcionando como hoje.

Regra de exclusão: se o toque começa numa **área de exclusão**, ele pertence aos botões (camada 2) e a camada de mouse **não faz nada**. Área de exclusão =
- o retângulo de cada botão/dpad do layout atual, **mais uma folga em volta** (`buttonMargin`, ver abaixo);
- mais as **caixas de exclusão do perfil** (`mouse.exclude`).

Decida a exclusão **no ACTION_DOWN**: o dedo que começou num botão é do botão até levantar (mesmo se arrastar pra fora), e o dedo que começou fora é do mouse até levantar (mesmo se passar por cima de um botão). Multitoque: cada ponteiro é decidido separadamente (um dedo no botão e outro no mouse ao mesmo tempo deve funcionar). Dica de implementação: a `MouseLayerView` é um `View` abaixo da `ControlsView`; faça a `ControlsView.OnTouchEvent` devolver `false` no ACTION_DOWN quando o ponto não cai num botão (+ folga), para o evento cair na camada de baixo (ou use um `FrameLayout` pai que despache por ponteiro, o que for mais simples e confiável). Escolha a que funcionar com multitoque.

## Gestos (camada 1)

- **Toque** (dedo desce e sobe rápido, sem andar): **clique esquerdo** → enviar `click` esquerdo.
- **2 toques rápidos** (o 2º toque em até `doubleTapMs` do 1º, perto do primeiro): **clique direito** → enviar `click` direito. Atenção: o 1º toque NÃO deve disparar clique esquerdo imediatamente se isso fizer o duplo toque virar "esquerdo + direito". Opções: (a) esperar `doubleTapMs` antes de enviar o clique esquerdo do 1º toque (adiciona latência), ou (b) enviar o esquerdo na hora e, se vier o 2º toque, enviar só o direito. **Use (b)** (latência zero), e documente no código que no duplo toque o servidor recebe esquerdo e depois direito.
- **Toque e segura** (dedo parado por `holdMs` sem andar): **clique esquerdo contínuo** → enviar `down` esquerdo quando completar `holdMs`; se o dedo andar, enviar `move`; ao levantar, enviar `up` esquerdo. Dá para arrastar (ex.: mover item). Dê um feedback tátil curto (vibração) quando o "segurar" é reconhecido.
- Se o dedo anda mais que o "slop" de toque (`ViewConfiguration.ScaledTouchSlop`) **antes** de completar `holdMs`, não é toque nem segurar: enviar só `move` (o cursor acompanha o dedo, sem clicar). O servidor entende `move` como mover o cursor.
- Sempre que o dedo desce, mande um `move` para a posição dele antes (o jogo precisa saber onde está o cursor).

## Protocolo (cliente → servidor), mensagens de texto JSON no WebSocket principal

```json
{"t":"mouse","a":"move|down|up|click","b":"left|right","x":0.52,"y":0.31}
```

- `a`: `move` (só move o cursor), `down` (aperta e mantém), `up` (solta), `click` (aperta e solta sozinho, ~40 ms).
- `b`: `left` (padrão se faltar) ou `right`. Só esses dois são aceitos.
- `x`, `y`: **posição no quadro de vídeo transmitido**, de 0 a 1 (0,0 = canto superior esquerdo do **quadro inteiro** que o servidor manda, incluindo as faixas pretas do modo Fit). Ou seja: ponto do toque dentro da área do `AspectSurfaceView` dividido pela largura/altura dessa área. **Não** tente descontar as faixas pretas: o servidor faz isso (toque na faixa preta é ignorado).
- Mande `move` com no máximo ~60/s (o servidor limita a ~120/s). Não mande NaN/Infinity.
- Mande `up` sempre que um `down` foi enviado, inclusive em `ACTION_CANCEL`, quando o app vai para segundo plano (`OnPause`) ou a conexão cai. Nenhum botão do mouse pode ficar preso.
- Reuse o mesmo canal de envio das teclas (`StreamClient`, fila `_outbox`): adicione `SendMouse(string action, string button, double x, double y)`.

## Configuração vinda do servidor (no `hello`)

O JSON do `hello` ganhou:

```json
"mouse": {
  "enabled": true,
  "exclude": [ {"x":0.0,"y":0.7,"w":0.3,"h":0.3} ],
  "buttonMargin": 0.02,
  "holdMs": 350,
  "doubleTapMs": 300
}
```

- `enabled`: se `false` (ou se `mouse` não existir, servidor antigo), **não crie/ative a camada de mouse**; o app fica como hoje.
- `exclude`: caixas em fração da área do vídeo (x, y, w, h de 0 a 1, origem no canto superior esquerdo). O toque dentro de qualquer uma é da camada de botões.
- `buttonMargin`: folga em volta de cada botão, como fração do **lado menor da tela** (ex.: 0,02 em tela de 720 px de altura ≈ 14 px). Aplique em todos os lados.
- `holdMs`, `doubleTapMs`: tempos dos gestos. Use estes valores (com limites sensatos).

Em `StreamClient.HandleText`, no case `"hello"`, leia `mouse` (tolerante: campos ausentes → padrões acima; `enabled` ausente → `false`) e exponha por um evento novo, por exemplo `MouseConfig` (record com `Enabled`, `Exclude`, `ButtonMargin`, `HoldMs`, `DoubleTapMs`). O projeto usa trimming/AOT: use `JsonDocument`/`TryGetProperty` como o código atual, sem reflexão.

## Detalhes da interface

- Criar `Ui/MouseLayerView.cs` (`View`) e instalá-la na `StreamActivity` **entre** o `AspectSurfaceView` e a `ControlsView`, com o mesmo tamanho/posição do vídeo (as coordenadas 0..1 são relativas ao `AspectSurfaceView`; se a view do vídeo não ocupa a tela toda, a camada de mouse deve cobrir só a área do vídeo, ou converter as coordenadas para ela).
- A `MouseLayerView` precisa de uma função "este ponto está numa área de exclusão?": os retângulos dos botões vêm da `ControlsView` (adicione um método, por exemplo `bool HitsAnyButton(float x, float y, float marginPx)`, usando o layout atual, inclusive o dpad como retângulo/círculo da sua área real) + as caixas `mouse.exclude`.
- Modo de edição de botões (se existir uma tela para arrastar/editar botões): a camada de mouse deve ficar **desligada** enquanto edita.
- Um botão pequeno ou opção no menu para ligar/desligar a camada de mouse localmente (padrão: ligada quando o servidor mandar `enabled: true`), lembrando a escolha no perfil do cliente.
- Opcional, mas útil: um indicador discreto (um círculo pequeno) onde o dedo está enquanto o mouse atua; some ao levantar.
- Acessibilidade/segurança: nada de enviar mouse enquanto o vídeo/jogo não estiver visível (antes do `hello` ou quando o servidor mostra a tela de espera, tanto faz: o servidor ignora, mas não precisa mandar).

## O que NÃO fazer

- Não mande roda do mouse, botão do meio ou outros botões: o servidor recusa.
- Não tente decidir faixa preta/enquadramento no cliente.
- Não envie `click` para o duplo toque do botão esquerdo duas vezes seguidas.

## Como testar

1. Compile o APK (`dotnet build` no cliente; o código usa trimming/AOT, evite reflexão).
2. Servidor com um jogo de menu que exija clique (ex.: devast.io). No log do servidor aparecem as primeiras linhas `Mouse click left em 0.52,0.31: enviado à janela do jogo.`; se aparecer `Mouse recusado ...`, o servidor explica o motivo.
3. Verifique: toque = clique esquerdo; duplo toque rápido = esquerdo + direito; segurar = arrastar com o esquerdo apertado; dedo em cima de um botão NÃO gera mouse; dedo ao lado do botão, fora da folga, gera mouse; dois dedos (um no botão e outro no mouse) funcionam juntos.
4. O servidor ignora toques na faixa preta e fora das caixas permitidas: isso é esperado.
