# Protocol interfície ↔ motor

**Versió del protocol:** 1
**Estat:** fase 0 (només ordres bàsiques del motor)

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
| `unknown_command` | L'ordre no existeix |
| `internal` | Error inesperat del motor (el motor continua funcionant) |

## 4. Ordres

| Ordre | Paràmetres | Resultat | Des de |
|---|---|---|---|
| `engine.hello` | — | `{ name, version, protocol }` | fase 0 |
| `engine.ping` | — | `{ pong: true, time }` | fase 0 |
| `state.get` | — | `{ engine: { version, uptime, clients } }` (s'ampliarà amb reproductors i enregistradors) | fase 0 |
| `engine.shutdown` | — | `{ stopping: true }` i el motor es tanca. A partir de la fase 1, es rebutjarà si hi ha enregistraments actius, tret de `force: true` | fase 0 |

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `uptime` (s), `clients` | cada segon | fase 0 |

## 6. Previst (no implementat)

- `devices.list`, `devices.changed` (fase 1)
- `player.*`, `player.state` (fase 1–2)
- `recorder.*`, `recorder.state` (fase 1–2)
- `cue.*` (preescolta, fase 2)
- `meters` (agrupat, 20–30 per segon, fase 1)
