# ExtendedScreen

Use um tablet Android como **monitor estendido** do Windows ou do Linux (GNOME/Wayland), ligado pelo **cabo USB**.

```
Windows: monitor virtual (Virtual Display Driver)
  → captura (DXGI Desktop Duplication) → NV12 na GPU → H.264 (Media Foundation, GPU)
  → TCP localhost:27183 ══ adb reverse (USB) ══► app Android → MediaCodec → tela
  ◄══ toque (vira mouse) e caneta (pressão/inclinação, pen nativo do Windows) ══
```

## Requisitos

- Windows 10 1809+ / Windows 11, .NET 10 Desktop Runtime
- Tablet Android 8.0+ com **Depuração USB** ativada
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (driver IddCx open-source, assinado), que cria o monitor extra no Windows

## Instalação

### 1. Monitor virtual
1. Baixe o instalador em *Releases* do repositório **VirtualDrivers/Virtual-Display-Driver** e instale.
2. Em *Configurações › Sistema › Tela* aparece um monitor novo. Deixe em **"Estender estas telas"** e arraste-o para o lado certo do monitor principal.
3. Para a resolução nativa do tablet (ex.: 2560x1600), adicione-a na lista de resoluções do driver (`vdd_settings.xml`, normalmente em `C:\VirtualDisplayDriver\`, ou pelo app de controle do driver) e recarregue o driver. Com a resolução disponível, o ExtendedScreen troca o monitor virtual para ela sozinho quando o tablet conecta.

### 2. Tablet
1. *Configurações › Sobre o tablet* → toque 7× em **Número da versão** para liberar as Opções do desenvolvedor.
2. *Opções do desenvolvedor* → ative **Depuração USB**.
3. Instale o app (`android/app/build/outputs/apk/debug/app-debug.apk`):
   ```bash
   adb install -r android/app/build/outputs/apk/debug/app-debug.apk
   ```
4. Ligue o cabo e aceite o pedido **"Permitir depuração USB?"** (marque "Sempre permitir deste computador").

### 3. PC
Rode `ExtendedScreen.Host.exe`. Ele fica na bandeja do sistema, encontra o tablet via adb, abre o túnel USB e espera o app conectar. Abra o app **ExtendedScreen** no tablet.

O `adb.exe` é procurado em `tools\platform-tools`, depois no Android SDK (`%LOCALAPPDATA%\Android\Sdk`), depois no `PATH`. Se você não tiver nenhum desses:

```bash
powershell -ExecutionPolicy Bypass -File scripts/get-adb.ps1
```

## Uso

- **Dedo**: o primeiro dedo é o mouse (toque = clique, arrastar = arrastar com o botão esquerdo).
- **Caneta**: vira uma caneta nativa do Windows, com pressão, inclinação, botão lateral (clique direito) e borracha. Funciona com pressão no Krita, Photoshop, OneNote etc. Enquanto a caneta está perto da tela, os toques de dedo são ignorados (rejeição de palma).
- **Menu da bandeja**: escolher o monitor (qualquer um, inclusive para espelhar o principal), FPS (30/60), bitrate, ajuste automático de resolução, cursor do mouse, encoder de hardware/software, log.

Configurações: `%APPDATA%\ExtendedScreen\settings.json`. Log: `%APPDATA%\ExtendedScreen\log.txt`.

## Compilar

PC (requer .NET 10 SDK; a solution inteira compila também no Linux):

```bash
dotnet build pc/ExtendedScreen.sln -c Release
```

```bash
dotnet test pc/ExtendedScreen.Tests
```

```bash
dotnet publish pc/ExtendedScreen.Host -c Release -o dist
```

Android (requer JDK 17 e Android SDK 35; ajuste `android/local.properties` com `sdk.dir`):

```bash
cd android && gradlew.bat assembleDebug
```

## Linux (GNOME/Wayland)

```
GNOME (Mutter): monitor virtual criado sob demanda (ScreenCast.RecordVirtual), sem driver
  → PipeWire → GStreamer → H.264 (VA-API na GPU, x264 ou OpenH264)
  → TCP localhost:27183 ══ adb reverse (USB) ══► app Android (o mesmo)
  ◄══ toque (mouse via RemoteDesktop do Mutter) e caneta (mesa digitalizadora virtual via uinput, com pressão) ══
```

Requisitos: sessão **GNOME no Wayland** (testado no Fedora 44 / GNOME 50), .NET 10 SDK, adb, GStreamer com `pipewiresrc`.

### Instalação (Fedora)

```bash
scripts/linux/setup.sh          # dotnet, adb, plugins do GStreamer e regra udev da caneta
scripts/linux/setup.sh --gpu    # idem + Mesa do RPM Fusion, para encode H.264 na GPU (AMD/Intel)
```

A regra udev (`scripts/linux/70-extendedscreen-uinput.rules`) dá ao usuário logado acesso a `/dev/uinput`, que cria a caneta com pressão e inclinação. Sem ela, a caneta funciona como mouse.

### Uso

```bash
dotnet run --project pc/ExtendedScreen.Linux -c Release -- --check   # verifica tudo
dotnet run --project pc/ExtendedScreen.Linux -c Release              # servidor (Ctrl+C sai)
```

Quando o app do tablet conecta, aparece um monitor novo ("Virtual remote monitor") com a resolução do tablet. Posicione-o em *Configurações › Telas*. Ele some quando o tablet desconecta ou o programa fecha. O GNOME mostra o ícone de compartilhamento de tela enquanto ele existe.

| Opção | Efeito |
|---|---|
| `--fps 30` / `--bitrate 15` | FPS e Mbps desta execução (padrões em `~/.config/ExtendedScreen/settings.json`) |
| `--mirror DP-1` | espelha um monitor existente em vez de criar um virtual (`--list-displays` lista os conectores) |
| `--no-cursor` | não desenha o cursor no vídeo |
| `--software` | ignora o encoder da GPU |
| `--record t.h264 --seconds 5 --width 1920 --height 1080` | grava um monitor virtual em arquivo, sem tablet (abre no `ffplay`) |

Log: `~/.config/ExtendedScreen/log.txt`. A caneta virtual (dispositivo `1209:e5c1`) cobre o desktop inteiro. O programa posiciona cada ponto dentro do monitor do tablet a partir do layout atual, porque o GNOME não aceita mapear uma mesa para um monitor virtual. Por isso ele zera o mapeamento dessa mesa em `org.gnome.desktop.peripherals.tablet`.

## Diagnóstico

Sem tablet, dá para testar a captura e o encoder gravando um arquivo H.264 (abre no VLC ou no `ffplay`):

```bash
ExtendedScreen.Host.exe --list-displays
```

```bash
ExtendedScreen.Host.exe --record teste.h264 --seconds 5 --display \\.\DISPLAY1
```

Use `--software` para forçar o encoder de software da Microsoft.

| Sintoma | O que verificar |
|---|---|
| Tablet fica em "Aguardando o PC…" | Menu da bandeja: a linha "USB" deve mostrar o número de série do tablet. Se mostrar "Autorize a depuração USB", aceite o pedido no tablet. |
| "Nenhum monitor virtual encontrado" | Instale o Virtual Display Driver ou escolha um monitor no menu **Monitor**. |
| Imagem esticada ou com faixas pretas | A resolução do monitor virtual é diferente da do tablet. Adicione a resolução do tablet no driver (passo 1.3). |
| Toque cai no lugar errado | Garanta que o app do PC está com DPI por monitor (padrão) e que o monitor certo está escolhido no menu. |

## Estrutura

- `pc/ExtendedScreen.Core`: protocolo, utilitários H.264, mapeamento de coordenadas (sem dependências, testado em `ExtendedScreen.Tests`)
- `pc/ExtendedScreen.Server`: servidor TCP, sessão com o tablet, adb e configurações, comuns a Windows e Linux (`IScreenBackend` separa o que é de cada SO)
- `pc/ExtendedScreen.Host`: app de bandeja do Windows (captura DXGI, encoder Media Foundation, injeção de entrada)
- `pc/ExtendedScreen.Linux`: host do Linux (Mutter via D-Bus, GStreamer, uinput)
- `android/`: app Kotlin (conexão, decodificador, captura de toque e caneta)
- `docs/protocol.md`: especificação do protocolo

## Limitações

- Exige Depuração USB (o transporte é o `adb reverse`). Um modo AOA/WinUSB sem depuração seria uma evolução futura.
- O app Android abre em paisagem (`sensorLandscape`).
- O clique direito com o dedo (toque longo ou com dois dedos) ainda não foi implementado. Use o botão lateral da caneta.
- Áudio não é transmitido.
- No Linux, só o GNOME (Mutter) está suportado. KDE/wlroots usam outras APIs de monitor virtual.
