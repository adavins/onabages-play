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
| `settings.get` | — | `{ settings: { ui: { zoom } } }`: ajustos desats a `config.json` | fase 2 |
| `settings.set` | `ui`: els ajustos a canviar, p. ex. `{ "zoom": 1.25 }` (escala de la interfície, de `0.5` a `1.5`; per sota de `0.7` la lletra no es fa més petita) | `{ settings }` amb tots els ajustos. Es desa de seguida. Un valor fora de marge dona `bad_params` | fase 2 |

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

Hi ha dos reproductors fixos, `"1"` i `"2"` (configurable al pas 9). Cada un té la seva llista (vegeu 4.5).

| Ordre | Paràmetres | Estats en què es pot fer | Resultat |
|---|---|---|---|
| `player.setDevice` | `player`, `deviceId` | tots menys `playing`. Si hi ha pista preparada, es torna a preparar: des del mateix punt si era a `paused` o `error` | `{ player }` |
| `player.play` | `player` | `ready` i `paused`: continua la pista actual. `empty` i `ended`: fa sonar la pista amb **cue** o, si no n'hi ha, la que va **després de l'última reproduïda** (la primera, si no n'ha sonat cap). Si no n'hi ha cap més, `bad_state` | `{ player }` |
| `player.playItem` | `player`, `itemId` | tots menys `error`. Doble clic: atura el que sona i fa sonar aquesta pista des de l'inici. La que sonava queda com a última reproduïda | `{ player }` |
| `player.pause` | `player` | `playing` | `{ player }` |
| `player.stop` | `player` | tots menys `error`. **Atura la llista**: passa a `ended` i la pista queda com a última reproduïda (`lastPlayedId`) | `{ player }` |
| `player.load` | `player`, `path` (ruta completa) | tots menys `playing`. Afegeix el fitxer al final de la llista i el deixa preparat (`ready`). Només per a la consola de proves: la interfície fa servir `playlist.add` | `{ player }` |
| `player.retry` | `player` | `error`. Torna a obrir la sortida i el fitxer i queda `ready` **a la mateixa posició**. No torna a sonar sol: cal `player.play` | `{ player }` |
| `player.seek` | `player`, `position` (segons des de l'inici) | `ready`, `playing`, `paused`, `ended`. Salta a aquest punt (si és més enllà del final, es queda al final). Si sonava, **continua sonant** des d'allà, amb un silenci molt curt mentre es torna a obrir la sortida; si era en pausa, continua en pausa; si havia acabat, queda `ready`. Tornar a començar la pista és `position: 0` | `{ player }` |
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

### 4.3. Enregistradors

Hi ha dos enregistradors fixos, `"1"` i `"2"` (el nombre serà configurable al pas 9 de la fase 2). Desar i Descartar són a 4.6; Editar, a la fase 3.

| Ordre | Paràmetres | Estats en què es pot fer | Resultat |
|---|---|---|---|
| `recorder.setDevice` | `recorder`, `deviceId` (una **entrada**) | tots menys `recording` | `{ recorder }` |
| `recorder.start` | `recorder`; opcionals, **només per a aquest enregistrament**: `folder` (ruta completa) i `name` (per al nom del fitxer). Per defecte, els de l'enregistrador (`recorder.configure`) | `idle`, `pending`, `error`. Comença de seguida, sense preguntar res; l'enregistrament pendent passa a recents (4.6). **Mai no sobreescriu cap fitxer** | `{ recorder }` |
| `recorder.configure` | `recorder`; `name` i/o `folder` (ruta completa). Un `name` buit torna al de per defecte (`Enregistrador N`) | tots. S'aplica a partir del pròxim enregistrament. Es desa a `config.json` | `{ recorder }` |
| `recorder.setGain` | `recorder`, `db`: volum d'enregistrament de `-60` (silenci) a `+12`; per defecte `0`. Fora de marge, es queda al límit | tots. S'aplica de seguida (també a mig enregistrament), amb una rampa curta sense clics. El que passa de 0 dBFS es retalla. Es desa a `config.json` | `{ recorder }` |
| `recorder.stop` | `recorder` | `recording`. La interfície **sempre** ha de demanar confirmació abans d'enviar-la | `{ recorder }` |

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
  "error": null }
```

- `state`: `idle` (inactiu), `recording`, `pending` (fitxer acabat, a `file`), `error`.
- `name`, `folder`, `gainDb`: vegeu `recorder.configure` i `recorder.setGain`. El mesurador (5.2) ja mostra el nivell **amb el volum aplicat**: el que queda al fitxer.
- `elapsed`: segons enregistrats. `fileSize`: bytes escrits. `freeDiskBytes`: espai lliure a la unitat del fitxer mentre s'enregistra, i a la de `folder` quan no (`null` si no se sap, p. ex. en una unitat de xarxa). `lowDiskSpace`: queda menys de l'equivalent a 2 hores d'enregistrament (uns 288 MB a 320 kbps).
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
| `playlist.load` | `player`, `path` (ruta completa d'un `.obpl`, `.m3u` o `.m3u8`); opcional `replace` (per defecte `true`: substitueix la llista; `false`: afegeix al final). La pista que sona continua sonant | `{ ids, missing }`: les pistes noves i quantes no existeixen |
| `playlist.save` | `player`, `path` (ruta completa; si no acaba en `.obpl`, s'hi afegeix) | `{ path, count }` |
| `track.info` | `path` | `{ info: { path, fileSize, duration, format, title, artist, album, year, genre, comment } }` (metadades amb TagLib#; els camps que no hi són, `null`) |

Objecte `playlist` (també a `state.get` → `playlists` i a l'esdeveniment `playlist.changed`):

```json
{ "player": "1", "cueId": "t4", "totalDuration": 612.4,
  "items": [
    { "id": "t3", "name": "Sintonia", "path": "D:\\Musica\\Sintonia.mp3",
      "duration": 32.5, "format": "MP3 320 kbps estèreo 44,1 kHz",
      "status": "ok", "problem": null, "loop": false, "autoNext": true }
  ] }
```

- `name`: nom del fitxer sense extensió (no es fan servir les metadades).
- `status`: `analyzing` (s'està comprovant), `ok` (sonarà), `error` (malmès o format no compatible) o `missing` (el fitxer ja no hi és). `problem`: el motiu, en català.
- **Comprovació:** en afegir una pista, el motor l'obre amb el mateix descodificador que el reproductor i en llegeix el principi. **Si una pista és `ok`, sonarà.** Cada 30 s es comprova que els fitxers encara existeixen (sense obrir-los); si un que faltava torna, es torna a comprovar.
- `totalDuration`: suma de les durades conegudes.
- **Avanç** (D-031): per defecte, manual (quan s'acaba una pista, la llista s'atura). `loop`: la pista es repeteix fins a Stop. `autoNext`: quan s'acaba, sona la del cue o, si no n'hi ha, la següent (**un sol pas**).  Quan ja ha passat a la següent, l'`autoNext` d'aquella pista es desmarca sol (D-033). `cueId`: només n'hi pot haver una; s'apaga quan comença a sonar. Amb bucle o avanç automàtic **no hi ha silenci** entre pistes: el motor decideix què ve després **1 s abans** del final.

### 4.6. Enregistraments acabats: pendents i recents (D-036)

Cada enregistrament que s'acaba (aturat, entrada perduda, motor que s'atura) entra a una llista. Si un enregistrador és a `pending` i el seu `file` és el `path` d'un element, és el seu **pendent** (la interfície el mostra al panell de l'enregistrador); la resta són els **recents**. Com a molt n'hi ha 30 (els més antics en surten, però els fitxers no s'esborren; els pendents no en surten mai). La llista es desa i es recupera en arrencar.

| Ordre | Paràmetres | Resultat |
|---|---|---|
| `recording.save` | `recordingId`, `path` (ruta completa; si no acaba en `.mp3`, s'hi afegeix). **Mai no sobreescriu**: si ja existeix, s'hi afegeix `_2`, `_3`... Mou el fitxer: al mateix disc és instantani; a un altre, es copia en segon pla (`busy: true` fins que acaba). Si era el pendent d'un enregistrador, aquest torna a `idle` de seguida | `{ path, recording }`: on quedarà |
| `recording.discard` | `recordingId`. Envia el fitxer a la **paperera de Windows** (en una unitat de xarxa, Windows l'esborra del tot) i el treu de la llista. Si era el pendent, l'enregistrador torna a `idle`. La interfície ha de demanar confirmació | `{ discarded }` |
| `recording.remove` | `recordingId`. El treu de la llista; el fitxer no es toca. No es pot amb el pendent d'un enregistrador (`bad_state`: s'ha de desar o descartar) | `{ removed }` |

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
  "cue": [-24.1, -23.9] }
```

- Per a cada element, `[esquerre, dret]` en **dBFS**: el pic més alt des de l'esdeveniment anterior (uns 40 ms). `-90` vol dir silenci.
- Reproductors: el que surt, **després del volum**. Preescolta: igual, el que surt pels auriculars. Enregistradors: el que entra, **amb el volum d'enregistrament aplicat**, també quan no s'enregistra (si `inputActive`).
- Una entrada mono dona el mateix valor als dos canals.
- La interfície fa la caiguda suau, la retenció del pic i l'indicador de saturació.

## 6. Previst (no implementat)

- Enregistradors: editar (fase 3)
