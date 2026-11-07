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
| `bad_json` | El missatge no és JSON vàlid, no és un objecte o té camps repetits (p. ex. dos `id`) |
| `protocol_version` | El camp `v` falta o no és compatible |
| `missing_cmd` | Falta el camp `cmd` |
| `bad_params` | Falta un paràmetre o té un valor no vàlid (p. ex. un reproductor que no existeix) |
| `bad_state` | L'ordre no es pot fer en l'estat actual (p. ex. Play sense pista carregada). No canvia res |
| `player_error` | L'ordre s'ha fet, però el reproductor ha quedat en estat `error` (dispositiu no disponible, fitxer il·legible). El missatge n'explica el motiu |
| `recorder_error` | L'ordre s'ha fet, però l'enregistrador ha quedat en estat `error` (entrada no disponible, carpeta sense permís...). El missatge n'explica el motiu |
| `recording_active` | `engine.shutdown` sense `force`, amb enregistraments en curs. No s'atura res |
| `file_error` | No s'ha pogut llegir o escriure un fitxer de llista (no existeix, és malmès, no hi ha permís...). El missatge n'explica el motiu |
| `file_exists` | `editor.save`: el fitxer de destinació ja existeix. No s'ha desat res; cal confirmar-ho i tornar-ho a enviar amb `overwrite: true` |
| `profile_exists` | `profile.import`: ja hi ha un perfil amb el nom del que s'importa. No s'ha importat res; es pot tornar a enviar amb un altre `name` |
| `unknown_command` | L'ordre no existeix |
| `internal` | Error inesperat del motor (el motor continua funcionant) |

## 4. Ordres

| Ordre | Paràmetres | Resultat | Des de |
|---|---|---|---|
| `engine.hello` | — | `{ name, version, protocol }` | fase 0 |
| `engine.ping` | — | `{ pong: true, time }` | fase 0 |
| `state.get` | — | `{ engine: { version, uptime, clients }, players: [ … ], recorders: [ … ], recordings: [ … ], playlists: [ … ], cue: { … }, hardware: { … } }` (vegeu 4.2 a 4.8) | fase 0 (`players`, `recorders`, `hardware`: fase 1; `playlists`, `recordings`, `cue`: fase 2) |
| `engine.shutdown` | `force` (opcional, `true`) | `{ stopping: true }` i el motor es tanca. Si hi ha enregistraments en curs, es rebutja amb `recording_active`, tret que s'enviï `force: true`: llavors es tanquen correctament (fitxers vàlids, sense marcador `.rec`) abans d'aturar-se | fase 0 (`force`: fase 1) |
| `devices.list` | `kind`: `"output"`, `"input"` o `"all"` (per defecte); `includeInactive`: `true` per incloure també els desactivats i desconnectats (per defecte, `false`) | `{ devices: [ … ] }` (vegeu 4.1) | fase 1 |
| `hardware.report` | — | `{ report: { … } }`: resum de l'arrencada (vegeu 4.4) | fase 1 |
| `settings.get` | — | `{ settings: { profile: { id, name }, ui: { zoom, recordersPerRow, endWarningSeconds, hideIdleRecorderMeters, playlistRowHeight, minPlaylistRowHeight, maxPlaylistRowHeight, playerOrder, recorderOrder }, layout: { players, recorders }, program: { name }, folders: { history, logs }, restartNeeded } }`: ajustos desats a `config.json` i al perfil actiu (vegeu 4.9) | fase 2 (`layout`, `recordersPerRow`, `endWarningSeconds`, `restartNeeded`: pas 9; `program`: pas 10); `profile`: fase 4 |
| `settings.set` | `ui`, `layout`, `program`, `trim` (`{ "thresholdDb": -60, "leadMs": 10, "tailMs": 50 }`: llindar −40, −50, −60, −70 o −80; marges de 0 a 2.000 ms), `fadeOut` (`{ "ms": 2000 }`: de 500 a 10.000), `recorderLimiter` (`{ "ceilingDb": -1 }`, de −6 a 0: sostre del limitador dels enregistradors; s'aplica de seguida, també a mig enregistrament), `recents` (`{ "clearOnExit": true }`), `recordingArchive` (`{ "enabled": true, "folder": "E:\\Historial", "keepDays": 30 }`: historial d'enregistraments; `folder` `""` = la carpeta per defecte; `keepDays` de 0 = no esborrar mai a 3.650; s'aplica a partir del pròxim enregistrament) i/o `loudness` (`{ "targetLufs": -12 }`, de −24 a −6; s'aplica a tots els reproductors a partir de la pista següent): només els ajustos a canviar, p. ex. `{ "ui": { "zoom": 1.25 } }` (vegeu 4.9) | `{ settings }` amb tots els ajustos. Es desa de seguida. Si algun valor és fora de marge, `bad_params` i no es canvia res | fase 2 |

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

Per defecte hi ha dos reproductors, `"1"` i `"2"`; se'n poden configurar fins a 6 (`settings.set`, `layout.players`; s'aplica en arrencar el motor, vegeu 4.9). Cada un té la seva llista (vegeu 4.5).

| Ordre | Paràmetres | Estats en què es pot fer | Resultat |
|---|---|---|---|
| `player.setDevice` | `player`, `deviceId` | tots menys `playing`. Si hi ha pista preparada, es torna a preparar: des del mateix punt si era a `paused` o `error` | `{ player }` |
| `player.play` | `player` | `ready` i `paused`: continua la pista actual. `empty` i `ended`: fa sonar la pista amb **cue** o, si no n'hi ha, la que va **després de l'última reproduïda** (la primera, si no n'ha sonat cap). Si no n'hi ha cap més, `bad_state` | `{ player }` |
| `player.playItem` | `player`, `itemId` | tots menys `error`. Doble clic: atura el que sona i fa sonar aquesta pista des de l'inici. La que sonava queda com a última reproduïda | `{ player }` |
| `player.pause` | `player` | `playing` | `{ player }` |
| `player.stop` | `player` | tots menys `error`. **Atura la llista**: passa a `ended` i la pista queda com a última reproduïda (`lastPlayedId`) | `{ player }` |
| `player.fadeOut` | `player` | `playing`. **Fade-out** (fase 5, D-107): el so baixa durant `settings.fadeOut.ms` (constant en dB, fins a −60 dB) i després el reproductor s'atura com amb `player.stop` (`ended`, la pista com a última reproduïda). Mentre baixa, `fadingOut` és `true`. Si ja n'hi ha un en marxa, no fa res. Es treu sol si el reproductor deixa de sonar (pausa, Stop, salt) o si es fa sonar una altra pista; si la pista s'acaba a mig fade-out, no encadena amb la següent | `{ player }` |
| `player.load` | `player`, `path` (ruta completa) | tots menys `playing`. Afegeix el fitxer al final de la llista i el deixa preparat (`ready`). Només per a la consola de proves: la interfície fa servir `playlist.add` | `{ player }` |
| `player.retry` | `player` | `error`. Torna a obrir la sortida i el fitxer i queda `ready` **a la mateixa posició**. No torna a sonar sol: cal `player.play` | `{ player }` |
| `player.seek` | `player`, `position` (segons des de l'inici) | `ready`, `playing`, `paused`, `ended`. Salta a aquest punt (si és més enllà del final, es queda al final). Si sonava, **continua sonant** des d'allà, amb un silenci molt curt mentre es torna a obrir la sortida; si era en pausa, continua en pausa; si havia acabat, queda `ready`. Tornar a començar la pista és `position: 0` | `{ player }` |
| `player.setCrossfade` | `player`, `ms` (de 0 a 10.000; 0 = sense fos) | tots. Fos entre pistes que s'encadenen soles (avanç automàtic): els últims `ms` de la pista se sobreposen amb el principi de la següent (corbes d'igual potència). També quan es fa sonar una altra pista a mà (`player.playItem`) mentre en sona una: la que sonava baixa mentre la nova puja, sense aturar la sortida (D-078); si la nova no es pot obrir, la que sona continua i es respon amb l'error. No s'aplica als bucles; si no ve cap pista, la que sona acaba sencera. En l'avanç automàtic, el canvi de durada s'aplica a partir de la pista següent. Es desa al perfil (`playerCrossfade`). Fora de marge: `bad_params` (fase 5, D-077) | `{ player }` |
| `player.setTrim` | `player`, `enabled` (true/false) | tots. Retall automàtic de silencis (fase 5, D-079, D-080): cada pista comença al primer so (menys `settings.trim.leadMs`) i s'acaba a l'últim (més `tailMs`), segons el llindar `settings.trim.thresholdDb`. El fitxer no es toca. Un salt (`playItem` amb `startAt`) es respecta, però s'acaba igualment a l'últim so. Les pistes sense anàlisi encara, tot silenci o amb soroll de fons per sobre del llindar sonen senceres. També amb l'encadenament i el fos. S'aplica a partir de la pista següent. Es desa al perfil | `{ player }` |
| `player.setNormalization` | `player`, `enabled` (true/false) | tots. S'aplica **a partir de la pista següent** (la que sona no canvia de volum). Es desa al perfil | `{ player }` |
| `player.setProcessing` | `player`; `processing` (els camps que es volen canviar, vegeu 4.13) **o** `preset` (l'`id` d'un preset de `settings.processingPresets`) | tots, també mentre sona: s'aplica de seguida i sense clics. Es desa al perfil (`playerProcessing` a `perfil.json`) i es conserva d'una pista a l'altra. Preset desconegut, reproductor que no existeix o sense `processing` ni `preset`: `bad_params` | `{ player }` |
| `player.setVolume` | `player`, `db` (de `-60` a `0`) | tots, també mentre sona. A `-60` el reproductor queda en silenci; fora de marge es queda al límit. El canvi es fa amb una rampa curta (sense clics). En arrencar el motor, tots els reproductors són a 0 dB (no es desa) | `{ player }` |

Objecte `player` (també a `state.get` i a l'esdeveniment `player.state`):

```json
{ "id": "1", "state": "playing",
  "deviceId": "{0.0.0.00000000}.{f2d6376b-…}",
  "file": "D:\\Musica\\prova.mp3",
  "duration": 183.512, "position": 12.300,
  "error": null, "underruns": 0, "volumeDb": 0,
  "currentItemId": "t3", "lastPlayedId": "t2", "format": "MP3 192 kbps estèreo 44,1 kHz" }
```

- `state`: `empty` (encara no ha sonat res), `ready` (preparat), `playing`, `paused`, `ended` (la llista s'ha aturat: la pista s'ha acabat o s'ha premut Stop. S'envia uns 200–300 ms després de l'última mostra, quan ja ha sortit tota de la targeta), `error`.
- `currentItemId`: pista de la llista que sona (o preparada, en pausa, en error). `lastPlayedId`: última que ha sonat (es marca en blau). `format`: format abreujat de la pista actual, quan ja s'ha comprovat.
- Quan una pista s'encadena amb la següent (bucle o avanç automàtic), arriba un `player.state` amb el `currentItemId` nou en el moment que comença a sonar.
- `duration` i `position`: en segons, amb tres decimals.
- `error`: el motiu, en català, quan `state` és `error`. La pista i la posició es conserven. Per continuar: `player.retry` (mateixa sortida) o `player.setDevice` (una altra), i després `player.play`. El reproductor **no reprèn mai sol**. El detall tècnic de Windows no s'envia: només queda al registre del motor.
- `underruns`: talls des que es va obrir el reproductor (la targeta ha demanat so i no n'hi havia de preparat). Ha de ser 0; serveix per a les proves llargues.
- `volumeDb`: volum del fader (de `-60`, silenci, a `0`).
- `processing`: el compressor/limitador del reproductor (vegeu 4.13).
- `crossfadeMs`: fos entre pistes encadenades, en ms (0 = cap; `player.setCrossfade`).
- `fadingOut`: hi ha un fade-out en marxa (`player.fadeOut`); en acabar, `state` passa a `ended`.
- `trim`: `{ enabled, thresholdDb, leadMs, tailMs }`: retall de silencis (`player.setTrim`; el llindar i els marges són de `settings.trim`). Amb el retall, `position` i `duration` continuen sent temps del fitxer: la pista comença a sonar al primer so (p. ex. `position` 0,49) i `duration` és on s'acaba de sonar (l'últim so més el marge), de manera que el temps restant arriba a zero quan s'acaba el so.
- `normalization`: `{ enabled, targetLufs, gainDb }`: normalització de sonoritat (vegeu 4.14). `gainDb` és el guany aplicat a la pista que sona (`null` si no se n'aplica cap: desactivada, o la pista encara no s'havia mesurat quan va començar).

### 4.3. Enregistradors

Per defecte hi ha dos enregistradors, `"1"` i `"2"`; se'n poden configurar fins a 6 (`layout.recorders`; fins a 4 abans de D-101, en arrencar el motor, vegeu 4.9). Desar i Descartar són a 4.6; Editar, a la fase 3.

| Ordre | Paràmetres | Estats en què es pot fer | Resultat |
|---|---|---|---|
| `recorder.setDevice` | `recorder`, `deviceId` (una **entrada**) | tots menys `recording` | `{ recorder }` |
| `recorder.start` | `recorder`; opcionals, **només per a aquest enregistrament**: `folder` (ruta completa) i `name` (per al nom del fitxer). Per defecte, els de l'enregistrador (`recorder.configure`) | `idle`, `pending`, `error`. Comença de seguida, sense preguntar res; l'enregistrament pendent passa a recents (4.6). **Mai no sobreescriu cap fitxer** | `{ recorder }` |
| `recorder.configure` | `recorder`; `name` i/o `folder` (ruta completa). Un `name` buit torna al de per defecte (`Enregistrador N`) | tots. S'aplica a partir del pròxim enregistrament. Es desa a `config.json` | `{ recorder }` |
| `recorder.setGain` | `recorder`, `db`: volum d'enregistrament de `-60` (silenci) a `+12`; per defecte `0`. Fora de marge, es queda al límit | tots. S'aplica de seguida (també a mig enregistrament), amb una rampa curta sense clics. El que passa de 0 dBFS es retalla (si el limitador és activat, abans es limita) | `{ recorder }` |
| `recorder.setLimiter` | `recorder`, `enabled` (true/false) | tots. Limitador del que es desa al fitxer (fase 5, D-083), després del volum: el so no passa del sostre `settings.recorderLimiter.ceilingDb` (per defecte −1 dB). Mira 1,5 ms endavant i abaixa just abans d'un cop, sense retallar-lo; per sota del sostre no toca res (sense compressió). S'aplica de seguida, també a mig enregistrament (transició de 10 ms, sense clics). Desactivat per defecte. Es desa al perfil | `{ recorder }` |
| `recorder.stop` | `recorder` | `recording`. La interfície **sempre** ha de demanar confirmació abans d'enviar-la | `{ recorder }` |
| `recorder.mark` | `recorder` | `recording`. **Marca** (fase 5, D-110): afegeix una marca a la posició d'ara de l'enregistrament (precisió d'un fotograma MP3) i desa totes les marques a `Nom.mp3.marques`, al costat del fitxer (i de la còpia de l'historial). Si el fitxer de marques no es pot escriure, la marca es queda a la memòria, es respon `bad_state` amb el motiu i es torna a provar amb la marca següent. L'enregistrament no es toca mai | `{ recorder }` |
| `recorder.nameMark` | `recorder`, `number` (1, 2...) i `name` (text; `""` o `null` = sense nom; com a molt 60 caràcters, sense espais als extrems) | `recording` o `pending`. **Nom d'una marca** (fase 5, D-114). Es desa de seguida al fitxer de marques. `bad_state` si no hi ha aquesta marca o el nom és massa llarg; `bad_params` si `name` no és un text | `{ recorder }` |

Fitxer: `<carpeta>\<nom>_AAAA-MM-DD_HH-MM-SS.mp3` (si ja existeix, s'hi afegeix `_2`, `_3`...). MP3 320 kbps CBR estèreo, a la freqüència de l'entrada. Mentre s'enregistra hi ha un marcador `<fitxer>.mp3.rec` (JSON) que s'esborra en aturar correctament.

Objecte `recorder` (també a `state.get` i a l'esdeveniment `recorder.state`):

```json
{ "id": "1", "state": "recording",
  "name": "Programa", "folder": "D:\\Enregistraments", "gainDb": 0,
  "deviceId": "{0.0.1.00000000}.{1942b38d-…}",
  "file": "C:\\Users\\…\\Enregistraments\\Programa_2026-11-02_17-30-00.mp3",
  "startedAt": "2026-11-02T17:30:00.123+01:00",
  "elapsed": 125.4, "fileSize": 5017600, "freeDiskBytes": 146028888064, "lowDiskSpace": false,
  "sampleRate": 48000, "bitrateKbps": 320,
  "samplesLost": 0, "captureGlitches": 0, "inputActive": true,
  "error": null, "marks": [ { "seconds": 62.016, "name": null } ] }
```

- `state`: `idle` (inactiu), `recording`, `pending` (fitxer acabat, a `file`), `error`.
- `name`, `folder`, `gainDb`: vegeu `recorder.configure` i `recorder.setGain`. El mesurador (5.2) ja mostra el nivell **amb el volum aplicat** i, si el limitador és activat, sense passar del seu sostre: el que queda al fitxer (D-085).
- `limiter`: `{ enabled, ceilingDb }`: limitador (`recorder.setLimiter`; el sostre és de `settings.recorderLimiter`).
- `archive`: `{ path, error, errorDetail }`: còpia d'historial de l'enregistrament en curs o de l'últim (fase 5, D-086). `path` és null si no n'hi ha. `error` diu per què no s'ha pogut crear o s'ha deixat d'escriure (disc ple, unitat desconnectada...); l'enregistrament continua igualment i la còpia és vàlida fins on arriba. `error` és un text curt per a l'operador; `errorDetail`, la carpeta i el missatge de Windows.
- `elapsed`: segons enregistrats. `fileSize`: bytes escrits. `freeDiskBytes`: espai lliure a la unitat del fitxer mentre s'enregistra, i a la de `folder` quan no (`null` si no se sap, p. ex. en una unitat de xarxa). `lowDiskSpace`: queda menys de l'equivalent a 2 hores d'enregistrament (uns 288 MB a 320 kbps).
- `samplesLost`: mostres perdudes perquè el disc no ha anat prou ràpid. `captureGlitches`: vegades que Windows ha avisat que s'han perdut dades de captura. Tots dos han de ser 0.
- `live`: el fitxer obert s'està enregistrant (fase 5, D-117). L'editor en té una **fotografia** (fins on era en obrir-lo o en fer `editor.refresh`) que només es llegeix: es pot escoltar (`editor.play`), seleccionar i desar trossos (`editor.saveSelection`); les marques es poden moure (`editor.moveMark`, a través de l'enregistrador); `editor.delete`, `keep`, `fadeIn`, `fadeOut` i `save` responen `bad_state`.
- `savedSelectionPath`: on s'ha desat l'última selecció (`editor.saveSelection`; fase 5, D-113). Es neteja amb l'ordre següent que canvia l'editor.
- `marks`: marques de l'enregistrament en curs o del pendent (`recorder.mark`; fase 5, D-110), per ordre. `seconds` des del principi de l'enregistrament; `name`: el nom (`recorder.nameMark`) o `null`. Es buida en començar-ne un altre o en desar o descartar el pendent.
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
- `elements`: només els elements que tenien un dispositiu desat. `element` és `player`, `recorder` o `cue` (la preescolta, sempre amb `id: "1"`). `device` és el dispositiu trobat ara (format de 4.1) o `null`.
- `newDevices`: dispositius actius que no hi eren l'última vegada i no són de cap element. `formatChanges`: dispositius amb el format canviat a Windows.
- El motor **mai no assigna un dispositiu diferent** del que s'havia triat (D-016). El resum és el de l'arrencada: no canvia fins que el motor es reinicia.

### 4.5. Llistes de reproducció

Cada reproductor té una llista. Viu al motor: la interfície la mostra i hi envia ordres. Cada pista té un `itemId` propi que no canvia encara que es mogui. Regles completes: D-031.

| Ordre | Paràmetres | Resultat |
|---|---|---|
| `playlist.get` | `player` | `{ playlist }` |
| `playlist.add` | `player`, `paths` (llista de rutes completes); opcional `beforeId` (s'insereixen davant d'aquesta pista; per defecte, al final) | `{ ids }`: els `itemId` nous. Si un dels fitxers és una llista (`.obpl`, `.m3u`, `.m3u8`), s'hi afegeixen les seves pistes (p. ex. en arrossegar una llista a sobre), en ordre. Si és una carpeta, s'hi afegeixen els seus fitxers d'àudio i els de les subcarpetes, en l'ordre de l'Explorador (per nom, «2» abans de «10»; primer els fitxers i després cada subcarpeta); se salten els ocults i les llistes que hi hagi a dins. Cada pista es comprova en segon pla (`status: "analyzing"` → `ok` o `error`) |
| `playlist.move` | `player`, `ids` (es mouen en l'ordre de la llista); opcional `beforeId` (per defecte, al final). Es pot moure la pista que sona | `{ playlist }` |
| `playlist.remove` | `player`, `ids`. Si s'esborra la pista que sona, acaba igualment | `{ playlist }` |
| `playlist.setFlags` | `player`, `itemId`; opcionals `loop`, `autoNext`, `cue` (`true`/`false`; els que no s'envien no canvien). Bucle i avanç automàtic no poden anar junts: activar `loop` apaga `autoNext`, i `autoNext: true` en una pista amb bucle dona `bad_state` (D-034) | `{ playlist }` |
| `playlist.duplicate` | `player`, `ids`. Cada pista es duplica just a sota de l'original (amb les marques de bucle i avanç automàtic; el cue no) | `{ ids }`: les còpies |
| `playlist.transfer` | `player` (origen), `ids`, `toPlayer` (destí; pot ser el mateix); opcionals `beforeId` (de la llista de destí; per defecte, al final) i `copy` (`true`: copia; per defecte es **mou**). Conserva bucle, avanç automàtic i el resultat de la comprovació. És el que fa arrossegar pistes d'una llista a una altra (amb Ctrl, copiar) | `{ ids }`: les pistes a la llista de destí |
| `playlist.peek` | `path` (ruta completa d'un `.obpl`, `.m3u` o `.m3u8`) | `{ items: [{ section, color } \| { path, exists }], tracks, missing }`: el contingut d'una llista desada **sense carregar-la enlloc** (pestanya Llistes del panell lateral, D-065). Si no es pot llegir: `file_error` |
| `playlist.load` | `player`, `path` (ruta completa d'un `.obpl`, `.m3u` o `.m3u8`); opcional `replace` (per defecte `true`: substitueix la llista; `false`: afegeix al final). La pista que sona continua sonant | `{ ids, missing }`: les pistes noves i quantes no existeixen |
| `playlist.save` | `player`, `path` (ruta completa; si no acaba en `.obpl`, s'hi afegeix) | `{ path, count }` |
| `playlist.addSection` | `player`, `title` (nom de la secció, com a molt 80 caràcters); opcionals `color` (`red`, `orange`, `yellow`, `green`, `teal`, `blue`, `purple` o `pink`) i `beforeId` (per defecte, al final). Separador de secció (D-042) | `{ id }` |
| `playlist.updateSection` | `player`, `itemId` (un separador); opcionals `title`, `color` (`null` el treu) i `collapsed` (`true`/`false`: la interfície n'amaga les pistes fins al separador següent). Els que no s'envien no canvien. Amb una pista, `bad_state` | `{ playlist }` |
| `track.info` | `path` | `{ info: { path, fileSize, duration, format, title, artist, album, year, genre, comment } }` (metadades amb TagLib#; els camps que no hi són, `null`) |

Objecte `playlist` (també a `state.get` → `playlists` i a l'esdeveniment `playlist.changed`):

```json
{ "player": "1", "cueId": "t4", "totalDuration": 612.4,
  "items": [
    { "id": "t3", "name": "Sintonia", "path": "D:\\Musica\\Sintonia.mp3",
      "duration": 32.5, "format": "MP3 320 kbps estèreo 44,1 kHz",
      "status": "ok", "problem": null, "loop": false, "autoNext": true,
      "type": "track", "color": null, "collapsed": false },
    { "id": "t5", "type": "section", "name": "ESPORTS", "path": "", "duration": null,
      "format": null, "status": "ok", "problem": null, "loop": false, "autoNext": false,
      "color": "blue", "collapsed": false }
  ] }
```

- `name`: nom del fitxer sense extensió (no es fan servir les metadades).
- `type`: `track` (una pista) o `section` (**separador de secció**, D-042). Un separador no és cap fitxer (`path` buit) i **no sona mai**: `name` és el títol, `color` el seu color (o `null`) i `collapsed` si la interfície n'ha d'amagar les pistes. Play, el cue i l'avanç automàtic se'l salten (l'avanç continua amb la primera pista de després); no pot tenir cue, bucle ni avanç (`bad_state`); `player.playItem` amb un separador dona `bad_state`. Es desa a les llistes `.obpl` i es mou, s'esborra, es duplica i es copia com una pista. Un fitxer el nom del qual és només guions baixos o guions (p. ex. `______.mp3`, el separador del Winamp) s'afegeix com a separador amb el títol «Secció», també des d'un M3U.
- `loudness`: sonoritat de la pista en LUFS (fase 4, 4.14), quan ja s'ha mesurat; `null` mentrestant (o si és silenci).
- `sound`: on hi ha so, per a cada llindar del retall de silencis: `[[llindar, inici, final], ...]` en segons (fase 5). `null` si encara no s'ha analitzat; sense el llindar si aquest és silenci. Els espetecs aïllats (menys de 20 ms de so amb 500 ms de silenci al voltant) no compten (D-082).
- `status`: `analyzing` (s'està comprovant), `ok` (sonarà), `error` (malmès o format no compatible) o `missing` (el fitxer ja no hi és). `problem`: el motiu, en català.
- **Comprovació:** en afegir una pista, el motor l'obre amb el mateix descodificador que el reproductor i en llegeix el principi. **Si una pista és `ok`, sonarà.** Cada 30 s es comprova que els fitxers encara existeixen (sense obrir-los); si un que faltava torna, es torna a comprovar.
- `totalDuration`: suma de les durades conegudes.
- **Avanç** (D-031): per defecte, manual (quan s'acaba una pista, la llista s'atura). `loop`: la pista es repeteix fins a Stop. `autoNext`: quan s'acaba, sona la del cue o, si no n'hi ha, la següent (**un sol pas**).  Quan ja ha passat a la següent, l'`autoNext` d'aquella pista es desmarca sol (D-033). `cueId`: només n'hi pot haver una; s'apaga quan comença a sonar. Amb bucle o avanç automàtic **no hi ha silenci** entre pistes: el motor decideix què ve després **1 s abans** del final.

### 4.6. Enregistraments acabats: pendents i recents (D-036)

Cada enregistrament que s'acaba (aturat, entrada perduda, motor que s'atura) entra a una llista. Si un enregistrador és a `pending` i el seu `file` és el `path` d'un element, és el seu **pendent** (la interfície el mostra al panell de l'enregistrador); la resta són els **recents**. Com a molt n'hi ha 30 (els més antics en surten, però els fitxers no s'esborren; els pendents no en surten mai). La llista es desa i es recupera en arrencar.

| Ordre | Paràmetres | Resultat |
|---|---|---|
| `recording.save` | `recordingId`, `path` (ruta completa; si no acaba en `.mp3`, s'hi afegeix). **Mai no sobreescriu**: si ja existeix, s'hi afegeix `_2`, `_3`... Mou el fitxer: al mateix disc és instantani; a un altre, es copia en segon pla (`busy: true` fins que acaba). Si era el pendent d'un enregistrador, aquest torna a `idle` de seguida. Amb la mateixa ruta on ja és, no es mou res: només queda desat, amb la durada i la mida del fitxer d'ara (p. ex. si l'editor l'ha substituït per la versió editada; D-103) | `{ path, recording }`: on quedarà |
| `recording.saveEdited` | `recordingId`, `path`: ruta completa del fitxer on l'editor acaba de desar la versió editada (fase 5, D-102) | `{ recording, original: { path, recycled, kept } }`. L'enregistrament passa a ser aquest fitxer i queda desat (`saved`; la durada i la mida, les del fitxer nou); si era el pendent d'un enregistrador, aquest torna a `idle`. L'original sense editar va a la **paperera** només si n'hi ha una còpia sencera a l'historial d'enregistraments (`recycled: true`); si no, es queda on és i `kept` diu per què. Si el fitxer és el mateix que l'original o no hi és, error i no canvia res |
| `recording.discard` | `recordingId`. Envia el fitxer a la **paperera de Windows** (en una unitat de xarxa, Windows l'esborra del tot) i el treu de la llista. Si era el pendent, l'enregistrador torna a `idle`. La interfície ha de demanar confirmació | `{ discarded }` |
| `recording.remove` | `recordingId`. El treu de la llista; el fitxer no es toca. No es pot amb el pendent d'un enregistrador (`bad_state`: s'ha de desar o descartar) | `{ removed }` |
| `recordings.clear` | opcional `includeUnsaved` (per defecte `false`). Neteja Recents (fase 5, D-089): treu els desats i els que ja no es troben; amb `includeUnsaved: true`, també els no desats. Els fitxers **no es toquen**. El pendent de cada enregistrador i els que s'estan desant es queden sempre | `{ removed, unsavedLeft }`: quants n'ha tret i quants de no desats hi queden |

Amb un element que s'està desant (`busy`), les tres donen `bad_state`.

Element (a `state.get` → `recordings` i a l'esdeveniment `recordings.changed`; el més recent primer):

```json
{ "id": "e3", "path": "D:\\Enregistraments\\Programa_2026-11-04_10-00-00.mp3",
  "recorder": "1", "startedAt": "2026-11-04T10:00:00.000+01:00",
  "duration": 3725.4, "fileSize": 149016000,
  "saved": false, "busy": false, "closedCleanly": true, "recovered": false,
  "exists": true, "problem": null }
```

- `id`: canvia en reiniciar el motor.
- `saved`: ja s'ha desat (`path` és la ruta nova). `busy`: s'està movent.
- `closedCleanly`: `false` si l'enregistrament es va tallar (entrada perduda, disc ple) o es va interrompre.
- `recovered` (des del pas 7, D-038): s'ha recuperat en arrencar el motor (vegeu 4.7). Sempre amb `closedCleanly: false`.
- `exists`: el fitxer encara hi és. `problem`: l'últim error en desar-lo (p. ex. sense permís a la carpeta), en català.

### 4.7. Recuperació d'enregistraments interromputs (D-038)

No hi ha cap ordre: el motor ho fa sol **en arrencar**, abans d'acceptar connexions. Busca marcadors `*.mp3.rec` a la carpeta de treball de cada enregistrador, a la de per defecte i a les carpetes dels enregistraments de la llista. Per a cada un: retalla el fitxer fins a l'últim fotograma MP3 complet, en treu l'etiqueta Xing/Info si n'hi ha, esborra el marcador i l'afegeix a la llista amb `recovered: true` (si ja hi era, s'hi actualitzen la durada i la mida). No toca mai un fitxer que un altre procés té obert, ni un fitxer on no trobi cap fotograma MP3 (en aquest cas el marcador es manté). Els resultats s'escriuen al registre del motor.

La utilitat `OnaBages.Recovery.exe` fa el mateix sense el motor: `OnaBages.Recovery [carpeta | fitxer.mp3 | fitxer.mp3.rec]` (sense res, la carpeta per defecte). No toca la llista de recents.

### 4.8. Preescolta (D-039)

Escoltar una pista per una sortida pròpia (auriculars) sense que surti a antena (especificació 7). N'hi ha una. **No toca cap reproductor ni cap llista.** Quan la pista s'acaba, s'atura.

| Ordre | Paràmetres | Resultat |
|---|---|---|
| `cue.play` | Opcionals: `path` (ruta completa; sense, torna a fer sonar l'última) i `position` (segons on comença; per defecte 0). Si ja en sonava una altra, la substitueix | `{ cue }` |
| `cue.stop` | — (si no sona, no fa res). La pista es recorda: `cue.play` sense `path` hi torna | `{ cue }` |
| `cue.seek` | `position` (segons). Si no sona, la fa sonar des d'allà (botons de salt amb la preescolta aturada) | `{ cue }` |
| `cue.setVolume` | `db`: de `-60` (silenci) a `0`. Es desa a `config.json` | `{ cue }` |
| `cue.setDevice` | `deviceId` (una **sortida**). Si sonava, s'atura. Es desa a `config.json` i es revisa en arrencar (4.4) | `{ cue }` |

Sense sortida, `cue.play` dona `bad_state`. Si el fitxer no es pot llegir o la sortida falla, `player_error` amb el motiu (i `state: "error"`).

Objecte `cue` (a `state.get` i a l'esdeveniment `cue.state`):

```json
{ "state": "playing", "deviceId": "{0.0.0.00000000}.{ef4a…}",
  "file": "D:\\Musica\\Pista.mp3", "duration": 213.7, "position": 12.4,
  "volumeDb": -6, "format": "MP3 320 kbps estèreo 44,1 kHz", "error": null,
  "sharesPlayerOutput": false }
```

- `state`: `stopped`, `playing` o `error`. No hi ha pausa.
- `file` i `duration`: la pista que sona o l'última que ha sonat (per tornar-hi i per als botons de salt). `position` és 0 si no sona.
- `sharesPlayerOutput`: la preescolta té la mateixa sortida que algun reproductor: **el que s'hi escolti sortirà a antena** (D-015). La interfície ho ha de dir de manera permanent.

### 4.9. Ajustos (D-041)

Objecte `settings` (resultat de `settings.get` i `settings.set`):

```json
{ "profile": { "id": "La Central", "name": "La Central" },
  "ui": { "zoom": 1.0, "recordersPerRow": 2, "endWarningSeconds": 10, "hideIdleRecorderMeters": false,
          "playlistRowHeight": 100, "minPlaylistRowHeight": 100, "maxPlaylistRowHeight": 200,
          "playerOrder": ["2", "1"], "recorderOrder": [] },
  "layout": { "players": 2, "recorders": 4 },
  "program": { "name": "Magazín dels dissabtes" },
  "folders": { "history": "C:\\Users\\...\\AppData\\Roaming\\OnaBages\\historial",
               "logs": "C:\\Users\\...\\AppData\\Roaming\\OnaBages\\logs" },
  "restartNeeded": true }
```

| Camp | Valors | Què fa |
|---|---|---|
| `profile` | només es llegeix | Perfil actiu (fase 4, D-057): `id` és el nom de la seva carpeta (`perfils\<id>\`) i `name`, el nom que es mostra |
| `ui.zoom` | de `0.5` a `1.5` (per defecte `1`) | Escala de la interfície. Per sota de `0.7` la lletra no es fa més petita |
| `ui.recordersPerRow` | enter de `1` a `6` (per defecte `2`) | Enregistradors per fila a la interfície |
| `ui.endWarningSeconds` | enter de `0` a `60` (per defecte `10`; `0` = sense avís) | Segons abans del final en què el temps restant d'un reproductor canvia d'aspecte |
| `ui.hideIdleRecorderMeters` | `true` / `false` (per defecte `false`) | Amagar el mesurador dels enregistradors que no enregistren (fase 5, D-100). Del perfil |
| `ui.playlistRowHeight` | de `100` a `200` (per defecte `100`) | Alçada de les files de les llistes dels reproductors, en % de la normal (fase 5, D-108). Només la interfície la fa servir. Del perfil. `ui.minPlaylistRowHeight` i `ui.maxPlaylistRowHeight` només es llegeixen |
| `ui.playerOrder` | llista d'identificadors, p. ex. `["2", "1"]` (per defecte `[]` = per número) | Ordre dels reproductors a la pantalla, d'esquerra a dreta (fase 5, D-105). Del perfil; s'aplica de seguida. Cada identificador de `"1"` a `"6"`, sense repetir-ne cap; els que no hi surten van al final, per número |
| `ui.recorderOrder` | com `ui.playerOrder` | Ordre dels enregistradors a la graella (fase 5, D-105) |
| `layout.players` | enter de `1` a `6` (per defecte `2`) | Nombre de reproductors |
| `layout.recorders` | enter de `1` a `6` (per defecte `2`) | Nombre d'enregistradors |
| `program.name` | text de com a molt 60 caràcters (per defecte `""`; `""` el treu) | Nom del programa: surt a la barra superior i a l'historial de reproducció (D-043). Els espais repetits i els salts de línia es treuen |
| `recordingArchive` | `enabled` (per defecte `true`), `folder` (ruta completa; `""` = la per defecte), `keepDays` (de `0` a `3650`; per defecte `30`; `0` = no esborrar mai). També inclou `defaultFolder` i `maxKeepDays` (només es llegeixen); `folder` és sempre la carpeta que es fa servir | Historial d'enregistraments (fase 5, D-086): el motor escriu una còpia de cada enregistrament original a `folder\<nom del perfil>\AAAA-MM-DD\` (D-088; si es canvia el nom del perfil actiu, les còpies següents van a la carpeta amb el nom nou), alhora que el fitxer de treball. Res no toca la còpia (desar, descartar, l'editor). En arrencar, s'esborren els MP3 de les carpetes de dia (de tots els perfils) de fa més de `keepDays` dies (mai el dia d'avui ni un fitxer obert). És de l'**ordinador** (`config.json`), perquè depèn dels discos. S'aplica a partir del pròxim enregistrament |
| `fadeOut.ms` | de `500` a `10000` (per defecte `2000`) | Durada del fade-out dels reproductors (`player.fadeOut`; fase 5, D-107). Del perfil. `fadeOut.minMs` i `fadeOut.maxMs` només es llegeixen |
| `recents.clearOnExit` | `true`/`false` (per defecte `false`) | En tancar l'aplicació, es treuen de Recents els desats i els no trobats (els no desats es queden). Es fa en tornar a arrencar el motor, abans que la interfície vegi la llista: així també es fa si l'ordinador va caure. És de l'**ordinador** (`config.json`), com la llista |
| `recorderLimiter.ceilingDb` | de `-6` a `0`, amb un decimal (per defecte `-1`) | Sostre del limitador dels enregistradors (fase 5, D-083), comú a tots; cada un l'activa amb `recorder.setLimiter`. També inclou `minCeilingDb` i `maxCeilingDb` (només es llegeixen) |

- **On es desa cada cosa (fase 4, D-057):** `ui.zoom`, `recents` i `recordingArchive` són de l'ordinador (`config.json`); la resta (`ui.recordersPerRow`, `ui.endWarningSeconds`, `ui.hideIdleRecorderMeters`, `ui.playlistRowHeight`, `layout`, `program`), com també els dispositius dels reproductors, enregistradors i preescolta, els ajustos dels enregistradors i les últimes llistes, és del **perfil actiu** (`perfils\<id>\perfil.json` i `perfils\<id>\llistes\`). On aquest document diu que alguna cosa «es desa a `config.json`», vol dir a la configuració: al fitxer que li toca segons aquesta regla.
- `folders` només es llegeix (no es pot canviar amb `settings.set`): carpetes de l'historial de reproducció (D-043) i dels registres del motor (D-045), perquè la interfície les pugui obrir.
- `layout` **s'aplica en tornar a arrencar el motor** (mai amb el motor en marxa, perquè no s'aturi res que soni o enregistri). Mentrestant, `restartNeeded` és `true`: el nombre desat no és el que té el motor ara.
- Si es treuen reproductors o enregistradors, **la seva configuració no s'esborra** (sortida o entrada, nom, carpeta, volum i última llista): si es tornen a posar, tornen tal com eren. En arrencar, també es busquen enregistraments interromputs a les carpetes dels que s'han tret.

### 4.10. Editor (fase 3, D-049)

Un sol fitxer MP3 obert alhora. Les edicions no toquen l'original fins que es desa: el motor només guarda quins trossos (en fotogrames) es queden. Els temps (`start`, `end`) són **segons de la versió editada** i s'ajusten al fotograma més proper (24 ms a 48 kHz, 26 ms a 44,1 kHz).

| Ordre | Paràmetres | Resultat |
|---|---|---|
| `editor.get` | — | `{ editor }` (vegeu més avall). També és a `state.get` |
| `editor.open` | `path` (ruta completa d'un `.mp3`) | `{ editor }` amb `busy: "opening"`: s'indexa en segon pla i, en acabar, arriba `editor.changed` (obert, o `error` si no s'ha pogut). El que hi hagués obert es tanca **sense desar**. Si el fitxer **s'està enregistrant**, se n'obre una fotografia que només es llegeix (`live: true`; fase 5, D-117). `bad_state` si té el marcador `.rec` i no s'està enregistrant (enregistrament interromput) |
| `editor.refresh` | — | `{ editor }` amb `busy: "opening"`. Torna a obrir el fitxer de l'editor (fase 5, D-117): d'un enregistrament en curs, amb el que s'ha enregistrat des de l'última fotografia i les marques noves; si ja no s'enregistra, com a fitxer normal (`live: false`). Els canvis sense desar es perden (la interfície ho pregunta). `bad_state` si no hi ha cap fitxer obert o s'està desant |
| `editor.delete` | `start`, `end` | `{ editor, changed }`. Elimina el fragment. `bad_state` si s'eliminaria tot. Retallar l'inici = `start: 0`; retallar el final = `end`: la durada |
| `editor.keep` | `start`, `end` | `{ editor, changed }`. Es queda només amb el fragment (retalla l'inici i el final en un sol pas) |
| `editor.fadeIn` / `editor.fadeOut` | `start`, `end` | `{ editor, changed }`. Fos d'entrada (del silenci al volum que tenia) o de sortida (fins al silenci) al fragment, amb amplitud lineal. Sense recodificar (D-073, D-074): es canvia el volum global de cada grànul, en passos de 1,5 dB. Es pot desfer. Sense fragment: `bad_state` |
| `editor.undo`, `editor.redo` | — | `{ editor }`. Fins a 200 passos |
| `editor.save` | opcionals: `path` (fitxer nou; sense `path`, a sobre de l'original) i `overwrite: true` | `{ editor }` amb `busy: "saving"`. Si la destinació ja existeix i no s'envia `overwrite: true`: **`file_exists`** i no es desa res (la interfície ho ha de confirmar i tornar a enviar). En acabar arriba `editor.changed`: `savedPath` (desat; l'editor continua amb aquest fitxer, sense desfer) o `error`. Escriptura segura: fitxer temporal a la mateixa carpeta + substitució |
| `editor.saveSelection` | `start`, `end` (segons de la versió editada), `path` (fitxer nou, ruta completa) i opcional `overwrite: true` | `{ editor }` amb `busy: "saving"`. **Desa només la selecció** en un fitxer nou (fase 5, D-113). **L'edició no canvia**: el mateix fitxer, els mateixos canvis i el mateix desfer. Les marques de dins de la selecció es desen al costat, comptades des del principi del fitxer nou. `file_exists` com a `editor.save`; `bad_state` si la selecció és buida o si `path` és el fitxer obert. En acabar arriba `editor.changed` amb `savedSelectionPath` (o `error`) |
| `editor.moveMark` | `number` (el de `marks`) i `seconds` (posició nova a la versió editada) | `{ editor }`. **Mou una marca** (fase 5, D-115). Es guarda en temps de l'original i **es desa de seguida** al fitxer de marques del fitxer obert: no canvia `modified` ni es desfà amb `editor.undo`. La marca conserva el número mentre l'editor és obert (`marks` va per ordre de posició). **Si el fitxer és d'un enregistrador que enregistra o el té pendent** (fase 5, D-118), ho fa l'enregistrador (n'és l'amo de les marques): queden ordenades per temps i, si passa per davant d'una altra, es renumeren (igual a l'editor i a l'enregistrador); com a molt fins on s'ha enregistrat; l'estat de l'enregistrador (`recorder.state`) també canvia. `bad_state` si no hi ha aquesta marca, si és en un tros eliminat o si no s'ha pogut desar (llavors no es mou) |
| `editor.close` | — | `{ editor }`. Tanca sense desar; si s'estava obrint o desant, s'atura (la destinació queda intacta) |
| `editor.peaks` | opcionals: `from` (primer fotograma, per defecte 0) i `count` (per defecte, tots) | `{ peaks: { start, count, data } }`: forma d'ona en fotogrames **de l'original** (no de la versió editada: la interfície la munta amb `segments`). `data` en base64, 2 bytes per fotograma (pic de l'esquerre i del dret, de 0 a 255, lineal). Només dona els que ja estan calculats (`peaksDone`) |
| `editor.play` | opcionals: `from` i `to` (segons de la versió editada; per defecte, de l'inici al final) | `{ editor }`. Fa sonar la **versió editada** (els trossos eliminats no sonen) per la sortida de l'editor. Si ja sonava, torna a començar des de `from`. Si `from` és al final, comença de l'inici. `bad_state` si no hi ha cap sortida (ni de l'editor ni de la preescolta) o si la triada no hi és; `player_error` si no s'ha pogut obrir la sortida o el fitxer. Qualsevol edició, desar, obrir o tancar atura la reproducció |
| `editor.stop` | — | `{ editor }`. La posició queda on era |
| `editor.setDevice` | `deviceId`: una **sortida**, o `null` per fer servir la de la preescolta (per defecte) | `{ editor }`. Si sonava, s'atura. Es desa a `config.json` (`editor.device`). Si en arrencar la sortida té un altre identificador, es busca pel nom |

Mentre `busy` no és `null`, `delete`, `keep`, `undo`, `redo` i `save` donen `bad_state`.

```json
{ "playback": { "state": "playing", "position": 754.32, "error": null, "output": null },
  "open": true, "path": "D:\\Enregistraments\\Matinal 2026-11-04 08.00.mp3",
  "busy": null, "progress": 1, "error": null, "savedPath": null,
  "sampleRate": 48000, "frameSeconds": 0.024, "bitrates": [320],
  "originalFrames": 150000, "originalDuration": 3600.0, "duration": 3412.536, "frames": 142189,
  "segments": [[125, 90000], [97810, 52189]],
  "peaksDone": 150000, "peaksError": null,
  "canUndo": true, "canRedo": false, "modified": true,
  "gains": [[97810, -2, -3], [97811, -3, -4]], "silenceSteps": -255, "granulesPerFrame": 2,
  "marks": [ { "number": 1, "seconds": 60.0, "name": null }, { "number": 3, "seconds": 210.0, "name": null } ] }
```

- `playback`: reproducció de l'editor. `state`: `stopped`, `playing` o `error` (amb el motiu a `error`). `position`: segons de la versió editada (on sona o on s'ha aturat). `output`: la sortida triada (`{ id, name }`) o `null` si fa servir la de la preescolta.
- `segments`: trossos de l'original que es queden, `[primer fotograma, quants]`, en ordre.
- `marks`: marques de l'enregistrament (`Nom.mp3.marques`; fase 5, D-112), **ja a la versió editada**: `[{ number, seconds, name }]`, per ordre de posició. Les dels trossos eliminats no hi surten (tornen amb `editor.undo`); `number` és el que tenien a l'original (no canvia en retallar). En desar (`editor.save`), es desen ajustades al costat del fitxer desat; si no en queda cap, el fitxer de marques de la destinació s'esborra. Si no s'han pogut escriure, `error` ho diu (l'àudio sí que s'ha desat). Sense marques: `[]`.
- `gains`: fosos, per fotograma de l'**original** (si després es talla, el fos es mou amb el seu so): `[fotograma, passos del grànul 1, passos del grànul 2]`, en ordre. Cada pas és de 1,5 dB (negatiu = més fluix); `silenceSteps` o menys = silenci. A MPEG-2 hi ha un sol grànul per fotograma (`granulesPerFrame`: 1) i el segon valor no es fa servir. Els fosos se senten a la previsualització (`editor.play`).
- `peaksDone`: fotogrames de l'original que ja tenen forma d'ona. Es calcula en segon pla després d'obrir (uns 25 s per hora en un ordinador normal) i es pot editar mentrestant; quan és igual a `originalFrames` (fotogrames de l'original), és completa. La segona vegada que s'obre un fitxer surt de la memòria cau i és completa de seguida. `peaksError`: no s'ha pogut calcular (el fitxer es pot editar igualment).
- `error`: motiu de l'última feina que ha fallat (obrir o desar). Es neteja amb l'ordre següent.
- Es desen els fotogrames **tal qual** (sense recodificar). Les etiquetes ID3v2 i ID3v1 es conserven; el fotograma Xing/Info de LAME es treu (diria una durada falsa).

### 4.11. Perfils (fase 4, D-057, D-058 i D-061; PIN: fase 5, D-095)

Un perfil és la configuració de treball d'un programa (vegeu 4.9). El paràmetre que diu quin perfil és es diu `profile` (no `id`, que ja és l'identificador del missatge) i porta l'`id` del perfil: el nom de la seva carpeta.

| Ordre | Paràmetres | Resposta |
|---|---|---|
| `profile.list` | — | `{ profiles: [{ id, name, active, hasPin }] }`, ordenats pel nom. `hasPin`: si el perfil té PIN (mai el PIN) |
| `profile.create` | `name`; opcional `copyFrom` (l'`id` d'un perfil) | `{ profile: { id, name }, profiles }`. Sense `copyFrom`, un perfil buit (2 reproductors, 2 enregistradors, sense dispositius) amb el programa del mateix nom; amb `copyFrom`, una còpia d'aquell, amb les seves últimes llistes. No canvia el perfil actiu |
| `profile.rename` | `profile`, `name` | `{ profiles, settings }`. La carpeta no canvia |
| `profile.delete` | `profile` | `{ profiles }`. Esborra la carpeta del perfil (configuració i últimes llistes; mai fitxers d'àudio). L'actiu no es pot eliminar: `bad_state` |
| `profile.export` | `profile`, `path` (ruta completa del `.zip`; si ja hi és, se substitueix) | `{ profile: { id, name }, path }`. El `.zip` porta `perfil.json` i `llistes/*.obpl` (mai fitxers d'àudio) |
| `profile.import` | `path` (ruta completa del `.zip`); opcional `name` | `{ profile: { id, name }, profiles }`. Un perfil nou amb el contingut del `.zip`; no canvia l'actiu. Sense `name`, es diu com el perfil del `.zip`; si ja n'hi ha un que es diu així, `profile_exists` (es pot tornar a enviar amb un altre `name`). Si el fitxer no és un perfil, és d'una versió desconeguda o passa dels límits (200 entrades, 10 MB per fitxer, 50 MB en total): `file_error`. Del `.zip` només s'agafen `perfil.json` i `llistes/<nom>.obpl`; les entrades amb camins (`..`, subcarpetes) s'ignoren |
| `profile.checkPin` | `profile`, `pin` (text) | `{ valid }`: si és el PIN del perfil. Un perfil sense PIN sempre dona `true` |
| `profile.setPin` | `profile`, `pin`: de 4 a 8 xifres, o `null` / `""` per treure'l | `{ profiles }`. Un PIN que no és vàlid: `bad_params` i no canvia res |
| `profile.switch` | `profile` | `{ switching: true, profile: { id, name } }`, i **el motor s'atura** poc després (com `engine.shutdown`). La interfície l'ha de tornar a arrencar: el motor nou llegeix el perfil triat (el nombre de reproductors i d'enregistradors només es pot canviar en arrencar). Des d'aquest moment el motor ja no desa res més de la configuració |

- Noms: de 1 a 60 caràcters (es treuen els espais repetits); no n'hi pot haver dos d'iguals (sense distingir majúscules). Si no, `bad_params`. Un perfil que no existeix, també `bad_params`.
- `profile.switch` dona `bad_state`, i no canvia res, si: algun enregistrador enregistra, algun reproductor o la preescolta sonen, o l'editor està obrint o desant o té canvis sense desar. El missatge diu què cal aturar. Canviar al perfil que ja és l'actiu: `bad_params`.
- Un enregistrament pendent (sense desar ni descartar) no impedeix el canvi: el fitxer ja és al disc i surt a Recents.
- **PIN** (D-095): protecció contra canvis per error, no seguretat. Es desa en text pla a `perfil.json` (`pin`) i el motor **no el comprova en cap altra ordre**: és la interfície qui el demana (triar un perfil, ⚙ Configuració, canviar el nom, eliminar i importar). El motor no el dona mai. Un perfil nou no en té; un duplicat (`copyFrom`) conserva el de l'origen; `profile.export` el treu del `.zip` i `profile.import` també el treu.
- Errors de disc en crear, canviar el nom o eliminar: `file_error`.

### 4.12. Explorador del panell lateral (fase 4, D-063)

Cada perfil té les seves carpetes per a dues pestanyes: `kind` = `"audios"` (fitxers d'àudio) o `"lists"` (llistes desades: `.obpl`, `.m3u`, `.m3u8`).

| Ordre | Paràmetres | Resposta |
|---|---|---|
| `explorer.get` | — | `{ audios: [{ name, path, exists }], lists: [...] }`. `exists`: la carpeta hi és ara (una unitat de xarxa o un USB poden no ser-hi). `path` es dona normalitzada com a `explorer.list` (p. ex. els noms curts com `ALBERT~1` desplegats) |
| `explorer.setFolders` | `kind`, `folders: [{ name?, path }]` (en l'ordre que es mostren; substitueixen les d'abans) | El mateix que `explorer.get`. `path`: ruta completa; no cal que existeixi. Sense `name`, el de la carpeta. Com a molt 20 carpetes i 40 caràcters per nom; una carpeta repetida dona `bad_params`. Es desa al perfil actiu (`explorer` a `perfil.json`) |
| `explorer.list` | `kind`, `path` | `{ path, folders: [{ name, path }], files: [{ name, path, size, modified, duration }], truncated, durationsPending }`: subcarpetes i fitxers de la pestanya (sense baixar a les subcarpetes), en l'ordre de l'Explorador de Windows («2» abans de «10»). Se salten els fitxers ocults i de sistema. Com a molt 5.000 de cada; si n'hi ha més, `truncated: true`. `modified`: data i hora local. `duration` (fase 5, D-104; només `audios`): segons, si ja se sap; si no, `null` i el motor la mesura en segon pla i l'envia amb l'esdeveniment `explorer.durations` (`durationsPending`: quantes falten). La llista no s'espera mai a mesurar |

- `explorer.list` **només llegeix dins de les carpetes configurades** per a aquella pestanya (la carpeta mateixa o una subcarpeta). Qualsevol altra ruta, també amb `..` o una carpeta germana que comenci igual (`Música2` no és dins de `Música`), dona `bad_params`. Una carpeta que no hi és o no es pot llegir: `file_error`.
- Per afegir el que es tria a un reproductor es fa servir `playlist.add` amb les rutes (també accepta llistes `.obpl` i `.m3u`, i n'afegeix les pistes).

### 4.13. Compressor/limitador dels reproductors (fase 4, D-066 i D-067)

Objecte `processing` (al reproductor, a `player.setProcessing` i a cada preset):

```json
{ "enabled": true, "inputGainDb": 9.5, "thresholdDb": -2.9, "ratio": 100,
  "attackMs": 4.2, "releaseMs": 1450, "outputGainDb": 0, "stereoLink": false, "ceilingDb": -0.3 }
```

| Camp | Marge (fora, al límit) | Per defecte | Què fa (equivalent del SqrSoft) |
|---|---|---|---|
| `enabled` | — | `false` | Desactivat, el so passa exactament igual (sense retard ni sostre) |
| `inputGainDb` | −20 a +30 | 0 | Guany abans de comprimir («Gain») |
| `thresholdDb` | −40 a 0 | −3 | Llindar («Threshold») |
| `ratio` | 1 a 100 | 4 | Relació; 100 = limitador («Ratio») |
| `attackMs` | 0,1 a 200 | 5 | Atac («AttackTime») |
| `releaseMs` | 10 a 5000 | 500 | Alliberament («ReleaseTime») |
| `outputGainDb` | −20 a +20 | 0 | Guany després de comprimir («OutputGain») |
| `stereoLink` | — | `false` | Canals junts («StereoTie») |
| `ceilingDb` | −6 a 0 | −0,3 | Sostre: la sortida no el passa mai (limitador amb 1,5 ms d'anticipació) |

- Ordre: pista → compressor → sostre → **volum (fader)** → sortida. El fader no canvia com comprimeix.
- `settings.get` porta `processingPresets: [{ id, name, builtIn, processing }]`: primer els de l'aplicació (`builtIn: true`: `sqrsoft-la-central`, la configuració del Winamp de La Central, `suau` i `nomes-sostre`) i després els propis del perfil (D-069).

| Ordre | Paràmetres | Resposta |
|---|---|---|
| `processing.savePreset` | `name` (1 a 40 caràcters); `processing` (els ajustos) **o** `player` (els d'aquest reproductor) | `{ preset: { id, name }, processingPresets }`. Es desa al perfil. Si ja hi ha un preset propi amb aquest nom (sense distingir majúscules), se substitueix (la interfície ho confirma abans). El nom d'un preset de l'aplicació o més de 30 presets propis: `bad_params` |
| `processing.deletePreset` | `preset` (id d'un preset propi) | `{ processingPresets }`. Els reproductors que el feien servir no canvien. Un preset de l'aplicació o que no existeix: `bad_params` |

### 4.14. Normalització de sonoritat (fase 4, D-070 i D-071)

- Cada pista que s'afegeix a una llista es comprova (`status`) i, després, amb menys prioritat, se'n mesura la sonoritat (EBU R128 / ITU BS.1770-4): `loudness` a l'element de la llista. Les mesures es desen a la memòria cau (`cache\sonoritat\`, 60 dies des de l'últim ús).
- Els reproductors amb `normalization.enabled` apliquen a cada pista el guany **objectiu − sonoritat**, com a molt **+10 dB** (`settings.loudness.maxBoostDb`), abans del compressor/limitador (4.13). Cada pista encadenada (bucle o avanç automàtic) porta el seu guany.
- Activar-la, desactivar-la o canviar l'objectiu té efecte a partir de la pista següent. Una pista que encara no s'havia mesurat quan va començar sona sense normalitzar.
- Les pistes mono es mesuren com sonen (el mateix senyal pels dos canals).
- `settings` porta `loudness: { targetLufs, maxBoostDb }` (per defecte, −12 LUFS i +10 dB).

## 5. Esdeveniments

| Esdeveniment | Camps | Freqüència | Des de |
|---|---|---|---|
| `engine.status` | `uptime` (s), `clients` | cada segon | fase 0 |
| `devices.changed` | `changes`: llista de canvis (vegeu 5.1) | quan Windows avisa d'un canvi real, agrupat (0,5 s sense avisos nous) | fase 1 |
| `player.state` | `player`: objecte complet (vegeu 4.2) | quan canvia l'estat, el fitxer, la sortida o l'error d'un reproductor | fase 1 |
| `player.position` | `players`: `[ { id, position, duration } ]` de tots els reproductors | 10 per segon, només mentre algun reproductor sona | fase 1 |
| `recorder.state` | `recorder`: objecte complet (vegeu 4.3) | quan canvia l'estat, el fitxer, l'entrada o l'error d'un enregistrador | fase 1 |
| `recorder.progress` | `recorders`: `[ { id, elapsed, fileSize, freeDiskBytes, lowDiskSpace, samplesLost, captureGlitches } ]` dels que enregistren | cada segon, només mentre algun enregistra | fase 1 |
| `hardware.reassigned` | `element`: una línia com les de `elements` de 4.4, amb el dispositiu ja assignat | quan torna el dispositiu d'un element que en arrencar era `missing`. El reproductor **no es posa a sonar**; després arriba també el seu `player.state` o `recorder.state` | fase 1 |
| `meters` | `players`, `recorders`, `cue`: nivells de pic de cada element (vegeu 5.2) | 25 per segon, si hi ha alguna interfície connectada | fase 2 |
| `playlist.changed` | `playlist`: la llista sencera (vegeu 4.5) | quan canvia alguna cosa (pistes, ordre, marques, resultat d'una comprovació). Agrupat: com a molt 10 per segon per reproductor | fase 2 |
| `recordings.changed` | `recordings`: la llista sencera d'enregistraments acabats (vegeu 4.6) | quan s'hi afegeix, es desa, es descarta o se'n treu algun. Agrupat: com a molt 10 per segon | fase 2 |
| `cue.state` | `cue`: objecte complet (vegeu 4.8) | quan canvia l'estat, la pista, la sortida, el volum o l'error de la preescolta | fase 2 |
| `cue.position` | `position`, `duration` (segons), `format` (el de la pista, quan ja es coneix) | 10 per segon, només mentre la preescolta sona | fase 2 |
| `editor.changed` | `editor`: objecte complet (vegeu 4.10) | a cada canvi: obrir, editar, desar, reproduir, errors; el progrés d'obrir, desar o fer la forma d'ona, com a molt 4 per segon | fase 3 |
| `explorer.durations` | `durations`: `{ "<ruta>": segons | null }` (`null`: no s'ha pogut llegir) | a grups (com a molt 100 o cada 0,3 s) mentre es mesuren els àudios de l'última carpeta llistada amb `explorer.list`; si se'n llista una altra, les que faltaven de l'anterior ja no es mesuren | fase 5 (D-104) |
| `editor.position` | `position` (segons de la versió editada) | 10 per segon, només mentre l'editor reprodueix | fase 3 |

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
  "recorders": { "1": [-18.0, -17.5], "2": [-90, -90] },
  "cue": [-24.1, -23.9],
  "playerReduction": { "1": 6.4, "2": 0 },
  "recorderReduction": { "1": 0, "2": 1.2 } }
```

- `playerReduction`: quant abaixa el compressor/limitador de cada reproductor (dB, ≥ 0; el màxim des de l'esdeveniment anterior), per a l'indicador de reducció. `0` si està desactivat o no sona (fase 4, 4.13).
- `recorderReduction`: el mateix per al limitador de cada enregistrador (fase 5, D-083). `0` si està desactivat o no s'enregistra.

- Per a cada element, `[esquerre, dret]` en **dBFS**: el pic més alt des de l'esdeveniment anterior (uns 40 ms). `-90` vol dir silenci.
- Reproductors: el que surt, **després del volum**. Preescolta: igual, el que surt pels auriculars. Enregistradors: el que entra, **amb el volum d'enregistrament aplicat**, també quan no s'enregistra (si `inputActive`).
- Una entrada mono dona el mateix valor als dos canals.
- La interfície fa la caiguda suau, la retenció del pic i l'indicador de saturació.

## 6. Previst (no implementat)

- Enregistradors: editar (fase 3)

## Fitxer de marques d'un enregistrament (`Nom.mp3.marques`)

Fase 5, D-110. JSON al costat de l'MP3 (escriptura segura: temporal + substitució). Segueix l'enregistrament en desar-lo (`recording.save`), va a la paperera amb ell (`recording.discard`) i també n'hi ha un al costat de la còpia de l'historial (que s'esborra amb la neteja de l'historial). Si està malmès, es llegeix com si no hi hagués marques.

```json
{ "schemaVersion": 1, "marks": [ { "seconds": 62.016 }, { "seconds": 845.2, "name": "Entrevista" } ] }
```

- `seconds`: posició des del principi del fitxer. `name` és opcional.
