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

Padrão em `appsettings.json`: `GameCores: 1`, `GameMemoryMb: 0` (dinâmico), `MemoryReserveMb: 768`, `CoreStart: 0`.
- **CPU:** o jogo fica só no núcleo 0; servidor e ffmpeg usam os demais. Para dar mais núcleos, aumente `GameCores`.
- **RAM dinâmica:** com `GameMemoryMb: 0`, cada vez que um jogo abre o teto é a RAM livre naquele momento menos `MemoryReserveMb` (mínimo 1024 MB). Um número maior que 0 volta a ser limite fixo. O `game.json` de cada jogo pode ter `Cores`/`MemoryMb` próprios.
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

### Diagnóstico avançado (imagem congelada, preta ou sem resposta às teclas)

Se o vídeo flui (30 quadros/s, 0 descartes) mas o jogo parece "travado", o problema está **antes** do codificador. Um bitrate constante e baixo (ex.: 96 kbps) significa que a imagem enviada é sempre idêntica. O servidor agora investiga sozinho:

- **Arquivo de log**: `diagnostico\servidor-AAAA-MM-DD_HH-mm-ss.log` (mesmo conteúdo do console, com horário). **Envie este arquivo** ao reportar um problema.
- **Página ao vivo**: com o celular conectado, abra `http://localhost:5055/debug` no PC (só aceita acesso local). Mostra a imagem exata enviada ao codificador e o estado do jogo/captura/teclas, atualizando a cada 1 s.
- **Linhas `DIAGNÓSTICO`**: `DIAGNÓSTICO ERRO|AVISO [CÓDIGO] mensagem`, com a causa provável e o teste sugerido. Códigos: `IMAGEM_PARADA`, `JANELA_TRAVADA`, `JANELA_MINIMIZADA`, `CAPTURA_FALHA`, `CAPTURA_FALHA_PARCIAL`, `CAPTURA_LENTA`, `SEM_VIDEO`, `REDE_LENTA`, `SEM_FOCO`, `TECLAS_RECUSADAS`, `TECLA_SEM_EFEITO`, `JOGO_FECHOU`. Cada problema é escrito uma vez, repetido a cada 30 s se continuar, e avisado quando some.
- **Provas em disco** (na mesma pasta): `quadro-*.bmp` (o que foi capturado: preto? parado?) e `video-*.h264` (os primeiros 20 s do fluxo exatamente como enviados ao celular; abra com `ffplay -f h264 video-*.h264`). Se o `.h264` toca no PC e o celular não decodifica, o problema é do decodificador do celular; se o `.bmp` já está preto/parado, é da captura.
- Resumo no fim da sessão (quadros, bytes, keyframes, teclas, mudanças de imagem) e conferência do SPS enviado contra o `hello`.
- O cliente pode mandar `{"t":"log","m":"texto"}` e a mensagem aparece no log do servidor.
- Opções em `appsettings.json` > `Server:Diagnostics`: `Enabled`, `Dir`, `FileLog`, `SaveFrames`, `DumpH264Seconds` (0 desliga), `DebugPage`, `KeepDays`.

Se o log indicar `IMAGEM_PARADA` com o jogo ativo, teste no `game.json` primeiro `"hideWindow": false` e depois também `"captureMode": "screen"`.

## Teclado (segurança)

O servidor injeta **somente**: letras A-Z, números 0-9, setas (`UP DOWN LEFT RIGHT`), `CTRL`, `SHIFT` e `ESC`. A lista fica em `Input/KeyMap.cs` e **não depende** do `game.json`: o `allowedKeys` é só informativo (o cliente recebe a lista fixa em `safeKeys` no `GET /games`).

- **Nunca** existem F1-F12, PrintScreen, Windows, ALT, TAB, ENTER, SPACE etc.: não há scancode cadastrado, então nem por engano saem.
- `CTRL+ESC` (menu Iniciar) e `CTRL+SHIFT+ESC` (Gerenciador de Tarefas) são bloqueados: ESC é recusado com CTRL apertado e vice-versa.
- Tecla **apertada** só é enviada se o jogo (ou outra janela do mesmo processo) estiver em primeiro plano naquele instante. Se o Windows não deixar trazer o jogo para frente, a tecla é **descartada** e o log mostra `DESCARTADA` / `SEM_FOCO`. Soltar tecla é sempre permitido, para nada ficar preso.
- Não existe caminho para mouse.

## Ocultar a janela do jogo (`hideMode`)

Com `hideWindow: true` (padrão), a janela some do Alt+Tab e da barra de tarefas. `hideMode` escolhe como:

- `"transparent"` (padrão): a janela fica **dentro do monitor** (o Windows só desenha o que está dentro da área de trabalho), com 1/255 de opacidade, sem receber cliques e **sempre abaixo de todas as outras janelas** (conferido a cada 250 ms; se o jogo pedir "sempre no topo", isso é desfeito). Ao dar foco para o jogo, a janela volta ao fundo sem perder o foco.
- `"offscreen"`: fora de todos os monitores (modo antigo; alguns jogos param de desenhar).

Limites: janela maior que o monitor ou coberta por janelas maximizadas pode não ser desenhada por jogos Chromium. Se a imagem ainda congelar, o log mostra `IMAGEM_PARADA`; tente `"hideMode": "offscreen"` ou `"hideWindow": false`.

## Qualidade de imagem e FPS

Padrões novos (para jogos criados a partir de agora): transmissão **1280x720 a 60 fps**, qualidade constante **CRF 21** com teto de **8000 kbps**, x264 **veryfast**, perfil **Main**.

- **Sem serrilhado nem borrado:** o jogo é capturado no **tamanho nativo** (sem reamostrar no Windows). O ffmpeg ajusta para o tamanho da transmissão com `scaleMode` (bicubic por padrão) e coloca faixas pretas se a proporção for diferente. Só se o jogo for maior que a transmissão ele é reduzido antes, com média de pixels.
- **Imagem parada fica nítida e barata:** com CRF, o vídeo gasta pouco em tela estática e usa até o teto quando há movimento.
- **Cores corretas:** conversão e marcação BT.709.
- Se o tamanho da janela do jogo muda (tela de espera -> jogo), o ffmpeg reinicia por uns 0,3 s, mas a saída continua com o mesmo tamanho e perfil, então o celular não reconfigura o decodificador.

Opções no `game.json` do jogo:

| campo | padrão | o que faz |
|---|---|---|
| `streamWidth` / `streamHeight` | 1280 / 720 | tamanho da transmissão (fixo) |
| `fps` | 60 | quadros por segundo (1 a 60) |
| `crf` | 21 | qualidade: 18 = muito nítido, 26 = leve; `0` = taxa fixa |
| `bitrateKbps` | 8000 | teto de taxa (alvo se `crf` for 0) |
| `preset` | veryfast | `ultrafast`, `superfast`, `veryfast`, `faster`, `fast`, `medium` (mais lento = mais qualidade e mais CPU) |
| `h264Profile` | main | `baseline` (mais compatível), `main`, `high` |
| `scaleMode` | bicubic | `lanczos` (mais nítido), `bilinear`, `neighbor` (pixel art: bordas duras) |
| `encoderThreads` | 0 | 0 = um por núcleo livre do ffmpeg (até 4) |

**game.json já existente não muda sozinho.** Os valores antigos (640x480, 30 fps, 2000 kbps) continuam valendo. Edite o arquivo, ou apague-o para o servidor gerar um novo com os padrões (perde seus ajustes de `exe`, `hideWindow` etc.).

Se o PC não acompanhar, o log mostra `FPS_BAIXO` dizendo se o gargalo é a captura ou a codificação. Para aliviar: `"preset": "ultrafast"`, `"fps": 30` ou `"streamWidth": 960, "streamHeight": 540`. Se algum celular não decodificar, use `"h264Profile": "baseline"`.

## Adaptação à tela do cliente (`sw`, `sh`, `fit`)

O cliente pode informar a tela do aparelho ao conectar: `ws://<ip>:5055/stream?game=<id>&pin=<pin>&sw=<largura>&sh=<altura>&fit=fit|fill|stretch`. Sem esses parâmetros vale o tamanho do `game.json`.

- **Tamanho do quadro:** parte da altura do perfil (`streamHeight`) e ajusta a largura para a proporção da tela (ex.: tela 20:9 com perfil 1280x720 vira ~1280x576). A quantidade de pixels nunca passa da do perfil, então o custo de CPU e de rede é o mesmo. O teto de taxa acompanha os pixels (0,5x a 2x o `bitrateKbps`). Proporção limitada a 1:1 até 2,5:1. O tamanho fica fixo durante a sessão.
- **`fit`** (padrão): jogo inteiro, com faixas pretas se a proporção for diferente. **`fill`**: ocupa tudo cortando as bordas. **`stretch`**: ocupa tudo deformando.
- O ajuste é feito pelo ffmpeg (bicubic), igual para os três modos.

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

`ws://<ip>:5055/stream?game=<id>&pin=<pin>[&sw=<largura da tela>&sh=<altura da tela>&fit=fit|fill|stretch]`

| Direção | Formato |
|---|---|
| servidor -> cliente | texto `{"t":"hello","game","w","h","fps"}` assim que conecta; com o som ligado traz também `"audio":{"codec":"aac","rate":48000,"ch":2}` |
| servidor -> cliente | binário: `[1][flags][quadro H.264 Annex B]` (flags bit0 = keyframe), **sempre** |
| servidor -> cliente | binário: `[2][0][quadro AAC cru]` (som do jogo; só existe se o `hello` trouxe `audio`; veja "Som") |
| servidor -> cliente | texto `{"t":"pong",...}` em resposta a `{"t":"ping",...}` |
| cliente -> servidor | texto `{"t":"key","k":"Z","d":1}` (d: 1 apertou, 0 soltou) |
| cliente -> servidor | texto `{"t":"ping",...}` (ou `ping` puro) |

O cliente deve separar as mensagens binárias pelo **primeiro byte** (1 = vídeo, 2 = som), descartar os 2 primeiros bytes e entregar o resto ao decodificador certo (H.264 ou AAC). Entregar um quadro de som ao decodificador de vídeo, ou o contrário, quebra a imagem.

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

## Som

O som do jogo vai para o celular junto com o vídeo, pelo mesmo WebSocket.

- **Só o jogo**: o servidor usa a captura de áudio *por processo* do Windows (WASAPI process loopback) no processo do jogo e nos filhos dele. Notificações, música e outros programas do PC **nunca** são enviados. Precisa do **Windows 10 versão 2004 (build 19041) ou mais novo**; em versões mais antigas o som fica desligado (não há captura do sistema inteiro).
- **Formato**: AAC-LC, 48 kHz, estéreo (ffmpeg, `128 kbps` por padrão). Cada quadro cru segue como mensagem binária `[2][0][quadro AAC]`.
- **hello**: o JSON inicial traz `"audio":{"codec":"aac","rate":48000,"ch":2}` quando o som está ligado.
- **game.json**: `"audio": true|false` e `"audioBitrateKbps": 32..320`.
- Se a captura falhar, o jogo segue sem som e o log diz o motivo (`Som DESLIGADO nesta sessão`). No fim da sessão o log mostra `Som: N quadros AAC capturados`. Se N for 0, o som não ligou.
- Se o jogo ainda não tocou nada, o servidor envia silêncio para manter o fluxo contínuo.

### Som do jogo mudo no PC do servidor (`muteGameOnServer`)

Com o som do celular ligado, o jogo fica **mudo nas caixas do PC** (com `"muteGameOnServer": true`; o padrão é `false` porque em teste real a captura veio muda com o mute ligado). É o mesmo que "silenciar o aplicativo" no Mixer de volume do Windows, só para o processo do jogo e os filhos dele; o resto do PC continua com som. O silêncio é desfeito quando a sessão termina (e antes de o jogo ser fechado).

- Só silencia se o som estiver de fato indo para o celular (`audio` ligado e captura funcionando).
- No log: `Som do jogo silenciado no PC do servidor`. No resumo: `N pacotes com som de verdade`. Se for 0 com o jogo tocando, o Windows está calando a captura junto: ponha `"muteGameOnServer": false` no `game.json`.
- Se o servidor travar e o jogo ficar mudo na próxima vez, tire o mudo do aplicativo no Mixer de volume do Windows.

### Som por dispositivo de saída (`audioCaptureDevice`): jogo mudo no PC sem perder o som no celular

Em vez da captura por processo, o servidor pode capturar **um dispositivo de reprodução inteiro** do Windows. O jogo é apontado para uma saída que ninguém ouve (por exemplo "Digital Audio (S/PDIF)" sem nada ligado); o PC fica em silêncio e o celular ouve o jogo. Não depende do mute do aplicativo.

1. Em **Configurações > Sistema > Som > Volume do aplicativo e preferências de dispositivo**, escolha para o **jogo** (o `player.exe`, por exemplo) a saída "Digital Audio (S/PDIF)". Faça isso com o jogo aberto, ou feche e abra o jogo depois de trocar: alguns jogos só escolhem a saída ao iniciar.
2. O dispositivo precisa estar **ativo** em Som > Reprodução (não "desativado" nem "desconectado"). O Windows só deixa capturar dispositivos ativos.
3. No `game.json`: `"audioCaptureDevice": "S/PDIF"` (basta um pedaço do nome, sem diferenciar maiúsculas).
4. No log: `Som do jogo ligado ... modo "dispositivo "Digital Audio (S/PDIF) ...", 48000 Hz ...`. Se o dispositivo não for achado ou estiver desconectado, o log lista todos os dispositivos e o estado de cada um.

Atenção: TUDO o que tocar nesse dispositivo vai para o celular. Deixe só o jogo nele. Com este modo ligado, `muteGameOnServer` não é usado.

## Núcleos dinâmicos e uso em segundo plano

- **Núcleos**: `GameCores` agora é `0` por padrão = **dinâmico**: o jogo recebe metade dos núcleos lógicos (mínimo 1, máximo 4), e o resto fica para o servidor, o x264 e o ffmpeg do som. Em um PC de 4 núcleos, 2 vão para o jogo. Um número maior que 0 fixa a quantidade (`"GameCores": 2`), e um jogo pode ter o seu em `"cores"` no `game.json`.
- **Segundo plano**: ao iniciar, o servidor liga temporizador de 1 ms, desliga a redução de CPU do Windows para processos em segundo plano (EcoQoS) e zera o "bloqueio de foreground". Sem isso o vídeo caía para ~30 fps e o Windows recusava dar foco ao jogo (`SEM_FOCO`) quando a janela do servidor não estava na frente. A linha `Ajustes do servidor: ...` no console e no log diz o que o Windows aceitou. Se o bloqueio de foreground for recusado, deixe a janela do servidor na frente uma vez ao abrir.
- O servidor continua sem injetar tecla fora do jogo: ao dar foco ele só usa chamadas de janela (`SetForegroundWindow`, `SwitchToThisWindow`).
- No log de cada sessão aparece `Som: modo CAPTURA POR PROCESSO ...` ou `Som: modo DISPOSITIVO "..."`, para você ver qual dos dois está valendo.

## Segurança do PIN e prioridade do som

- **Limite de tentativas**: 5 PINs errados em 1 minuto bloqueiam aquele endereço por 1 minuto (resposta `429`, com `Retry-After`); cada novo bloqueio dobra o tempo, até 15 min. Acertar o PIN zera a conta. O próprio PC (`localhost`) não é limitado. O log avisa `Muitos PINs errados vindos de ...`.
- **O tráfego ainda é HTTP puro** (PIN na URL): use só em rede local confiável. TLS e token por dispositivo continuam pendentes.
- **Som antes do vídeo**: no envio, o áudio pendente sai antes do próximo quadro de vídeo. Um keyframe grande atrasa o som no máximo um quadro, e não a fila inteira.

## Modos de transmissão (leve, médio, pesado)

`"qualityMode"` no `game.json`: `"auto"` (padrão), `"leve"`, `"medio"` ou `"pesado"`.

| Modo | fps | Taxa (kbps) |
|---|---|---|
| pesado | o do `game.json` (até 60) | a do `game.json` (`bitrateKbps`, ajustada ao tamanho do quadro) |
| médio | até 30 | ~55% |
| leve | até 24 | ~28% |

- **auto**: começa no **médio**, **desce** para o modo mais leve depois de 2 segundos ruins nos últimos 4 (fila de envio cheia, quadros descartados ou envio demorando 300 ms ou mais) e **sobe** depois de 20 segundos seguidos bons. Se uma subida falha logo em seguida, a espera para a próxima dobra (até 2 min), para não ficar alternando.
- O tamanho do quadro **não muda** entre os modos: o decodificador do celular continua o mesmo. Só mudam fps e taxa; o ffmpeg reinicia por uns décimos de segundo a cada troca.
- O log mostra `Modo de transmissão -> leve: ... Motivo: ...` a cada troca e o modo final no resumo. O servidor também manda `{"t":"quality","mode":"leve","fps":24,"kbps":2240}` ao cliente (informativo).
- `"qualityMode": "pesado"` (ou outro fixo) desliga a adaptação.

## Som em soquete separado

Com som ligado, o `hello` traz `audioSocket: { path: "/audio", token }`. O cliente abre `ws://host:porta/audio?pin=...&token=...` e o som passa a ir por ele,
sem esperar na fila do vídeo (um quadro de vídeo grande não atrasa mais o som). Cliente antigo, ou soquete que falhe/caia: o som segue (ou volta) pelo soquete principal automaticamente.
O token é aleatório por sessão e o PIN continua obrigatório.
