# Protocol interfície ↔ motor

**Versió del protocol:** 1
**Estat:** fase 1 (ordres bàsiques del motor, llista de dispositius i avisos de canvis)

Qualsevol canvi en aquest protocol s'ha d'anotar aquí en el mateix commit que el canvi de codi.

## 1. Transport

- WebSocket a `ws://127.0.0.1:<port>/ws?token=<testimoni>`.
- El motor escolta **només** a `127.0.0.1`. Port per defecte: `47800`.
- El testimoni el genera qui arrenca el motor (`--token`) o, si no se n'hi passa cap, el mateix motor.
- El motor publica la informació de connexió a `%APPDATA%\OnaBages\engine.json`:

```json
{ "schemaVersion": 1, "pid": 1234, "port": 47800, "token": "…", "startedAt": "2026-11-01T22:00:00Z", "version": "0.1.0" }
```

- Comprovació de vida sense testimoni: `GET http://127.0.0.1:<port>/health` → `200 ok`.
- Es poden connectar diversos clients alhora (la interfície i la consola de proves). Els esdeveniments s'envien a tots.
- Mida màxima d'un missatge: 64 KB.

## 2. Format dels missatges

Tots els missatges són objectes JSON amb el camp `"v": 1`.

### Ordre (client → motor)

```json
{ "v": 1, "id": 7, "cmd": "engine.hello" }
```

- `id`: número que tria el client; la resposta porta el mateix `id`.
- `cmd`: nom de l'ordre, en format `àmbit.acció`.
- Altres camps: paràmetres de l'ordre.

### Resposta (motor → client)

```json
{ "v": 1, "id": 7, "ok": true, "result": { … } }
{ "v": 1, "id": 7, "ok": false, "error": { "code": "unknown_command", "message": "…" } }
```

### Esdeveniment (motor → clients)

```json
{ "v": 1, "ev": "engine.status", "uptime": 12.0, "clients": 1 }
```

## 3. Codis d'error

| Codi | Significat |
|---|---|
| `bad_json` | El missatge no és JSON vàlid o no és un objecte |
| `protocol_version` | El camp `v` falta o no és compatible |
| `missing_cmd` | Falta el camp `cmd` |
| `bad_params` | Falta un paràmetre o té un valor no vàlid (p. ex. un reproductor que no existeix) |
| `bad_state` | L'ordre no es pot fer en l'estat actual (p. ex. Play sense pista carregada). No canvia res |
| `player_error` | L'ordre s'ha fet, però el reproductor ha quedat en estat `error` (dispositiu no disponible, fitxer il·legible). El missatge n'explica el motiu |
| `unknown_command` | L'ordre no existeix |
| `internal` | Error inesperat del motor (el motor continua funcionant) |

## 4. Ordres

| Ordre | Paràmetres | Resultat | Des de |
|---|---|---|---|
| `engine.hello` | — | `{ name, version, protocol }` | fase 0 |
| `engine.ping` | — | `{ pong: true, time }` | fase 0 |
| `state.get` | — | `{ engine: { version, uptime, clients }, players: [ … ] }` (vegeu 4.2; s'ampliarà amb els enregistradors) | fase 0 (`players`: fase 1) |
| `engine.shutdown` | — | `{ stopping: true }` i el motor es tanca. A partir de la fase 1, es rebutjarà si hi ha enregistraments actius, tret de `force: true` | fase 0 |
| `devices.list` | `kind`: `"output"`, `"input"` o `"all"` (per defecte); `includeInactive`: `true` per incloure també els desactivats i desconnectats (per defecte, `false`) | `{ devices: [ … ] }` (vegeu 4.1) | fase 1 |

### 4.1. Dispositius

```json
{ "id": "{0.0.0.00000000}.{f2d6376b-71c4-4ffc-a362-a446ddd885a8}",
  "name": "Altavoces - Sound Blaster Z (Sound Blaster Z)",
  "kind": "output",
  "status": "active",
  "format": { "sampleRate": 48000, "bits": 24, "channels": 2 } }
```

- `id`: identificador estable de Windows. És el que es desa a la configuració.
- `name`: nom complet tal com el mostra Windows. Canvia si l'usuari reanomena el dispositiu; l'`id`, no.
- `kind`: `output` (sortida) o `input` (entrada).
- `status`: `active`, `disabled` (desactivat a Windows) o `unplugged` (la targeta hi és però no hi ha res connectat).
- `format`: el format triat a Windows (o el del fabricant, si no se n'ha triat cap); `null` si no se sap.
- Els dispositius que Windows recorda però que ja no hi són no s'inclouen mai.

### 4.2. Reproductors

Fase 1: hi ha dos reproductors fixos, `"1"` i `"2"`. Encara no hi ha llistes ni es desa res.

| Ordre | Paràmetres | Estats en què es pot fer | Resultat |
|---|---|---|---|
| `player.setDevice` | `player`, `deviceId` | tots menys `playing`. Si hi ha pista, es torna a preparar: des del mateix punt si era a `paused` o `error`; des de l'inici en els altres casos | `{ player }` |
| `player.load` | `player`, `path` (ruta completa) | tots menys `playing`. Cal haver triat la sortida | `{ player }` |
| `player.play` | `player` | `ready`, `paused`, `ended` (torna a començar) | `{ player }` |
| `player.pause` | `player` | `playing` | `{ player }` |
| `player.stop` | `player` | tots menys `error`. Torna a l'inici (`ready`) | `{ player }` |
| `player.retry` | `player` | `error`. Torna a obrir la sortida i el fitxer i queda `ready` **a la mateixa posició**. No torna a sonar sol: cal `player.play` | `{ player }` |

Objecte `player` (també a `state.get` i a l'esdeveniment `player.state`):

```json
{ "id": "1", "state": "playing",
  "deviceId": "{0.0.0.00000000}.{f2d6376b-…}",
  "file": "D:\\Musica\\prova.mp3",
  "duration": 183.512, "position": 12.300,
  "error": null, "underruns": 0 }
```

- `state`: `empty` (sense pista), `ready` (preparat), `playing`, `paused`, `ended` (ha acabat la pista), `error`.
- `duration` i `position`: en segons, amb tres decimals.
- `error`: el motiu, en català, quan `state` és `error`. La pista i la posició es conserven. Per continuar: `player.retry` (mateixa sortida) o `player.setDevice` (una altra), i després `player.play`. El reproductor **no reprèn mai sol**. El detall tècnic de Windows no s'envia: només queda al registre del motor.
- `underruns`: talls des que es va obrir el reproductor (la targeta ha demanat so i no n'hi havia de preparat). Ha de ser 0; serveix per a les proves llargues.

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `uptime` (s), `clients` | cada segon | fase 0 |
| `devices.changed` | `changes`: llista de canvis (vegeu 5.1) | quan Windows avisa d'un canvi real, agrupat (0,5 s sense avisos nous) | fase 1 |
| `player.state` | `player`: objecte complet (vegeu 4.2) | quan canvia l'estat, el fitxer, la sortida o l'error d'un reproductor | fase 1 |
| `player.position` | `players`: `[ { id, position, duration } ]` de tots els reproductors | 10 per segon, només mentre algun reproductor sona | fase 1 |

### 5.1. Canvis de dispositius

```json
{ "v": 1, "ev": "devices.changed",
  "changes": [
    { "change": "status", "device": { "id": "…", "name": "Altavoces - Realtek (…)", "kind": "output", "status": "unplugged", "format": { … } },
      "previousStatus": "active" }
  ] }
```

| `change` | Què ha passat | Camp addicional |
|---|---|---|
| `added` | Apareix un dispositiu nou (p. ex. s'ha connectat un USB) | — |
| `removed` | El dispositiu ja no hi és (s'ha tret el USB o la targeta) | — (`device` és com era abans) |
| `status` | Ha canviat d'estat (`active`, `disabled`, `unplugged`) | `previousStatus` |
| `renamed` | L'usuari l'ha reanomenat a Windows | `previousName` |
| `format` | Ha canviat el format a Windows | `previousFormat` |

- `device` té el mateix format que a `devices.list` (4.1).
- Un mateix dispositiu pot aparèixer més d'una vegada si li han canviat diverses coses alhora.
- Windows envia molts avisos per a una sola acció (i també quan algú mou el volum). El motor els agrupa i **només envia l'esdeveniment si la llista ha canviat de debò**.
- Els dispositius «no presents» no s'hi tenen en compte: un USB que es treu dona `removed` o `status` → `unplugged`, segons el controlador.

## 6. Previst (no implementat)

- `player.*`, `player.state` (fase 1–2)
- `recorder.*`, `recorder.state` (fase 1–2)
- `cue.*` (preescolta, fase 2)
- `meters` (agrupat, 20–30 per segon, fase 1)
