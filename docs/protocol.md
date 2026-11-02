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
| `bad_params` | Un paràmetre de l'ordre té un valor no vàlid |
| `unknown_command` | L'ordre no existeix |
| `internal` | Error inesperat del motor (el motor continua funcionant) |

## 4. Ordres

| Ordre | Paràmetres | Resultat | Des de |
|---|---|---|---|
| `engine.hello` | — | `{ name, version, protocol }` | fase 0 |
| `engine.ping` | — | `{ pong: true, time }` | fase 0 |
| `state.get` | — | `{ engine: { version, uptime, clients } }` (s'ampliarà amb reproductors i enregistradors) | fase 0 |
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

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `uptime` (s), `clients` | cada segon | fase 0 |
| `devices.changed` | `changes`: llista de canvis (vegeu 5.1) | quan Windows avisa d'un canvi real, agrupat (0,5 s sense avisos nous) | fase 1 |

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
