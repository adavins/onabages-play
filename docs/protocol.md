# Protocol interfície ↔ motor

**Versió del protocol:** 1
**Estat:** fase 1 (motor, dispositius, reproductors i enregistradors)

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
| `recorder_error` | L'ordre s'ha fet, però l'enregistrador ha quedat en estat `error` (entrada no disponible, carpeta sense permís...). El missatge n'explica el motiu |
| `recording_active` | `engine.shutdown` sense `force`, amb enregistraments en curs. No s'atura res |
| `unknown_command` | L'ordre no existeix |
| `internal` | Error inesperat del motor (el motor continua funcionant) |

## 4. Ordres

| Ordre | Paràmetres | Resultat | Des de |
|---|---|---|---|
| `engine.hello` | — | `{ name, version, protocol }` | fase 0 |
| `engine.ping` | — | `{ pong: true, time }` | fase 0 |
| `state.get` | — | `{ engine: { version, uptime, clients }, players: [ … ], recorders: [ … ], hardware: { … } }` (vegeu 4.2, 4.3 i 4.4) | fase 0 (`players`, `recorders`, `hardware`: fase 1) |
| `engine.shutdown` | `force` (opcional, `true`) | `{ stopping: true }` i el motor es tanca. Si hi ha enregistraments en curs, es rebutja amb `recording_active`, tret que s'enviï `force: true`: llavors es tanquen correctament (fitxers vàlids, sense marcador `.rec`) abans d'aturar-se | fase 0 (`force`: fase 1) |
| `devices.list` | `kind`: `"output"`, `"input"` o `"all"` (per defecte); `includeInactive`: `true` per incloure també els desactivats i desconnectats (per defecte, `false`) | `{ devices: [ … ] }` (vegeu 4.1) | fase 1 |
| `hardware.report` | — | `{ report: { … } }`: resum de l'arrencada (vegeu 4.4) | fase 1 |
| `settings.get` | — | `{ settings: { ui: { zoom } } }`: ajustos desats a `config.json` | fase 2 |
| `settings.set` | `ui`: els ajustos a canviar, p. ex. `{ "zoom": 1.25 }` (escala de la interfície, de `0.7` a `1.5`) | `{ settings }` amb tots els ajustos. Es desa de seguida. Un valor fora de marge dona `bad_params` | fase 2 |

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
| `player.seek` | `player`, `position` (segons des de l'inici) | `ready`, `playing`, `paused`, `ended`. Salta a aquest punt (si és més enllà del final, es queda al final). Si sonava, **continua sonant** des d'allà, amb un silenci molt curt mentre es torna a obrir la sortida; si era en pausa, continua en pausa; si havia acabat, queda `ready`. Tornar a començar la pista és `position: 0` | `{ player }` |
| `player.setVolume` | `player`, `db` (de `-60` a `0`) | tots, també mentre sona. A `-60` el reproductor queda en silenci; fora de marge es queda al límit. El canvi es fa amb una rampa curta (sense clics). En arrencar el motor, tots els reproductors són a 0 dB (no es desa) | `{ player }` |

Objecte `player` (també a `state.get` i a l'esdeveniment `player.state`):

```json
{ "id": "1", "state": "playing",
  "deviceId": "{0.0.0.00000000}.{f2d6376b-…}",
  "file": "D:\\Musica\\prova.mp3",
  "duration": 183.512, "position": 12.300,
  "error": null, "underruns": 0, "volumeDb": 0 }
```

- `state`: `empty` (sense pista), `ready` (preparat), `playing`, `paused`, `ended` (ha acabat la pista: s'envia uns 200–300 ms després de l'última mostra, quan ja ha sortit tota de la targeta), `error`.
- `duration` i `position`: en segons, amb tres decimals.
- `error`: el motiu, en català, quan `state` és `error`. La pista i la posició es conserven. Per continuar: `player.retry` (mateixa sortida) o `player.setDevice` (una altra), i després `player.play`. El reproductor **no reprèn mai sol**. El detall tècnic de Windows no s'envia: només queda al registre del motor.
- `underruns`: talls des que es va obrir el reproductor (la targeta ha demanat so i no n'hi havia de preparat). Ha de ser 0; serveix per a les proves llargues.
- `volumeDb`: volum del fader (de `-60`, silenci, a `0`).

### 4.3. Enregistradors

Fase 1: hi ha dos enregistradors fixos, `"1"` i `"2"`. Encara no hi ha Desar, Descartar ni Editar (fase 2).

| Ordre | Paràmetres | Estats en què es pot fer | Resultat |
|---|---|---|---|
| `recorder.setDevice` | `recorder`, `deviceId` (una **entrada**) | tots menys `recording` | `{ recorder }` |
| `recorder.start` | `recorder`; opcionals: `folder` (ruta completa; per defecte `Documents\Ona Bages Play\Enregistraments`) i `name` (per al nom del fitxer; per defecte `Enregistrador N`) | `idle`, `pending`, `error` (l'enregistrament anterior es queda on és) | `{ recorder }` |
| `recorder.stop` | `recorder` | `recording`. La interfície **sempre** ha de demanar confirmació abans d'enviar-la | `{ recorder }` |

Fitxer: `<carpeta>\<nom>_AAAA-MM-DD_HH-MM-SS.mp3` (si ja existeix, s'hi afegeix `_2`, `_3`...). MP3 320 kbps CBR estèreo, a la freqüència de l'entrada. Mentre s'enregistra hi ha un marcador `<fitxer>.mp3.rec` (JSON) que s'esborra en aturar correctament.

Objecte `recorder` (també a `state.get` i a l'esdeveniment `recorder.state`):

```json
{ "id": "1", "state": "recording",
  "deviceId": "{0.0.1.00000000}.{1942b38d-…}",
  "file": "C:\\Users\\…\\Enregistraments\\Programa_2026-11-02_17-30-00.mp3",
  "startedAt": "2026-11-02T17:30:00.123+01:00",
  "elapsed": 125.4, "fileSize": 5017600, "freeDiskBytes": 146028888064,
  "sampleRate": 48000, "bitrateKbps": 320,
  "samplesLost": 0, "captureGlitches": 0, "inputActive": true,
  "error": null }
```

- `state`: `idle` (inactiu), `recording`, `pending` (fitxer acabat, a `file`), `error`.
- `elapsed`: segons enregistrats. `fileSize`: bytes escrits. `freeDiskBytes`: espai lliure a la unitat del fitxer (`null` si no se sap, p. ex. en una unitat de xarxa).
- `samplesLost`: mostres perdudes perquè el disc no ha anat prou ràpid. `captureGlitches`: vegades que Windows ha avisat que s'han perdut dades de captura. Tots dos han de ser 0.
- `inputActive`: l'entrada és oberta i el mesurador funciona. **L'entrada s'obre en triar-la, no en començar a enregistrar** (per veure el nivell abans d'enregistrar); `recorder.start` fa servir la mateixa entrada i comença a l'instant. Si l'entrada es perd quan no s'enregistra, no és cap error: `inputActive` passa a `false` i el motor la torna a obrir quan torna (cada 3 s ho prova).
- `error`: el motiu, en català, quan `state` és `error`. Si l'entrada es desconnecta mentre s'enregistra, el fitxer es tanca correctament (vàlid fins a aquell moment) i `file` hi continua apuntant. Per continuar, `recorder.start` en fa un de nou.

### 4.4. Maquinari en arrencar

El motor desa el dispositiu de cada reproductor i enregistrador a `%APPDATA%\OnaBages\config.json` cada vegada que s'envia `player.setDevice` o `recorder.setDevice` (encara que la sortida falli en obrir-se: és la que ha triat l'operador). En arrencar, el compara amb el maquinari present (especificació 5.6). El resultat és a `hardware.report` i a `state.get` (camp `hardware`):

```json
{ "hasChanges": true, "firstRun": false, "configProblem": null,
  "elements": [
    { "element": "player", "id": "1", "result": "missing",
      "saved": { "id": "{0.0.0.00000000}.{f2d6…}", "name": "Altavoces (Sound Blaster Z)" },
      "device": null }
  ],
  "newDevices": [ { …dispositiu… } ],
  "formatChanges": [ { "device": { … }, "previousFormat": { "sampleRate": 44100, "bits": 16, "channels": 2 } } ] }
```

| `result` | Què ha passat | S'assigna? |
|---|---|---|
| `ok` | Hi és, amb el mateix identificador | Sí |
| `unplugged` | Hi és, però sense cable | Sí (funcionarà quan es connecti) |
| `disabled` | Hi és, però desactivat a Windows | Sí (cal activar-lo) |
| `idChanged` | L'identificador ha canviat, però hi ha **un sol** dispositiu amb el mateix nom (p. ex. un USB en un altre port). Es desa l'identificador nou | Sí |
| `ambiguous` | Hi ha diversos dispositius amb el mateix nom | **No**: l'operador n'ha de triar un |
| `missing` | No hi és | **No**. Si torna mentre el motor funciona, s'hi assigna sol (`hardware.reassigned`) |

- `hasChanges`: `false` si no hi ha res a explicar (la interfície no mostra la finestra «Canvis en el maquinari»).
- `firstRun`: no hi ha cap dispositiu desat (primera arrencada o configuració perduda). La interfície proposarà una assignació (fase 2).
- `configProblem`: si `config.json` no s'ha pogut llegir, el motiu. El motor arrenca sense assignacions i en desa una còpia a `config.json.malmes`.
- `elements`: només els elements que tenien un dispositiu desat. `device` és el dispositiu trobat ara (format de 4.1) o `null`.
- `newDevices`: dispositius actius que no hi eren l'última vegada i no són de cap element. `formatChanges`: dispositius amb el format canviat a Windows.
- El motor **mai no assigna un dispositiu diferent** del que s'havia triat (D-016). El resum és el de l'arrencada: no canvia fins que el motor es reinicia.

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `uptime` (s), `clients` | cada segon | fase 0 |
| `devices.changed` | `changes`: llista de canvis (vegeu 5.1) | quan Windows avisa d'un canvi real, agrupat (0,5 s sense avisos nous) | fase 1 |
| `player.state` | `player`: objecte complet (vegeu 4.2) | quan canvia l'estat, el fitxer, la sortida o l'error d'un reproductor | fase 1 |
| `player.position` | `players`: `[ { id, position, duration } ]` de tots els reproductors | 10 per segon, només mentre algun reproductor sona | fase 1 |
| `recorder.state` | `recorder`: objecte complet (vegeu 4.3) | quan canvia l'estat, el fitxer, l'entrada o l'error d'un enregistrador | fase 1 |
| `recorder.progress` | `recorders`: `[ { id, elapsed, fileSize, freeDiskBytes, samplesLost, captureGlitches } ]` dels que enregistren | cada segon, només mentre algun enregistra | fase 1 |
| `hardware.reassigned` | `element`: una línia com les de `elements` de 4.4, amb el dispositiu ja assignat | quan torna el dispositiu d'un element que en arrencar era `missing`. El reproductor **no es posa a sonar**; després arriba també el seu `player.state` o `recorder.state` | fase 1 |
| `meters` | `players`, `recorders`: nivells de pic de cada element (vegeu 5.2) | 25 per segon, si hi ha alguna interfície connectada | fase 2 |

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

### 5.2. Mesuradors

```json
{ "v": 1, "ev": "meters",
  "players":   { "1": [-12.3, -11.8], "2": [-90, -90] },
  "recorders": { "1": [-18.0, -17.5], "2": [-90, -90] } }
```

- Per a cada element, `[esquerre, dret]` en **dBFS**: el pic més alt des de l'esdeveniment anterior (uns 40 ms). `-90` vol dir silenci.
- Reproductors: el que surt, **després del volum**. Enregistradors: el que entra, també quan no s'enregistra (si `inputActive`).
- Una entrada mono dona el mateix valor als dos canals.
- La interfície fa la caiguda suau, la retenció del pic i l'indicador de saturació.

## 6. Previst (no implementat)

- Reproductors: llistes (fase 2)
- Enregistradors: desar, descartar, editar, recents, recuperació (fase 2)
- `cue.*` (preescolta, fase 2)
