# GameStream.Server

Servidor (Windows, C#/.NET 10) que roda um jogo, transmite a janela em H.264 e recebe comandos de teclado
por WebSocket. Pensado para jogos 2D feitos para teclado (RPG Maker, Godot).

> Este esqueleto foi escrito sem compilar (o ambiente onde foi gerado não tem o .NET SDK).
> Se der erro de compilação, copie a mensagem e eu corrijo.

## Requisitos

- Windows 10/11 e [.NET 10 SDK](https://dotnet.microsoft.com/download). No Visual Studio 2022, instale a carga de trabalho "ASP.NET e desenvolvimento para a Web".
- `ffmpeg.exe` em `tools/` (build "essentials" do ffmpeg para Windows, com libx264 e gdigrab).
  Se não estiver lá, o servidor tenta o `ffmpeg` do PATH.

## Rodar

```
cd GameStream.Server
dotnet run
```

O console mostra o PIN de pareamento, a pasta dos jogos e os limites por jogo.
Para fixar o PIN, preencha `Server:Pin` em `appsettings.json`.

## Pasta dos jogos

Por padrão: `Documentos\jogos_servidor` (ex.: `C:\Users\usuario\Documents\jogos_servidor`).
Para mudar, preencha `Server:GamesDir` em `appsettings.json`.

O servidor cria a pasta e uma pasta esqueleto (`meu-primeiro-jogo`) na primeira execução.
Para criar outras:

```
dotnet run -- novo "Nome do Jogo"
```

Estrutura de cada jogo:

```
jogos_servidor\
  Nome do Jogo\
    (arquivos do jogo, em qualquer subpasta)  OU  um atalho .lnk para o .exe
    game.json        <- gerado sozinho na primeira vez; edite se quiser
    dados\           <- informações do servidor sobre este jogo
      appdata\       <- saves de jogos que gravam em AppData (veja abaixo)
    LEIA-ME.txt
```

Pastas que começam com `_` ou `.` são ignoradas.

### Como ele escolhe o que executar

1. Se `game.json` já tem `exe`, usa esse.
2. Senão procura um **atalho (.lnk)** que aponte para um .exe (até 2 níveis de pasta). O destino pode estar em outra pasta.
3. Senão procura um **.exe** (até 3 níveis), preferindo `Game.exe` (RPG Maker), depois o mais raso e o maior.
   Ignora desinstaladores, instaladores, redistribuíveis, updaters e crash handlers.

A escolha vai para o `game.json` (campo `exe`). Se ele errar, corrija esse campo.
Jogos novos aparecem sem reiniciar o servidor (as pastas são relidas a cada `/games` e a cada conexão).

## Limites por jogo

Padrão em `appsettings.json`: `GameCores: 2`, `GameMemoryMb: 4096`, `CoreStart: 0`.
No i5-3470 (4 núcleos, sem hyper-threading): o jogo fica nos núcleos 0 e 1 e o ffmpeg nos núcleos 2 e 3.
Cada jogo pode ter valores próprios em `game.json` (`cores`, `memoryMb`).

- A memória é o total confirmado (commit) de todos os processos do jogo; passou disso, o jogo recebe erro de alocação.
- O servidor avisa no console se o limite de RAM for grande demais para a máquina.

## Saves

- **RPG Maker**: grava na própria pasta do jogo.
- **Godot** e outros jogos que usam AppData: com `redirectAppData: true` (padrão) o servidor aponta `APPDATA` e
  `LOCALAPPDATA` do jogo para `dados\appdata`, então os saves ficam dentro da pasta do jogo.
  Jogos que ignoram essas variáveis ainda gravam no AppData do Windows.
- Todos os jogadores de um mesmo jogo compartilham os mesmos saves (por enquanto é um jogador por vez).

## Vídeo sempre ligado (tela de espera)

Ao conectar, o servidor manda o `hello` na hora e começa a enviar vídeo **imediatamente**, mesmo antes de o jogo abrir:

- enquanto o jogo carrega: fundo preto + logo + "Iniciando o jogo...";
- se o jogo falhar ou fechar: a mesma tela com a mensagem ("Não encontrei o executável do jogo.", "Jogo encerrado." etc.);
- o fim do jogo **não** derruba a conexão: o cliente continua recebendo a tela de espera até desconectar.

A transmissão tem **tamanho fixo** (`streamWidth` x `streamHeight`, padrão 640 x 480). O jogo é encaixado nesse quadro
mantendo a proporção (faixas pretas se for preciso), então o tamanho nunca muda no meio da sessão.

**Logo:** por padrão é um círculo azul com um "play" e o texto GameStream. Para usar o seu, salve um arquivo **.bmp** em
`<pasta do jogo>\dados\logo.bmp` (só daquele jogo) ou aponte `LogoPath` no `appsettings.json` (todos os jogos).

**Ping/pong:** o servidor recebe mensagens desde o primeiro instante, responde `{"t":"ping",...}` com `{"t":"pong",...}`
(devolvendo os mesmos campos), responde `ping` em texto puro com `pong` e manda pings do próprio WebSocket a cada 10 s.

## Janela oculta e captura

Por padrão (`hideWindow: true`, `captureMode: "printwindow"` no `game.json`):

- a janela do jogo vai para uma posição fora de todos os monitores e some do Alt+Tab e da barra de tarefas;
- o servidor captura só a **área do jogo** (sem borda nem barra de título) com `PrintWindow`.

A janela pode piscar por um instante quando o jogo abre, antes de ser escondida.

Se a imagem do jogo ficar **preta**, o jogo não é capturável por `PrintWindow`. Use no `game.json`:

```
"captureMode": "screen",
"hideWindow": false
```

(`"gdigrab"` continua aceito como outro nome de `"screen"`.) Nesse modo a janela precisa ficar visível e sem nada por cima.

Limites:

- O jogo continua desenhando os próprios quadros (isso é inevitável); o que some é a janela na tela.
- `SendInput` leva as teclas para a janela em primeiro plano, então **enquanto há uma sessão, o teclado do PC fica com o jogo**. Para usar o PC ao mesmo tempo, o jogo precisa rodar em outra máquina, numa VM ou numa sessão do Windows separada.
- Se o jogo minimizar a si mesmo, a captura falha. Jogos que pausam quando a janela está oculta também podem parar.
- Se a janela do jogo mudar de tamanho durante a sessão, a captura continua com o tamanho inicial.

## Diagnóstico (log do servidor)

A cada 5 s a sessão escreve uma linha como:

```
Vídeo de rpg: 30 quadros/s, 1480 kbps, descartados 0 (0 estouros da fila), envio mais lento 4 ms, fila 0
```

- **quadros/s** perto do `fps` do jogo = o vídeo está saindo do servidor. Se aparecer 0 (linha em aviso), o problema está no PC (ffmpeg/captura), não no celular.
- **descartados / estouros** > 0 = o celular ou o Wi-Fi não acompanharam; o servidor pulou até o próximo keyframe.
- **envio mais lento** alto (centenas de ms) = rede congestionada.
- `A captura do quadro levou N ms` = a janela do jogo está travada, carregando ou pesada demais para o `PrintWindow`.
- Se a janela do jogo para de responder, o servidor repete a última imagem (o vídeo não para) e volta ao normal quando o jogo destravar.
- Se o jogo trava, sair do app no celular encerra a sessão mesmo assim: o servidor mata o jogo primeiro e libera o jogador seguinte.

## Autoteste

Confere o caminho do vídeo inteiro **sem celular e sem jogo**: ffmpeg real com quadros sintéticos, divisão em quadros,
fila de envio (inclusive um cliente lento simulado) e decodificação independente do resultado. Precisa do `ffmpeg` em `tools\` ou no PATH.

```
dotnet run --project tests/GameStream.Server.SelfTest
```

Rode depois de mexer em `Streaming/` ou `Net/WsSender.cs`. Código de saída 0 = tudo certo.

## Testar sem o APK

1. Abra `http://localhost:5055/test.html` no Chrome/Edge, no mesmo PC.
2. Digite o PIN e o id do jogo (nome da pasta em minúsculas), clique em Conectar.
3. As teclas do navegador viram comandos, desde que estejam em `allowedKeys`.

Para o celular na rede local, o WebCodecs não funciona em HTTP fora de localhost; é aí que entra o APK.
Libere a porta 5055 no firewall do Windows quando for testar pela rede.

## Protocolo

`ws://<ip>:5055/stream?game=<id>&pin=<pin>`

| Direção | Formato |
|---|---|
| servidor -> cliente | texto `{"t":"hello","game","w","h","fps"}` assim que conecta |
| servidor -> cliente | binário: `[1][flags][quadro H.264 Annex B]` (flags bit0 = keyframe), **sempre** |
| servidor -> cliente | texto `{"t":"pong",...}` em resposta a `{"t":"ping",...}` |
| cliente -> servidor | texto `{"t":"key","k":"Z","d":1}` (d: 1 apertou, 0 soltou) |
| cliente -> servidor | texto `{"t":"ping",...}` (ou `ping` puro) |

O cliente deve descartar os 2 primeiros bytes da mensagem binária e entregar o resto ao decodificador.

Garantias do fluxo de vídeo (conferidas pelo autoteste):

- baseline, **1 slice por quadro**, sem B-frames, sem SEI e **sem AUD** (o AUD só é usado dentro do servidor para separar os quadros);
- **keyframe a cada 1 s**; todo keyframe leva SPS + PPS na frente do IDR, então o cliente pode entrar (ou voltar) em qualquer keyframe;
- nível H.264 calculado pela resolução e pelo fps (640x480 @ 30 = 3.1);
- o primeiro quadro da conexão é sempre um keyframe.

Se o celular ou o Wi-Fi não derem conta, o servidor **não acumula atraso**: ele guarda no máximo ~0,5 s de vídeo na fila de envio.
Passou disso, descarta o que está esperando e só volta a enviar no próximo keyframe (a imagem pausa por até 1 s e segue).
Erros do jogo aparecem na própria imagem (tela de espera), não em mensagens de erro.

`GET /games?pin=<pin>` lista os jogos, as teclas permitidas e o layout padrão dos botões.

## Estrutura do código

- `Config/`: opções, perfil (`game.json`), descoberta de .exe/atalho, pastas esqueleto.
- `Sandbox/`: lançador do jogo, limites de CPU/RAM e Job Object (mata o jogo ao fechar a sessão, bloqueia clipboard/resolução etc.).
- `Streaming/`: ffmpeg (H.264), divisor de quadros, compositor/captura da janela.
- `Input/`: scancodes e `SendInput` (só teclado, só teclas do perfil).
- `Net/`: PIN, sessão WebSocket e fila de envio do vídeo.
- `tests/`: autoteste do caminho do vídeo.
- `Interop/`: P/Invoke do Windows.

## Limites conhecidos (próximas etapas)

- **Sandbox**: o Job Object não isola arquivos nem rede. Falta token restrito / AppContainer (ou Sandboxie), com escrita liberada só na pasta do jogo.
- **Foco**: `SendInput` vai para a janela em primeiro plano. Ideal: monitor virtual ou sessão do Windows dedicada ao jogo.
- **Captura**: `PrintWindow` pode dar tela preta em jogos com DirectX moderno (ex.: alguns Godot 4); o modo `screen` exige janela visível. Alternativa melhor: Windows Graphics Capture.
- **Rede**: HTTP/WebSocket puro e PIN em query string. Só para rede local. Falta TLS, limite de tentativas e token por dispositivo.
- **Latência**: transporte é TCP. Quando o Wi-Fi oscila o servidor já descarta quadros (veja acima), mas o TCP ainda pode reter dados no buffer do sistema. Depois: UDP, bitrate adaptativo, pedido de keyframe pelo cliente.
- **Um jogador por vez** (MVP).
