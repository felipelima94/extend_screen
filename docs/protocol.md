# Protocolo ExtendedScreen

Transporte: TCP. O tablet conecta em `127.0.0.1:27183`, porta que o `adb reverse tcp:27183 tcp:27183` encaminha pelo cabo USB até o PC.

## Framing

Toda mensagem tem o formato:

| campo   | tipo   | descrição              |
|---------|--------|------------------------|
| type    | u8     | tipo da mensagem       |
| length  | u32 LE | tamanho do payload     |
| payload | bytes  | `length` bytes         |

Todos os inteiros e floats são **little-endian**. `length` máximo: 16 MiB.

## Mensagens

### 1 `HELLO` (tablet → PC)
| campo         | tipo   |
|---------------|--------|
| version       | u16 (= 1) |
| width         | u32 — pixels da tela do tablet (orientação atual) |
| height        | u32    |
| densityDpi    | u32    |
| flags         | u8 — bit0: o decoder suporta baixa latência |
| nameLength    | u16    |
| name          | UTF-8 (modelo do aparelho) |

### 2 `CONFIG` (PC → tablet)
| campo   | tipo |
|---------|------|
| width   | u32 — tamanho do vídeo |
| height  | u32  |
| fps     | u32  |
| bitrate | u32 — bits/s |

### 3 `VIDEO_CSD` (PC → tablet)
SPS e PPS em Annex-B (`00 00 00 01 SPS 00 00 00 01 PPS`). O PC envia antes do primeiro keyframe e sempre que eles mudam. Ao receber, o tablet (re)cria o decoder.

### 4 `VIDEO_FRAME` (PC → tablet)
| campo | tipo |
|-------|------|
| ptsUs | i64  |
| flags | u8 — bit0: keyframe |
| data  | resto do payload: access unit H.264 em Annex-B |

### 5 `REQUEST_KEYFRAME` (tablet → PC)
Payload vazio. O tablet envia ao (re)criar o decoder ou depois de um erro.

### 6 `POINTER` (tablet → PC)
| campo     | tipo |
|-----------|------|
| tool      | u8 — 0 dedo, 1 caneta, 2 borracha (caneta invertida) |
| action    | u8 — 0 down, 1 move, 2 up, 3 hover, 4 hover-exit, 5 cancel |
| pointerId | u32 |
| x         | f32 — 0..1 dentro da área do vídeo |
| y         | f32 — 0..1 |
| pressure  | f32 — 0..1 |
| tiltX     | f32 — graus, -90..90 |
| tiltY     | f32 — graus, -90..90 |
| buttons   | u8 — bit0: botão lateral da caneta |

Conversão no PC: o **dedo** vira o mouse (down = botão esquerdo pressionado, move = arrastar, up = soltar). Só o primeiro dedo conta. A **caneta** e a **borracha** viram pen input nativo do Windows, com pressão e inclinação.

### 7 `PING` (PC → tablet) / 8 `PONG` (tablet → PC)
| campo     | tipo |
|-----------|------|
| timestamp | i64 — ticks do relógio do PC |

O tablet devolve o mesmo valor no `PONG`, e o PC calcula o RTT.

## Sequência

```
tablet                         PC
  |---- HELLO ------------------>|
  |<--- CONFIG -------------------|
  |---- REQUEST_KEYFRAME ------->|
  |<--- VIDEO_CSD ----------------|
  |<--- VIDEO_FRAME (key) --------|
  |<--- VIDEO_FRAME ... ----------|
  |---- POINTER ... ------------->|
  |<--- PING / PONG ------------->|
```
