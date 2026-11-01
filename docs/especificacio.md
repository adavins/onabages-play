# Ona Bages Play
## Especificació mestra — versió 2.0

**Versió:** 2.0 — Revisió de la v1.0. Decisions d'arquitectura confirmades l'1 de novembre de 2026
**Plataforma:** Windows 10 / 11
**Tipus d'aplicació:** escriptori, només àudio, modular
**Distribució:** gratuïta, per a la pròpia emissora i altres emissores
**Equip de desenvolupament:** una sola persona, amb VS Code i Claude Code

---

# 0. Sobre aquesta versió

## 0.1. Què canvia respecte de la v1.0

Aquesta versió adapta la v1.0 a tres condicions reals del projecte:

1. **Desenvolupament individual**, amb poca experiència en C++, TypeScript i React, i més experiència en JavaScript pur i Bootstrap. El projecte ha de ser assumible i s'ha de poder fer en un temps raonable.
2. **Ús en programes en directe amb operador**, de diverses hores. No és una emissió desatesa 24/7.
3. **Maquinari no professional**: targetes de consum i dispositius USB, tots estèreo i separats.

Canvis principals:

| Tema | v1.0 | v2.0 | Motiu |
|---|---|---|---|
| Motor d'àudio | C++ + WASAPI directe | **C# (.NET) + NAudio** (fa servir WASAPI) | Molt menys risc per a un sol desenvolupador amb poc C++. Sense CMake ni MSVC. Errors més fàcils de depurar |
| Interfície | React + TypeScript | **HTML + Bootstrap + JavaScript pur** dins d'Electron | Tecnologia coneguda. Sense passos de compilació |
| Comunicació | Per definir | **WebSocket local** amb missatges JSON | Es pot provar des d'una pàgina web senzilla. La interfície es pot reiniciar sense aturar l'àudio |
| Persistència | SQLite des del principi | **Fitxers JSON** al MVP; SQLite només si cal | Més simple de fer, copiar, exportar i depurar |
| Enregistrament | WAV i MP3, escriptura segmentada | **MP3 directe a 320 kbps CBR** en un sol fitxer amb bolcat periòdic al disc | Requisit explícit: estalviar temps i espai. És més simple i igual de segur |
| Preescolta | No hi era | **Mòdul propi** amb sortida dedicada | Funció imprescindible. Avui es fa amb una instància de Winamp |
| Usuaris i permisos | Mòdul inicial | **Fora de l'abast inicial** | No aporta valor al primer ús real |
| Abast inicial | Totes les funcions per fases | **MVP estricte**: reproductors + preescolta + enregistradors | Arribar aviat a l'ús real |
| Sincronització de rellotges | Per estudiar | **Fora d'abast** | Cap funció barreja àudio de dispositius diferents (vegeu 5.4) |

## 0.2. Decisions confirmades (1 de novembre de 2026)

Aquestes decisions substitueixen la decisió 18 de la v1.0:

- [x] Motor en C# / .NET amb NAudio en lloc de C++.
- [x] Interfície amb JavaScript pur i Bootstrap en lloc de React i TypeScript.
- [x] Configuració en JSON en lloc de SQLite al MVP, amb una capa de dades preparada per migrar (vegeu 11.4).
- [x] Enregistrament en un sol fitxer MP3 amb bolcat periòdic en lloc de segments.
- [x] Desenvolupament de totes les fases amb VS Code i Claude Code.

---

# 1. Visió del projecte

Ona Bages Play és una aplicació d'escriptori per a la **producció de programes de ràdio en directe**. Reuneix en un sol entorn:

- diversos reproductors independents, cadascun amb la seva sortida física;
- una preescolta per auriculars;
- diversos enregistradors MP3 simultanis;
- un editor ràpid per retallar i desar el que s'ha enregistrat.

Substitueix el flux de treball actual: **diverses instàncies de Winamp + mp3DirectCut**. Ha de conservar-ne la rapidesa i afegir-hi el que falta: una visió de conjunt, l'avançament manual o automàtic per pista, una preescolta integrada i la protecció dels enregistraments.

No és un DAW ni un sistema d'automatització 24/7.

**Prioritat absoluta:** l'àudio no es pot interrompre i els enregistraments no es poden perdre.

# 2. Principis de disseny

1. **Fiabilitat abans que funcions.** Una funció que pot provocar un tall d'àudio no entra fins que s'hagi provat.
2. **Simplicitat d'operació.** Una acció habitual s'ha de poder fer amb un sol clic o una tecla. Res no ha d'interrompre una reproducció o un enregistrament sense confirmació.
3. **Motor separat de la interfície.** L'àudio corre en un procés propi. Si la interfície es penja o es tanca, la reproducció i els enregistraments continuen, i en tornar-la a obrir es reconnecta.
4. **Simplicitat tècnica.** Poques dependències, ben conegudes i amb llicències compatibles amb la distribució gratuïta. Si hi ha dues solucions, es tria la més fàcil de mantenir.
5. **Desenvolupament incremental.** Cada fase acaba amb una cosa que funciona i que es pot provar amb el maquinari real.
6. **No dependre només del color.** Els estats es mostren amb color, icona i text.

# 3. Context d'ús i maquinari

## 3.1. Escenari principal

Un programa en directe d'una hora o més, amb un operador o un locutor-operador:

- Reproductor 1: música.
- Reproductor 2: indicatius, sintonies i falques.
- Reproductor 3 o més: talls preparats i recursos.
- Preescolta: escoltar per auriculars una pista abans de llançar-la.
- Enregistrador 1: sortida de programa de la taula.
- Enregistrador 2 o més: altres fonts (connexions, línies).

Quan el programa, o una part, s'acaba, s'atura l'enregistrament, s'obre a l'editor, es retallen l'inici, el final i les parts sobrants, i es desa. Mentrestant, els altres enregistradors poden continuar funcionant.

## 3.2. Maquinari de referència

Dispositius estèreo separats, de gamma de consum:

- Creative Sound Blaster Z.
- Creative Sound Blaster Audigy.
- Àudio integrat a la placa base.
- Dispositius USB d'entrada i sortida (previsiblement, des de la consola de mescles).

Taula de mescles de referència: AEQ Capitol IP12. L'aplicació no ha de dependre de cap model concret.

Conseqüències per al disseny:

- Es treballa en **mode compartit de WASAPI**, que és el més compatible amb controladors de consum. El mode exclusiu i ASIO queden fora de l'abast inicial.
- Cada reproductor i cada enregistrador s'assigna a **un dispositiu estèreo**. Si en el futur apareixen dispositius multicanal, el model haurà de permetre triar també el parell de canals (vegeu 5.2).
- Els dispositius USB es poden connectar i desconnectar mentre l'aplicació funciona.
- Cal comprovar aviat els controladors de l'**Audigy** a la versió de Windows que es faci servir, perquè el suport per a targetes antigues pot ser limitat.

## 3.3. Preparació de Windows (recomanada)

Molts talls d'àudio no els provoca l'aplicació sinó el sistema. Cal documentar i aplicar a cada ordinador:

- Desactivar les «millores d'àudio» de Windows a tots els dispositius utilitzats.
- Desactivar els efectes propis dels controladors Creative (SBX, Crystalizer, etc.) a les sortides d'antena.
- Fixar el mateix format a tots els dispositius (per exemple, 44,1 kHz o 48 kHz, 16 o 24 bits) des del Tauler de control de so.
- Treure els sons del sistema dels dispositius d'antena.
- Pla d'energia d'alt rendiment, i desactivar la «suspensió selectiva USB».
- Configurar les «hores actives» de Windows Update perquè no hi hagi reinicis durant els programes.

La llista detallada és a `docs/installacio-windows.md`.

# 4. Arquitectura

## 4.1. Visió general

```text
┌────────────────────────────────────────────┐
│  Electron (finestra, menús, diàlegs)       │
│  ┌──────────────────────────────────────┐  │
│  │ Interfície: HTML + Bootstrap + JS    │  │
│  └──────────────────┬───────────────────┘  │
└─────────────────────┼──────────────────────┘
                      │ WebSocket local (127.0.0.1)
                      │ missatges JSON versionats
┌─────────────────────┴──────────────────────┐
│  Motor d'àudio (procés .NET independent)   │
│  dispositius · reproductors · preescolta   │
│  enregistradors · mesuradors · DSP         │
│  NAudio (WASAPI) · LAME (MP3)              │
└────────────────────────────────────────────┘
          │                         │
   sortides físiques        entrades físiques
```

## 4.2. Tecnologies

| Àrea | Tecnologia | Llicència | Funció |
|---|---|---|---|
| Finestra d'escriptori | Electron | MIT | Finestra, diàlegs de fitxers, arrossegar fitxers de l'Explorador, instal·lador |
| Interfície | HTML + Bootstrap 5 + JavaScript (mòduls ES) | MIT | Panells i controls, tema fosc |
| Motor | C# / .NET 10 (LTS) | MIT | Procés d'àudio independent |
| Àudio Windows | NAudio | MIT | WASAPI (sortida i captura), enumeració i avisos de dispositius, lectura de formats, filtres |
| Codificació MP3 | LAME (via NAudio.Lame o equivalent) | LGPL | Enregistrament MP3 directe |
| Descodificació | NAudio + Media Foundation de Windows | MIT / sistema | MP3, WAV, AAC/M4A, FLAC, WMA |
| OGG | NAudio.Vorbis (complement) | MIT | OGG Vorbis |
| Forma d'ona (editor) | Canvas propi o wavesurfer.js | MIT / BSD | Visualització i selecció |
| Panells mòbils (fase posterior) | GridStack.js | MIT | Distribució configurable |
| Instal·lador | electron-builder | MIT | Un sol instal·lador amb interfície i motor |

**Llicències:** totes permeten distribuir l'aplicació de forma gratuïta. LAME és LGPL: cal distribuir-la com a biblioteca separada (DLL), incloure'n el text de llicència i indicar on es pot obtenir el codi font. FFmpeg no és necessari.

## 4.3. Per què aquesta combinació

- **C# en lloc de C++.** Els errors de memòria i de fils en C++ són els més difícils de depurar, sobretot amb poca experiència. C# dona errors clars i és molt més segur, i NAudio ja resol WASAPI, formats, dispositius i filtres. Per a la reproducció radiofònica, la latència de WASAPI en mode compartit (de l'ordre de desenes de mil·lisegons) és més que suficient.
- **JavaScript pur i Bootstrap.** És la tecnologia que el desenvolupador ja coneix, i no necessita compilació. Es pot afegir comprovació de tipus opcional amb JSDoc i `// @ts-check`, sense convertir el projecte a TypeScript.
- **Electron.** Dona accés a les rutes reals dels fitxers arrossegats, als diàlegs natius i a un instal·lador senzill. La interfície continua sent una pàgina web normal.
- **WebSocket local.** La interfície i el motor són independents. Durant el desenvolupament, el motor es pot provar des d'una pàgina HTML de proves al navegador, sense Electron.

Alternatives descartades:
- **C++ amb miniaudio o JUCE:** més control, però massa risc i complexitat per a l'equip previst.
- **Aplicació .NET amb WebView2:** consumeix menys memòria, però si cau l'amfitrió cau també el motor.

## 4.4. Comunicació entre interfície i motor

- El motor escolta només a `127.0.0.1`, en un port configurable, i exigeix un testimoni (*token*) generat en cada arrencada.
- Missatges JSON amb un camp de versió del protocol.
- **Ordres** (de la interfície al motor), amb identificador i resposta:

```json
{ "v": 1, "id": 42, "cmd": "player.play", "player": "p1" }
{ "v": 1, "id": 42, "ok": true }
```

- **Esdeveniments** (del motor a la interfície), sense resposta:

```json
{ "v": 1, "ev": "player.state", "player": "p1", "state": "playing", "item": "a7f3", "pos": 83.2, "dur": 214.5 }
{ "v": 1, "ev": "meters", "data": { "p1": [-12.3, -11.8], "r1": [-18.0, -17.5] } }
```

- Mesuradors i posicions: entre 20 i 30 actualitzacions per segon, agrupades en un sol missatge.
- **El motor és la font de veritat** de l'estat de l'àudio. En connectar-se, la interfície demana l'estat complet (`state.get`) i el reconstrueix.
- El protocol es documenta a `docs/protocol.md` i es versiona.

## 4.5. Cicle de vida dels processos

- Electron arrenca el motor si no està en marxa. El motor publica el port i el testimoni en un fitxer d'execució (`%APPDATA%\OnaBages\engine.json`), perquè una interfície reiniciada s'hi pugui tornar a connectar.
- Si la interfície es tanca **amb reproduccions o enregistraments actius**, demana confirmació. Si es tanca de manera inesperada, el motor continua, i en tornar-la a obrir es reconnecta.
- El motor no es tanca mentre hi hagi un enregistrament actiu, tret d'una ordre explícita i confirmada.
- Si el motor cau, la interfície ho mostra de manera molt visible i ofereix reiniciar-lo. Els enregistraments es recuperen tal com descriu la secció 8.5.

## 4.6. Estructura del repositori

```text
ona-bages-play/
├── CLAUDE.md                 ← regles del projecte per a Claude Code
├── README.md
├── docs/
│   ├── especificacio.md      ← aquest document
│   ├── protocol.md
│   ├── decisions.md          ← registre de decisions
│   └── installacio-windows.md
├── engine/                   ← solució .NET (OnaBages.slnx)
│   ├── OnaBages.Audio/       ← dispositius, reproductors, enregistradors, DSP
│   ├── OnaBages.Engine/      ← procés: WebSocket, ordres, estat
│   ├── OnaBages.Recovery/    ← utilitat de recuperació independent
│   └── OnaBages.Tests/       ← proves automàtiques
├── app/                      ← Electron
│   ├── main/                 ← main.js, preload.js
│   └── renderer/             ← index.html, css/, js/
└── tools/
    └── consola-proves.html   ← prova del motor des del navegador
```

# 5. Motor d'àudio

## 5.1. Dispositius

- Enumerar les sortides i les entrades actives, amb nom i identificador estable de Windows.
- Desar l'assignació pel **identificador**, i com a alternativa pel nom, perquè es pugui recuperar després d'un reinici o d'una reconnexió USB.
- Rebre els avisos de Windows de connexió, desconnexió i canvi de dispositiu predeterminat.
- **No fer servir mai «dispositiu predeterminat»** per a una sortida d'antena. Sempre s'assigna un dispositiu concret.

## 5.2. Model de rutes

Cada reproductor, la preescolta i cada enregistrador tenen una ruta:

```text
{ dispositiu: <id>, canals: [0, 1] }
```

Al MVP, `canals` sempre serà `[0, 1]` (estèreo). El camp hi és perquè en el futur es puguin fer servir dispositius multicanal sense canviar el model.

## 5.3. Independència dels fluxos

- Cada reproductor, la preescolta i cada enregistrador tenen el seu propi flux WASAPI.
- Un error en un flux (desconnexió, error del controlador) **no afecta els altres**.
- Polítiques quan un dispositiu es desconnecta:
  - **Reproductor:** passa a l'estat `Error: dispositiu no disponible` i conserva la pista i la posició. Quan el dispositiu torna, **no** reprèn sol; l'operador prem Play.
  - **Enregistrador:** tanca el fitxer de manera neta (queda vàlid), mostra un avís molt visible i ofereix «Continuar en un fitxer nou» amb un clic quan el dispositiu torna.
- El fil d'àudio no ha de crear objectes ni esperar operacions de disc o de xarxa. La lectura de fitxers i l'escriptura MP3 es fan en fils separats, amb memòria intermèdia.

## 5.4. Rellotges de dispositius diferents

Cap funció de l'aplicació barreja àudio de dispositius diferents: cada reproductor va a una sortida, cada enregistrador llegeix d'una entrada, i la preescolta té la seva pròpia sortida. Per tant, les petites diferències de velocitat entre targetes **no afecten** l'aplicació, i no cal cap sincronització. Si en el futur s'afegeix alguna funció que barregi fonts de dispositius diferents, caldrà revisar-ho.

## 5.5. Format

- Cada flux s'obre amb el format que té el dispositiu a Windows (mode compartit).
- La descodificació es remostreja, si cal, a la freqüència del dispositiu de sortida.
- Els enregistradors codifiquen a la freqüència de l'entrada (44,1 kHz o 48 kHz), sense remostrejar.

# 6. Reproductors

## 6.1. Concepte

Cada reproductor és una unitat independent, a l'estil de Winamp, amb:

- llista pròpia;
- sortida física pròpia;
- volum propi;
- controls i estat propis.

Nombre de reproductors configurable (inicialment fins a 6).

## 6.2. Controls i informació

- Play, Pausa, Stop, Següent.
- Volum (fader), amb indicació en dB.
- Temps transcorregut, temps restant (gran i ben visible) i durada.
- Pista actual i última pista reproduïda, marcades a la llista.
- Mesurador de nivell de sortida.
- **Avís de final de pista:** el temps restant canvia d'aspecte els últims 10 segons (valor configurable).
- **Dreceres de teclat** configurables (per exemple, una tecla per fer Play o Stop a cada reproductor).

## 6.3. Llista de reproducció

- Afegir fitxers des d'un diàleg o arrossegant-los des de l'Explorador.
- Reordenar arrossegant, i eliminar elements.
- Modificar la llista mentre sona, sense afectar la reproducció.
- Desar i carregar llistes (format JSON propi; importació de M3U).
- Avís si un fitxer de la llista no existeix.

Formats: MP3, WAV, AAC/M4A, FLAC, WMA (via Media Foundation) i OGG (via complement).

## 6.4. Avançament per pista

Cada element de la llista té un mode:

- **Automàtic (▶▶):** quan acaba, comença el següent.
- **Espera (⏸):** quan acaba, el reproductor queda en espera fins que l'operador prem Play o Següent.

El mode es desa amb l'element, es pot canviar en qualsevol moment amb un clic i es mostra amb icona, color i text.

**Regla de «pista següent»:** la pista següent és sempre la que hi ha **després de la pista actual en l'ordre actual de la llista** en el moment que acaba. Es calcula per identitat de l'element, no per posició numèrica. Si la pista actual s'ha eliminat de la llista, es fa servir la que ocupava el lloc següent.

## 6.5. Màquina d'estats

| Estat | Descripció | Accions possibles |
|---|---|---|
| `Buit` | No hi ha cap pista preparada | Carregar |
| `Preparat` | Pista carregada, a l'inici o en una posició | Play |
| `Reproduint` | Sona | Pausa, Stop, Següent |
| `Pausat` | Aturat a la posició actual | Play (continua), Stop |
| `Esperant` | Ha acabat una pista en mode Espera; la següent està preparada | Play, Següent |
| `Final` | Ha acabat l'última pista de la llista | Carregar, Play (des d'una pista triada) |
| `Error` | Dispositiu no disponible o fitxer il·legible | Reintentar, triar un altre dispositiu |

La màquina d'estats és al motor. La interfície només envia ordres i mostra l'estat que rep.

## 6.6. Historial de reproducció

Cada pista que sona s'afegeix a un fitxer CSV diari (hora, reproductor, fitxer, títol i artista si n'hi ha, durada reproduïda). És útil per declarar què s'ha emès i no necessita base de dades.

# 7. Preescolta

## 7.1. Objectiu

Permetre escoltar per auriculars qualsevol pista **sense que surti a antena**. Substitueix la pràctica actual de fer servir una instància de Winamp i silenciar-ne el canal a la taula.

## 7.2. Funcionament

- Un mòdul de preescolta amb **sortida pròpia** (dispositiu d'auriculars o d'escolta).
- Botó «Preescoltar» a cada element de qualsevol llista, i a la llista d'enregistraments recents.
- Controls: Play/Stop, barra de posició per saltar, volum propi i botons de salt ràpid (inici, −10 s, +10 s, últims 15 s).
- La preescolta **no modifica** l'estat de cap reproductor: no carrega pistes ni mou posicions.
- Indicació molt visible quan la preescolta està sonant.
- Avís si la sortida de preescolta coincideix amb la d'algun reproductor.

Qualsevol reproductor normal es pot continuar fent servir com a preescolta, si es prefereix el mètode actual.

# 8. Enregistradors

## 8.1. Objectiu

Enregistrar programes i fonts de manera tan senzilla i ràpida com mp3DirectCut: **Enregistrar → Aturar → Editar o Desar**, amb un clic a cada pas.

## 8.2. Configuració per enregistrador

- Entrada física.
- Nom (per exemple, «Programa», «Línia 2»), que s'utilitza en el nom del fitxer.
- Carpeta de treball.
- Format: **MP3, 320 kbps, CBR, estèreo**. És el valor per defecte; altres bitrates es poden configurar.

Nombre d'enregistradors configurable (inicialment fins a 4).

## 8.3. Flux d'operació

```text
  Inactiu ──[Enregistrar]──▶ Enregistrant ──[Aturar]──▶ Pendent
     ▲                                                   │
     └───────────── [Desar] · [Descartar] · [Editar] ────┘
                    [Nou enregistrament] (des de Pendent)
```

- **Enregistrar:** comença immediatament, sense diàlegs. El fitxer es crea a la carpeta de treball amb el nom `Nom_AAAA-MM-DD_HH-MM-SS.mp3`.
- **Aturar:** demana confirmació amb una segona pulsació o mantenint premut, per evitar aturades accidentals. El fitxer es tanca i l'enregistrador passa a **Pendent**.
- **Pendent** mostra el fitxer acabat amb tres accions:
  - **Editar:** l'obre a l'editor (fase 3).
  - **Desar:** diàleg amb el nom i la carpeta de destinació ja proposats. Mou o reanomena el fitxer.
  - **Descartar:** demana confirmació i l'envia a la paperera de Windows.
- **Nou enregistrament:** es pot començar des de Pendent sense desar l'anterior. L'anterior passa a la llista d'**enregistraments recents**, on continuen disponibles les mateixes accions.

Informació visible: estat (amb icona i text), temps transcorregut, mesurador d'entrada amb indicació de saturació, mida del fitxer i espai lliure al disc.

**Espai al disc:** a 320 kbps es fan servir uns 144 MB per hora. Si queda menys espai del configurat (per defecte, l'equivalent a 2 hores), es mostra un avís.

## 8.4. Escriptura directa en MP3

- L'àudio capturat es codifica amb LAME en temps real i s'escriu directament al fitxer MP3. No hi ha cap WAV intermedi.
- La codificació es fa en un fil separat de la captura, amb una memòria intermèdia de diversos segons. Si el disc va lent un moment, no es perd àudio.
- **Bolcat periòdic al disc:** cada pocs segons (per defecte, 2 s), es força l'escriptura física al disc (`Flush(true)`, que equival a `FlushFileBuffers`). En cas de fallada, com a màxim es perd l'àudio posterior a l'últim bolcat.
- **Fitxer vàlid en tot moment:** en CBR, cada fotograma MP3 es pot llegir sense necessitat de cap capçalera global. Cal desactivar l'etiqueta Xing/Info de LAME (no és necessària en CBR) o, si la biblioteca no ho permet, reescriure-la en tancar o recuperar el fitxer. Així, un fitxer interromput es pot reproduir fins a l'últim fotograma complet.

Aquest mètode substitueix l'escriptura segmentada de la v1.0: dona la mateixa protecció, però l'usuari treballa sempre amb un sol fitxer i no cal concatenar res. Si les proves de tancament forçat (fase 1) mostren algun problema, es tornarà a l'escriptura segmentada amb un sol codificador continu.

## 8.5. Recuperació

- En començar un enregistrament es crea un fitxer marcador `Nom_....mp3.rec` (JSON amb l'hora d'inici, el dispositiu i els paràmetres). S'elimina quan l'enregistrament s'atura correctament.
- **En arrencar**, el motor busca fitxers `.rec` a les carpetes de treball. Per a cada un:
  1. retalla el fitxer fins a l'últim fotograma MP3 complet;
  2. corregeix o elimina l'etiqueta Xing/Info;
  3. elimina el marcador;
  4. afegeix l'enregistrament a la llista de recents amb l'etiqueta **«Recuperat»**.
- **Utilitat independent** (`OnaBages.Recovery.exe`): fa la mateixa operació sobre un fitxer o una carpeta sense obrir l'aplicació. Comparteix el mateix codi.
- Com que el motor és un procés separat, **un tancament de la interfície no atura l'enregistrament**. La recuperació només és necessària si cau el motor o el sistema.

**Limitació:** cap mètode de programari protegeix contra un tall de corrent o una fallada del disc abans del bolcat. Es recomana un SAI.

# 9. Editor

## 9.1. Objectiu

Editar ràpidament un enregistrament acabat, com amb mp3DirectCut: retallar, treure fragments i desar, **sense tornar a codificar**.

## 9.2. Funcions

**Primera versió (fase 3):**
- Forma d'ona de tot el fitxer, amb zoom i desplaçament.
- Reproducció des de qualsevol punt (per la sortida de preescolta o una altra de configurable).
- Selecció d'un fragment, i reproducció de la selecció.
- Retallar l'inici, retallar el final i eliminar la selecció.
- Desfer i refer.
- Desar com a fitxer nou, o sobreescriure amb confirmació.

**Segona versió:**
- Fos d'entrada i de sortida.
- Marcadors.
- Normalització del volum del fitxer.

## 9.3. Edició sense pèrdua

- Un MP3 està format per fotogrames d'1.152 mostres: uns 26 ms a 44,1 kHz i uns 24 ms a 48 kHz. Els punts de tall s'ajusten al fotograma més proper. És la mateixa precisió que mp3DirectCut, i és suficient per a l'edició de programes.
- Retallar i eliminar = copiar fotogrames sencers a un fitxer nou. És instantani i no hi ha pèrdua de qualitat. NAudio permet llegir els fotogrames MP3 individualment.
- Els fosos requereixen descodificar i tornar a codificar només els fotogrames afectats. Es deixen per a la segona versió perquè cal provar els efectes a les vores (bit reservoir del MP3).
- La forma d'ona es calcula descodificant el fitxer un sol cop i es desa en un fitxer de pics al costat de l'MP3, perquè les obertures següents siguin immediates.

## 9.4. Convivència amb els enregistraments

- L'editor treballa en un fil o procés de baixa prioritat.
- Mai no obre un fitxer que s'està enregistrant.
- Cal provar-ho: editar un fitxer d'1 hora mentre dos enregistradors funcionen, sense cap pèrdua.

# 10. Processament d'àudio i sonoritat

## 10.1. Què és la normalització de sonoritat (LUFS)

El **LUFS** és una unitat que mesura el **volum que percep l'oïda**, no només els pics del senyal. Dues cançons amb el mateix nivell de pic poden sonar una molt més forta que l'altra. La normalització de sonoritat analitza cada pista una vegada i hi aplica un guany perquè **totes sonin aproximadament igual de fortes**, sense que l'operador hagi de tocar els faders a cada canvi. És la mateixa idea que el *ReplayGain* de Winamp. L'estàndard de referència és l'EBU R128.

## 10.2. Proposta

| Funció | Fase | Comentari |
|---|---|---|
| Volum per reproductor | MVP | Imprescindible |
| Normalització de sonoritat per pista (opcional per reproductor) | 4 | Anàlisi en segon pla en afegir la pista; el valor es desa a la memòria cau |
| Equalitzador per reproductor (paramètric, 3–5 bandes) | 4 | Filtres de NAudio |
| Limitador per reproductor (sostre configurable) | 4 | Protecció contra pics |
| Compressor per reproductor | 4 | Threshold, ratio, attack, release, makeup; indicador de reducció |
| Presets de processament | 4 | Desats al perfil |

**Cadena:** fitxer → descodificació → guany de sonoritat → EQ → compressor → limitador → volum → sortida.

Els canvis de paràmetres s'han d'aplicar de manera progressiva (sense clics). El processament d'antena principal probablement ja el fa la taula o el processador d'emissió; el de l'aplicació és un complement.

# 11. Configuració i perfils

## 11.1. MVP

- Una sola **configuració de treball**, desada automàticament en JSON a `%APPDATA%\OnaBages\`:
  - dispositius i noms dels reproductors, la preescolta i els enregistradors;
  - carpetes de treball i de destinació;
  - dreceres de teclat;
  - últimes llistes de cada reproductor (es recuperen en arrencar).
- Escriptura segura: es desa en un fitxer temporal i després se substitueix l'original, perquè un tancament inesperat no deixi la configuració malmesa.

## 11.2. Perfils (fase 4)

- Un perfil = una carpeta amb fitxers JSON: distribució de la pantalla, reproductors, enregistradors, rutes, llistes, processament i dreceres.
- Operacions: crear, duplicar (copiar la carpeta), canviar, eliminar, exportar i importar (un fitxer .zip).
- Els perfils guarden **referències** als fitxers d'àudio, mai còpies.
- **Canvi de perfil segur:** no es permet amb reproduccions o enregistraments actius sense confirmació explícita, i mai s'atura un enregistrament sense avís. Si falten dispositius, s'avisa i l'element corresponent queda en estat `Error`.

## 11.3. Usuaris

Fora de l'abast inicial. Si algun dia cal, es podrà afegir un usuari per sessió (fitxer `usuaris.json`) amb preferències pròpies, perfil per defecte i, opcionalment, un PIN desat només com a *hash*. Un PIN serveix per identificar qui opera, no per protegir dades: en un ordinador local, qui té accés als fitxers ho pot llegir tot. No es preveu un sistema de permisos.

## 11.4. Capa de dades

Per poder passar de JSON a SQLite en el futur sense reescriure l'aplicació:

1. **Tot l'accés a dades persistents passa per un sol mòdul del motor** (*repositori*). La resta del codi no llegeix ni escriu fitxers de configuració directament.
2. **Cada fitxer JSON porta un camp `schemaVersion`.** En llegir un fitxer antic, el repositori el converteix al format actual (migració).
3. **Escriptura atòmica:** fitxer temporal + substitució.

JSON és suficient per a configuració, perfils, llistes i uns quants usuaris. SQLite caldria si apareix:
- una biblioteca musical gran amb cerques i filtres;
- un historial amb consultes («què ha sonat el mes passat al programa X»);
- dades compartides entre diversos ordinadors.

Les dues opcions poden conviure: configuració i perfils en JSON (fàcils de copiar i exportar), i biblioteca o historial en SQLite, sempre gestionat pel motor.

# 12. Interfície

## 12.1. Criteris

- Tema fosc per defecte (Bootstrap 5, mode de color fosc), amb tema clar opcional.
- Pensada per a una pantalla de 1920 × 1080; usable també a 1366 × 768.
- **Escala de la interfície (zoom)** configurable (per exemple del 70 % al 150 %): fa més grans o més petits tots els elements alhora (lletra, icones, botons). Es fa amb el zoom d'Electron (`setZoomFactor`), amb dreceres Ctrl+/Ctrl−/Ctrl+0, i el valor es desa a la configuració. El CSS fa servir `rem` en lloc de píxels fixos. Fase 2.
- Elements grans per a les accions de directe: Play, Stop, Enregistrar i temps restant.
- Estats amb color, icona i text.
- Confirmació per a les accions que interrompen: Stop d'un reproductor que sona (configurable), aturar un enregistrament, tancar l'aplicació.

## 12.2. Distribució del MVP

```text
┌──────────────────────────────────────────────────────────────┐
│ Barra superior: rellotge gran · estat del motor · perfil     │
├───────────────┬───────────────┬───────────────┬──────────────┤
│ Reproductor 1 │ Reproductor 2 │ Reproductor 3 │ Preescolta   │
│ (llista)      │ (llista)      │ (llista)      │              │
├───────────────┴───────────────┴───────────────┼──────────────┤
│ Enregistrador 1 · Enregistrador 2 · ...       │ Recents      │
└───────────────────────────────────────────────┴──────────────┘
```

- Distribució fixa amb la graella de Bootstrap, amb nombre de reproductors i enregistradors configurable.
- **Fase 4:** panells que es poden moure i redimensionar (GridStack.js), amb la distribució desada al perfil.

# 13. Dades i fitxers

| Dada | Format | Ubicació |
|---|---|---|
| Configuració i perfils | JSON | `%APPDATA%\OnaBages\` |
| Fitxer d'execució del motor (port, testimoni) | JSON | `%APPDATA%\OnaBages\engine.json` |
| Llistes de reproducció | JSON propi (importació M3U) | On triï l'usuari i dins del perfil |
| Historial de reproducció | CSV diari | `%APPDATA%\OnaBages\historial\` |
| Enregistraments | MP3 | Carpetes de treball i de destinació |
| Marcadors de recuperació | `.rec` (JSON) | Al costat de l'enregistrament |
| Formes d'ona | Fitxer de pics | Al costat de l'MP3 o a la memòria cau |
| Anàlisi de sonoritat | JSON (memòria cau) | `%APPDATA%\OnaBages\cache\` |
| Registre tècnic del motor | Text, rotatiu | `%APPDATA%\OnaBages\logs\` |

# 14. Pla de desenvolupament

## 14.1. Metodologia amb Claude Code

Totes les fases es desenvolupen amb VS Code i Claude Code.

- **`CLAUDE.md`** al repositori amb les regles bàsiques del projecte: arquitectura, prohibicions (res d'àudio a la interfície, cap espera de disc al fil d'àudio, el motor és la font de veritat), convencions de noms, idioma i com executar les proves.
- **Passos petits i verificables:** cada petició a Claude Code ha d'acabar amb una cosa que es pot executar i provar.
- Un commit de Git per cada pas que funciona, per poder tornar enrere.
- `docs/decisions.md`: una línia per cada decisió presa, amb el motiu.
- Proves automàtiques a les parts amb lògica: màquina d'estats dels reproductors, regla de pista següent, recuperació d'MP3 i edició per fotogrames.
- **Proves d'àudio automàtiques:** reproduir un to de prova, enregistrar-lo i analitzar el fitxer per detectar talls, clics o silencis. Així les proves llargues es validen soles.
- Claude Code no pot escoltar, connectar dispositius ni veure la finestra: el desenvolupador fa aquestes proves i n'hi explica el resultat (o hi enganxa captures de pantalla).

## 14.2. Eines

- Visual Studio Code + Claude Code.
- .NET 10 SDK i l'extensió C# (la C# Dev Kit és opcional).
- Node.js LTS.
- Git.

No calen Visual Studio Build Tools, MSVC ni CMake.

## 14.3. Fases

### Fase 0 — Preparació
- Crear el repositori, l'estructura de carpetes i `CLAUDE.md`.
- Esquelet mínim: el motor arrenca i accepta connexions WebSocket; la interfície s'hi connecta i mostra l'estat.
- Fer l'inventari del maquinari de cada ordinador de l'emissora: dispositius, controladors, formats.
- Aplicar la preparació de Windows (3.3).

**Criteri:** el projecte compila, la interfície mostra «Motor connectat», i el maquinari està documentat.

### Fase 1 — Prototip del motor (sense interfície)
- Enumerar dispositius.
- Reproduir MP3 i WAV per un dispositiu triat.
- Reproduir dues pistes simultànies per dues sortides diferents.
- Enregistrar una entrada directament a MP3 320 CBR, amb bolcat periòdic.
- Enregistrar dues entrades alhora.
- Proves: matar el procés durant un enregistrament (`taskkill /f`) i comprovar que el fitxer es pot reproduir; desconnectar i reconnectar un USB.
- Control des de `tools/consola-proves.html` via WebSocket.

**Criteri:** 3 hores de reproducció i enregistrament simultanis sense talls amb el maquinari real, i enregistraments vàlids després d'un tancament forçat.

### Fase 2 — MVP utilitzable en directe
- Interfície Electron + Bootstrap.
- Reproductors amb llistes, controls, avançament automàtic o espera, avís de final de pista i dreceres.
- Preescolta.
- Enregistradors amb el flux Enregistrar → Aturar → Pendent → Desar/Descartar, i llista de recents.
- Recuperació automàtica en arrencar i utilitat de recuperació independent.
- Configuració desada en JSON. Historial CSV.
- Instal·lador.

**Criteri:** fer un programa real complet amb Ona Bages Play, primer en paral·lel amb Winamp i després substituint-lo.

### Fase 3 — Editor
- Forma d'ona, selecció, reproducció, retallar inici i final, eliminar fragment, desfer, desar sense recodificar.

**Criteri:** editar i desar un enregistrament d'1 hora en pocs minuts, mentre un altre enregistrador funciona, sense pèrdues.

### Fase 4 — Processament, perfils i distribució flexible
- Normalització de sonoritat, EQ, limitador, compressor i presets.
- Perfils amb exportació i importació.
- Panells configurables.
- Fosos a l'editor.

### Fase 5 — Ampliacions segons l'ús real
Candidates: botonera de talls (cartwall), detector de silenci, punts cue d'inici i final, encadenament amb fos entre pistes, retall automàtic de silencis inicials i finals, biblioteca musical amb cerca, inicis programats, control MIDI o per *fader start*, usuaris.

# 15. Proves i criteris de qualitat

**Reproducció**
- 4 reproductors simultanis durant 3 hores, sense talls.
- Modificar llistes mentre sonen.
- Avançament automàtic i en espera, incloent-hi les llistes editades durant la reproducció.
- Desconnexió i reconnexió USB durant la reproducció: els altres reproductors no s'aturen.

**Enregistrament**
- 2–3 enregistradors simultanis durant 4 hores.
- Tancament forçat del motor, de la interfície i de tot el sistema: verificar la recuperació en cada cas.
- Verificar els fitxers MP3 resultants amb diversos reproductors (Winamp, VLC, mp3DirectCut).
- Disc gairebé ple: l'avís salta a temps.

**Editor**
- Edició d'un fitxer llarg amb enregistraments actius.
- Comparar el resultat amb el mateix tall fet a mp3DirectCut.

**Interfície**
- Tancar i tornar a obrir la interfície amb reproduccions i enregistraments actius: es reconnecta i mostra l'estat correcte.
- Resolucions de 1920 × 1080 i 1366 × 768.

**Rendiment**
- Mesurar CPU i memòria del motor i de la interfície en una sessió de 4 hores. Les xifres objectiu es fixaran després de la fase 1.

# 16. Riscos

| Risc | Impacte | Mitigació |
|---|---|---|
| Controladors de consum inestables o amb efectes propis (Creative, Audigy antiga) | Talls, so alterat | Preparació de Windows (3.3); proves a la fase 1 amb cada targeta |
| Pauses del gestor de memòria de .NET al fil d'àudio | Clics | Camí d'àudio sense creació d'objectes; memòria intermèdia adequada; proves llargues |
| Fallada del motor durant un enregistrament | Pèrdua dels últims segons | Bolcat cada 2 s; marcador `.rec`; recuperació automàtica |
| Precisió dels talls MP3 (~25 ms) | Talls poc precisos | Suficient per a la parla; igual que mp3DirectCut |
| Abast massa ampli per a una sola persona | Projecte que no s'acaba | MVP estricte; fases amb criteri de validació; ampliacions només després de l'ús real |
| Dispositius USB que canvien d'identificador | Reproductor sense sortida en arrencar | Identificació per id i per nom; avís clar |
| Port WebSocket local accessible per altres programes | Control no autoritzat | Només `127.0.0.1` + testimoni per sessió |

# 17. Decisions acordades

1. Aplicació d'escriptori per a Windows, només d'àudio.
2. Orientada a programes en directe amb operador, de diverses hores; no és un sistema 24/7 desatès.
3. Distribució gratuïta, per a la pròpia emissora i altres emissores.
4. Desenvolupament individual i progressiu, començant pel motor d'àudio.
5. Reproductors independents, cadascun amb sortida pròpia (dispositius estèreo separats).
6. Llistes editables durant la reproducció.
7. Avançament automàtic o en espera, configurat per pista.
8. Preescolta amb sortida dedicada.
9. Diversos enregistradors simultanis.
10. **Enregistrament directe en MP3**, per defecte 320 kbps CBR estèreo, sense WAV intermedi.
11. Enregistrament manual i àgil: Enregistrar → Aturar → Editar / Desar / Nou.
12. Recuperació dels enregistraments, també amb una utilitat independent.
13. Editor ràpid inspirat en mp3DirectCut, que pot treballar mentre altres enregistradors funcionen.
14. Prioritat de fases: reproductors i enregistradors primer, editor després.
15. EQ, compressor, limitador i perfils en una fase posterior.
16. Motor en C#/.NET 10 + NAudio; interfície HTML + Bootstrap + JavaScript dins d'Electron; comunicació per WebSocket local; configuració en JSON amb capa de dades migrable.
17. Desenvolupament de totes les fases amb VS Code i Claude Code.

# 18. Aspectes pendents

- Freqüència de mostreig de treball (44,1 o 48 kHz), segons el que doni la sortida USB de la taula.
- Nombre màxim de reproductors i enregistradors (proposta: 6 i 4).
- Mapa de dreceres de teclat per defecte.
- Dispositiu de preescolta de cada estudi.
- Convenció de noms i carpetes de destinació dels enregistraments.
- Llicència del codi de l'aplicació (codi obert o només binari gratuït).
- Si l'Audigy es manté o se substitueix, segons les proves de la fase 1.

# 19. Glossari

- **CBR:** bitrate constant. Cada fotograma MP3 té la mateixa mida.
- **Fotograma MP3:** unitat mínima d'un fitxer MP3 (1.152 mostres, uns 24–26 ms).
- **LUFS:** unitat de sonoritat percebuda (EBU R128).
- **Mode compartit (WASAPI):** diverses aplicacions poden fer servir el mateix dispositiu; Windows en fixa el format.
- **MVP:** producte mínim viable, la primera versió útil en un programa real.
- **Preescolta (PFL / cue):** escoltar un àudio per auriculars sense que surti a antena.
- **WebSocket:** canal de comunicació bidireccional entre la interfície i el motor.
