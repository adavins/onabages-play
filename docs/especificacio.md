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

El maquinari **varia segons l'emissora**. L'únic que es pot donar per segur és l'**àudio integrat a la placa base** (una entrada i una sortida). Per tant:

- **Configuració mínima garantida:** l'aplicació ha de funcionar amb només la targeta integrada.
- Diversos reproductors poden compartir una mateixa sortida: cada un manté el seu flux independent i Windows els barreja (mode compartit).
- Si la **preescolta comparteix sortida** amb algun reproductor, es permet, però la interfície mostra un avís clar i permanent: «La preescolta surt per antena».
- La llista anterior és el maquinari de l'emissora de referència i de les proves, no un requisit.
- Els ordinadors són **domèstics, sovint amb recursos limitats**, i s'hi poden afegir o treure targetes en qualsevol moment. L'aplicació detecta el maquinari a cada arrencada i avisa dels canvis (vegeu 5.6).

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

## 5.6. Detecció del maquinari en arrencar

Els ordinadors són domèstics i el maquinari **no és fix**: s'hi poden posar i treure targetes i dispositius USB entre una sessió i l'altra. L'aplicació no pot suposar que troba el mateix que la vegada anterior.

**En arrencar**, el motor enumera totes les entrades i sortides actives i les compara amb les assignacions desades. Per a cada reproductor, la preescolta i cada enregistrador:

| Situació | Què fa l'aplicació |
|---|---|
| El dispositiu hi és (mateix identificador) | Res. |
| L'identificador ha canviat però hi ha **un sol** dispositiu amb el mateix nom (per exemple, un USB en un altre port) | Es considera el mateix dispositiu: s'actualitza l'identificador i es mostra un avís informatiu. |
| Hi ha **diversos** dispositius amb el mateix nom (per exemple, dues targetes USB iguals) | No es tria cap: l'element queda `Sense dispositiu` i l'operador n'ha de triar un. |
| El dispositiu no hi és | L'element queda en estat `Sense dispositiu`, amb un avís. Conserva la llista, la pista i la resta de configuració. |

- **L'aplicació no reassigna mai automàticament** un element a un altre dispositiu. Un element `Sense dispositiu` no es pot reproduir ni enregistrar fins que l'operador en tria un a la configuració.
- **Dispositius nous** que no estaven assignats a res: avís informatiu («Dispositiu nou disponible: …»).
- **Format canviat** a Windows (per exemple, de 44,1 a 48 kHz): avís informatiu; el flux s'adapta sol (5.5).
- **Resum en arrencar:** si hi ha hagut algun canvi, la interfície mostra una finestra «Canvis en el maquinari» amb una línia per element (abans, ara, què ha passat) i un botó per anar a la configuració de dispositius. Si no hi ha canvis, no es mostra res.
- **Primera arrencada** (o sense configuració): s'obre la configuració de dispositius amb una proposta d'assignació a partir del que s'ha detectat. Amb un sol dispositiu de sortida, la proposta ho posa tot a la mateixa sortida, amb l'avís de preescolta de 3.2. **No s'aplica res fins que l'operador ho confirma.**
- **Durant l'execució**, s'apliquen les polítiques de 5.3. Quan un dispositiu que faltava torna (mateix identificador o nom únic), es torna a associar al seu element, però un reproductor **no reprèn sol**.
- Tots els avisos de maquinari queden al registre tècnic i a una llista d'avisos que es pot consultar a la interfície.
- La detecció es fa al motor (fase 1); el resum, la configuració i la llista d'avisos, a la interfície (fase 2). Les ordres i els esdeveniments corresponents s'afegiran a `docs/protocol.md` quan s'implementin.

# 6. Reproductors

## 6.1. Concepte

Cada reproductor és una unitat independent, a l'estil de Winamp, amb:

- llista pròpia;
- sortida física pròpia;
- volum propi;
- controls i estat propis.

Nombre de reproductors configurable (inicialment fins a 6).

## 6.2. Controls i informació

- Play, Pausa, Stop, Següent, i tornar a començar la pista.
- **Barra de posició:** clicar-la o arrossegar-la per saltar a un altre punt de la pista, també mentre sona.
- Mentre està en pausa, el botó de Pausa parpelleja.
- Volum (fader), amb indicació en dB.
- Temps transcorregut, temps restant (gran i ben visible) i durada.
- Pista actual i última pista reproduïda, marcades a la llista.
- Mesurador de nivell de sortida.
- **Avís de final de pista:** el temps restant canvia d'aspecte els últims 10 segons (valor configurable).
- **Dreceres de teclat**: a la fase 2, un mapa fix (F1, F2… Play/Pausa de cada reproductor; Maj+F1, Maj+F2… Stop; D-041). Poder-les canviar queda per a una fase posterior.

## 6.3. Llista de reproducció

Definida pel desenvolupador en provar la fase 2 (D-031): es conserva el que funciona de Winamp i s'hi afegeixen marques per pista.

- **Afegir:** arrossegant fitxers (un o diversos) a sobre de la llista, que queden inserits on es deixen anar; o amb el diàleg «Obrir» (al final).
- **Reordenar** arrossegant, també diverses pistes seleccionades i també la que sona. **Esborrar** amb la tecla Supr. Tot es pot fer mentre sona, sense afectar la reproducció.
- **Selecció:** un clic només selecciona (fons diferent); Ctrl+clic i Maj+clic, diverses.
- **Doble clic:** atura la pista que sona (si n'hi ha) i fa sonar la triada.
- **Cada fila:** número de posició, botons de bucle, cue i avanç automàtic (només icones), nom del fitxer **sense extensió** (no es fan servir les metadades) i durada.
- **Durada total** de la llista, que s'actualitza sola.
- **Colors:** la que sona, en verd; l'última reproduïda (quan s'atura), en blau; la seleccionada, una mica més clara o més fosca.
- **Menú del botó dret:** informació (finestra amb les metadades) i «Mostrar a l'Explorador».
- **Seguretat:** en afegir una pista, el motor la comprova (l'obre i en llegeix el principi). Si és malmesa o el format no és compatible, es mostra igualment però amb un fons diferent i una icona ⚠ que diu el motiu. **Si una pista no té cap avís, sonarà.** Cada 30 s es comprova en segon pla que els fitxers encara existeixen.
- **Desar i obrir llistes** en format propi `.obpl` (JSON, amb la ruta completa i la relativa de cada pista, i les marques de bucle i avanç automàtic) i **importar M3U/M3U8**. Una llista arrossegada a sobre de la llista hi insereix les seves pistes. Una **carpeta** arrossegada hi insereix tots els seus fitxers d'àudio, també els de les subcarpetes, en l'ordre de l'Explorador. L'última llista de cada reproductor es desa sola i es recupera en arrencar (D-035).

- **Separadors de secció** (D-042): files amb el nom d'una secció del programa (p. ex. «ESPORTS», «CINEMA») i un color opcional, per veure quines pistes corresponen a cada secció. No sonen mai (Play, el cue i l'avanç automàtic se'ls salten), mostren quantes pistes i quanta durada té la secció, i es poden plegar per escurçar la llista. El reproductor mostra la secció de la pista que sona. Els fitxers «______.mp3» de les llistes del Winamp es converteixen sols en separadors.

Formats: MP3, WAV, AAC/M4A, FLAC, WMA (via Media Foundation) i OGG (via complement).

El reproductor mostra el format de la pista que sona, abreujat (p. ex. «MP3 192 kbps estèreo 44,1 kHz»).

## 6.4. Avançament

- **Per defecte, l'avanç és manual:** quan s'acaba la pista o es prem Stop, la llista s'atura i la pista queda marcada com a última reproduïda.
- **Play** fa sonar la pista marcada amb **cue** o, si no n'hi ha, la que va després de l'última reproduïda.
- Marques per pista (es poden canviar en qualsevol moment, també mentre sona):
  - **Bucle:** la pista es repeteix fins que es prem Stop. No pot anar amb l'avanç automàtic: activar el bucle l'apaga, i amb el bucle activat no es pot activar (D-034).
  - **Cue:** aquesta serà la següent. Només n'hi pot haver una per llista; s'apaga quan comença a sonar.
  - **Avanç automàtic:** quan aquesta pista s'acaba, sona la del cue o, si no n'hi ha, la següent. Només un pas: continua si la que comença també té l'avanç automàtic. Per precaució, quan ja ha passat a la següent, es desmarca sol (D-033).
- Amb bucle o avanç automàtic no hi ha silenci entre pistes: el motor prepara la següent i decideix quina és 1 s abans del final.

**Regla de «pista següent»:** la pista següent és sempre la que hi ha **després de la pista actual en l'ordre actual de la llista** en el moment que acaba. Es calcula per identitat de l'element, no per posició numèrica. Si la pista actual s'ha eliminat de la llista, es fa servir la que ocupava el lloc següent.

## 6.5. Màquina d'estats

| Estat | Descripció | Accions possibles |
|---|---|---|
| `Buit` | Encara no ha sonat res | Play (la del cue o la primera), doble clic |
| `Preparat` | Pista preparada en una posició (p. ex. després de Reintentar) | Play |
| `Reproduint` | Sona | Pausa, Stop, saltar, doble clic |
| `Pausat` | Aturat a la posició actual | Play (continua), Stop, saltar |
| `Aturat` | La pista s'ha acabat (avanç manual) o s'ha premut Stop | Play (la del cue o la següent), doble clic |
| `Error` | Dispositiu no disponible o fitxer il·legible | Reintentar, triar un altre dispositiu |

La màquina d'estats és al motor. La interfície només envia ordres i mostra l'estat que rep.

## 6.6. Historial de reproducció

Cada pista que sona s'afegeix a un fitxer CSV diari (hora, **programa**, reproductor, fitxer, títol i artista si n'hi ha, durada reproduïda). És útil per declarar què s'ha emès i no necessita base de dades.

- El **programa** és el nom que es posa a ⚙ Configuració (p. ex. «Magazín dels dissabtes»); també surt a la barra superior. Permet filtrar què ha sonat a cada programa. Quan hi hagi perfils (fase 4), serà el nom del perfil o un camp del perfil.
- Només compta el que surt per antena (reproductors, no la preescolta), i només les pistes que han sonat almenys 2 segons. La durada reproduïda no compta les pauses.
- Format pensat per a l'Excel en català: separador «;», UTF-8 amb BOM, durades `hh:mm:ss`. Detalls a D-043.

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
- **Volum d'enregistrament**: de −∞ a +12 dB, per defecte 0 dB. S'aplica abans del mesurador i del fitxer (D-036).

Nombre d'enregistradors configurable: fins a 6 (fase 5, D-101; abans, 4), per exemple en dues files de 3.

## 8.3. Flux d'operació

```text
  Inactiu ──[Enregistrar]──▶ Enregistrant ──[Aturar]──▶ Pendent
     ▲                                                   │
     └───────────── [Desar] · [Descartar] · [Editar] ────┘
                    [Nou enregistrament] (des de Pendent)
```

- **Enregistrar:** comença immediatament, sense diàlegs, sempre en un **fitxer nou** (com a mp3DirectCut). El fitxer es crea a la carpeta de treball amb el nom `Nom_AAAA-MM-DD_HH-MM-SS.mp3`; si ja existeix, s'hi afegeix `_2`, `_3`... Mai no se sobreescriu res. Mentre s'enregistra, no se'n pot començar un altre al mateix enregistrador.
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

  També busca a la carpeta per defecte i a les carpetes dels enregistraments de la llista. Mai no toca un fitxer que un altre programa té obert (un enregistrament que continua), ni un fitxer on no trobi cap fotograma MP3: en aquest cas el marcador es manté i queda escrit al registre (D-038).
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
- Fos d'entrada i de sortida. Fet (fase 4, D-073–D-075): sobre la selecció, sense recodificar, amb els botons «Fos d'entrada» i «Fos de sortida» (tecles E i S); es veuen a l'ona i se senten a la previsualització.
- Marcadors. Fet (fase 5, D-110–D-119): marques posades mentre s'enregistra (botó MARCA, amb nom opcional), que es veuen i es mouen a l'editor, per seleccionar i desar el tros entre marques; també amb l'enregistrament en curs.
- Normalització del volum del fitxer. Fet (fase 5, D-123, D-124): per pic, a la selecció o a tot el fitxer, sense recodificar (passos de 1,5 dB, sense passar mai de l'objectiu; per defecte −1 dBFS).

## 9.3. Edició sense pèrdua

- Un MP3 està format per fotogrames d'1.152 mostres: uns 26 ms a 44,1 kHz i uns 24 ms a 48 kHz. Els punts de tall s'ajusten al fotograma més proper. És la mateixa precisió que mp3DirectCut, i és suficient per a l'edició de programes.
- Retallar i eliminar = copiar fotogrames sencers a un fitxer nou. És instantani i no hi ha pèrdua de qualitat. NAudio permet llegir els fotogrames MP3 individualment.
- Els fosos (i, més endavant, la normalització del volum del fitxer) es fan **sense tornar a codificar** (D-073), com el mp3DirectCut: es canvia el volum global (*global_gain*) de cada grànul de 576 mostres (uns 12 ms), en passos de 1,5 dB. No es perd qualitat, no hi ha clics a les vores (no hi intervé el *bit reservoir*), la mida del fitxer no canvia i desar continua sent immediat.
- La forma d'ona es calcula descodificant el fitxer un sol cop i es desa en un fitxer de pics a la memòria cau (`cache\formes-ona\` de la carpeta de dades; no al costat de l'MP3, per no omplir les carpetes d'enregistraments), perquè les obertures següents siguin immediates (D-050).

## 9.4. Convivència amb els enregistraments

- L'editor treballa en un fil o procés de baixa prioritat.
- Mai no **escriu** en un fitxer que s'està enregistrant. Des de la fase 5 (D-117) el pot obrir, però només en llegeix una fotografia (fins on era en obrir-lo): per escoltar-lo, moure'n les marques i desar-ne trossos en fitxers nous; no s'hi pot retallar, fer fosos ni desar a sobre.
- Cal provar-ho: editar un fitxer d'1 hora mentre dos enregistradors funcionen, sense cap pèrdua.
  - Prova automàtica: `OnaBages.ToneCheck convivencia [minuts] [carpeta]` (D-053). Dos enregistradors amb una entrada simulada en temps real mentre l'editor obre, fa la forma d'ona, talla i desa un MP3 d'1 hora; després comprova que els enregistraments no tenen talls ni mostres perdudes.
  - Prova amb el maquinari real: la fa el desenvolupador des de l'aplicació (vegeu D-053).

# 10. Processament d'àudio i sonoritat

## 10.1. Què és la normalització de sonoritat (LUFS)

El **LUFS** és una unitat que mesura el **volum que percep l'oïda**, no només els pics del senyal. Dues cançons amb el mateix nivell de pic poden sonar una molt més forta que l'altra. La normalització de sonoritat analitza cada pista una vegada i hi aplica un guany perquè **totes sonin aproximadament igual de fortes**, sense que l'operador hagi de tocar els faders a cada canvi. És la mateixa idea que el *ReplayGain* de Winamp. L'estàndard de referència és l'EBU R128.

## 10.2. Proposta

| Funció | Fase | Comentari |
|---|---|---|
| Volum per reproductor | MVP | Imprescindible |
| Normalització de sonoritat per pista (opcional per reproductor) | 4 | Anàlisi en segon pla en afegir la pista; el valor es desa a la memòria cau. Fet al motor (D-070, D-071): objectiu −12 LUFS, com a molt +10 dB, a partir de la pista següent; desactivada per defecte; botó NORM a cada reproductor i objectiu a ⚙ (D-072) |
| Equalitzador per reproductor (paramètric, 3–5 bandes) | 4 → futur | **Ajornat** (D-073): no cal de moment. Quan es faci, abans del compressor (cadena de 10.2) |
| Limitador per reproductor (sostre configurable) | 4 | Protecció contra pics |
| Compressor per reproductor | 4 | Threshold, ratio, attack, release, makeup; indicador de reducció. **Es fa primer** (D-066): com el plugin SqrSoft del Winamp que es fa servir ara, amb el preset «SqrSoft La Central». Fet (D-066–D-068): botó DSP a cada reproductor, finestra d'ajustos i indicador de reducció; el limitador (sostre) va inclòs |
| Presets de processament | 4 | Desats al perfil |

**Cadena:** fitxer → descodificació → guany de sonoritat → EQ → compressor → limitador → volum → sortida.

Els canvis de paràmetres s'han d'aplicar de manera progressiva (sense clics). El processament d'antena principal probablement ja el fa la taula o el processador d'emissió; el de l'aplicació és un complement.

# 11. Configuració i perfils

## 11.1. MVP

- Una sola **configuració de treball**, desada automàticament en JSON a `%APPDATA%\OnaBages\`:
  - dispositius i noms dels reproductors, la preescolta i els enregistradors;
  - carpetes de treball i de destinació;
  - dreceres de teclat;
  - últimes llistes de cada reproductor (es recuperen en arrencar);
  - nom del programa (barra superior i historial, 6.6).
- Escriptura segura: es desa en un fitxer temporal i després se substitueix l'original, perquè un tancament inesperat no deixi la configuració malmesa.

## 11.2. Perfils (fase 4)

- Un perfil = una carpeta amb fitxers JSON: distribució de la pantalla, reproductors, enregistradors, rutes, llistes, processament i dreceres.
  - Fet (pas 1a, D-057): `perfils\<nom>\perfil.json` (dispositius, enregistradors, preescolta, distribució i nom del programa) i `perfils\<nom>\llistes\` (últimes llistes). El que és de l'ordinador es queda a `config.json`. La configuració d'abans es converteix sola en el primer perfil.
  - Fet (pas 1b, D-058): menú del perfil a la barra superior per canviar de perfil, crear-ne un de buit, duplicar l'actual, canviar-li el nom i eliminar-ne un altre. Canviar de perfil reinicia el motor (uns 3 s).
  - Fet (pas 1c, D-061): exportar un perfil en un `.zip` i importar-lo com a perfil nou.
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
- Confirmació per a les accions que interrompen: aturar un enregistrament i tancar l'aplicació. El Stop d'un reproductor **no** demana confirmació (D-028).

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
- **Panell lateral (fase 4, pas 2b; D-062):** la preescolta i Recents passen a un panell lateral a la dreta, plegat per defecte (una franja d'icones: Àudios, Llistes i Recents, i el botó de desplegar, que avisa si la preescolta sona amb el panell plegat). Obert, mostra la pestanya triada i, a baix, la preescolta. En obrir-lo la finestra es fa més ampla, i els reproductors i enregistradors no canvien de mida. S'obre sol quan la preescolta es posa a sonar. Els reproductors i enregistradors ocupen tota l'amplada de la distribució.

# 13. Dades i fitxers

| Dada | Format | Ubicació |
|---|---|---|
| Configuració de l'ordinador (zoom, dispositius coneguts, sortida de l'editor, perfil actiu) | JSON | `%APPDATA%\OnaBages\config.json` (versió 2, D-057) |
| Perfils (configuració de treball de cada programa i últimes llistes dels reproductors) | JSON | `%APPDATA%\OnaBages\perfils\<nom>\perfil.json` i `...\llistes\` |
| Fitxer d'execució del motor (port, testimoni) | JSON | `%APPDATA%\OnaBages\engine.json` |
| Llistes de reproducció | JSON propi (importació M3U) | On triï l'usuari i dins del perfil |
| Historial de reproducció | CSV diari | `%APPDATA%\OnaBages\historial\` |
| Enregistraments | MP3 | Carpetes de treball i de destinació |
| Marcadors de recuperació | `.rec` (JSON) | Al costat de l'enregistrament |
| Marques d'un enregistrament (D-110) | `.marques` (JSON) | Al costat de l'enregistrament (i de la còpia de l'historial) |
| Formes d'ona | Fitxer de pics (`.obpk`), es guarden 60 dies des de l'últim ús | `%APPDATA%\OnaBages\cache\formes-ona\` |
| Anàlisi de sonoritat | JSON (memòria cau) | `%APPDATA%\OnaBages\cache\` |
| Registre tècnic del motor | Text, un fitxer per dia (`motor-AAAA-MM-DD.log`), es guarden 30 dies (D-045) | `%APPDATA%\OnaBages\logs\` |

**Mode portable (D-044):** si al costat de l'executable hi ha una carpeta `dades\`, tot el que aquí diu `%APPDATA%\OnaBages\` es desa en aquesta carpeta (també la memòria cau d'Electron). Així l'aplicació es pot copiar a un altre ordinador o sobreviure a una reinstal·lació de Windows amb tota la seva configuració. Els dispositius es tornen a buscar pel nom (5.6) i els fitxers d'àudio, per la ruta relativa de les llistes `.obpl` quan la completa no hi és.

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
- Fer l'inventari del maquinari dels ordinadors de proves (els que es faran servir a la fase 1): dispositius, controladors, formats. A cada emissora, els dispositius es trien des de la configuració de l'aplicació.
- Aplicar la preparació de Windows (3.3).

**Criteri:** el projecte compila, la interfície mostra «Motor connectat», i el maquinari està documentat.

### Fase 1 — Prototip del motor (sense interfície)
- Enumerar dispositius i, en arrencar, comparar-los amb les assignacions desades (5.6).
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
- Versió portable en un `.zip` (carpeta `dades\` i .NET inclòs; D-044), en lloc d'un instal·lador (D-047).
- Pendent de la fase 1 (D-026): prova de 3 hores amb `OnaBages.ToneCheck` i desconnexió d'un dispositiu USB real.

**Criteri:** fer un programa real complet amb Ona Bages Play, primer en paral·lel amb Winamp i després substituint-lo.

### Fase 3 — Editor
- Forma d'ona, selecció, reproducció, retallar inici i final, eliminar fragment, desfer, desar sense recodificar.

**Criteri:** editar i desar un enregistrament d'1 hora en pocs minuts, mentre un altre enregistrador funciona, sense pèrdues.
- Fet amb la prova automàtica (D-053). Pendent (D-056): la mateixa prova amb el maquinari real i a l'ordinador modest, i comparar un tall amb el mp3DirectCut.

### Fase 4 — Processament, perfils i distribució flexible
**Tancada (D-076)**: tot fet i provat pel desenvolupador, excepte el que s'indica com a ajornat.
- Normalització de sonoritat, limitador, compressor i presets. (L'EQ queda ajornat: D-073.)
- Perfils amb exportació i importació.
- Panells configurables: **ajornat** (D-069; els elements de la pantalla ja s'ajusten prou bé).
- Fosos a l'editor.
- **Explorador d'àudios:** panell lateral, minimitzat per defecte, amb diverses carpetes per perfil (p. ex. «Sintonies», «Talls», «Entrevistes»); les pistes s'arrosseguen als reproductors. El contingut de les carpetes el llegeix el motor. Fet al motor (pas 2a, D-063): carpetes de cada perfil per a les pestanyes Àudios i Llistes, i `explorer.list`, que només llegeix dins d'aquestes carpetes. Fet a la interfície (pas 2c, D-064): pestanya Àudios del panell lateral (navegar, filtrar, arrossegar als reproductors, doble clic = preescolta, botó dret).
- **Panell de llistes desades:** les llistes `.obpl` d'un programa (p. ex. les seccions de cada dia de «La Central») a punt per arrossegar-les senceres a un reproductor. Fet (pas 2d, D-065): pestanya Llistes del panell lateral (arrossegar, veure el contingut, afegir o substituir la llista d'un reproductor).

### Fase 5 — Ampliacions segons l'ús real
Fet: **encadenament amb fos entre pistes** (D-077, D-078): per reproductor, de 0 (per defecte) a 10 s, també molt curt (10–100 ms); en l'avanç automàtic i també quan es fa sonar una pista a mà mentre en sona una altra.

Fet: **retall automàtic de silencis inicials i finals** (D-079, D-080, D-081): per reproductor, desactivat per defecte; llindar i marges configurables al perfil (per defecte −60 dBFS, 10 ms abans del so i 50 ms després). El fitxer no es toca i no es perd cap mostra de so.

Fet: **limitador opcional als enregistradors** (D-083, D-084): botó LIM a cada enregistrador, desactivat per defecte; sostre comú al perfil (per defecte −1 dB, es pot triar −0,1 dB); es pot activar mentre s'enregistra.

Fet: **historial d'enregistraments** (D-086, D-087): còpia de cada enregistrament original en una carpeta per perfil i per dia (D-088), escrita alhora que l'enregistrament; activat per defecte, 30 dies (configurable; 0 = no esborrar mai).

Fet: **documentació integrada** (D-092–D-094): manual d'usuari i documentació tècnica en dos fitxers HTML autoportables (`docs/`), que s'obren al navegador amb el botó «?» de l'aplicació.

Fet: **alarma de silenci als enregistradors** (D-121, D-122): mentre s'enregistra, si el que entra al fitxer és per sota de −50 dBFS durant 30 s (configurable al perfil), el panell parpelleja en vermell i surt un avís. No atura mai l'enregistrament.

Fet: **botonera d'instantanis** (D-127–D-130): pestanya del panell lateral amb 4 pàgines de 4 × 4 botons i sortida pròpia (pot ser la d'un reproductor); sona a l'instant (l'inici de cada tall és a la memòria) i fins a 8 talls alhora; s'omple arrossegant àudios; color, nom i volum per botó; teclat numèric. Al perfil.

Candidates: equalitzador per reproductor (ajornat de la fase 4, D-073), punts cue d'inici i final, biblioteca musical amb cerca, inicis programats, control MIDI o per *fader start*, usuaris.

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
- Les proves de rendiment es fan sempre també a l'**ordinador de proves més modest** (ordinador domèstic amb recursos limitats). Les xifres objectiu s'han de complir en aquest ordinador.
- Per no carregar els ordinadors modestos: els fitxers d'àudio no es carreguen sencers a la memòria (es llegeixen a trossos), les formes d'ona es calculen en segon pla i es desen a la memòria cau, i la freqüència d'actualització dels mesuradors es pot reduir a la configuració.

**Maquinari**
- Arrencar després de treure una targeta, d'afegir-ne una, de canviar un USB de port i amb dues targetes USB iguals: es mostra el resum correcte i no es reassigna res automàticament (5.6).
- Arrencar amb només l'àudio integrat de la placa base.

# 16. Riscos

| Risc | Impacte | Mitigació |
|---|---|---|
| Controladors de consum inestables o amb efectes propis (Creative, Audigy antiga) | Talls, so alterat | Preparació de Windows (3.3); proves a la fase 1 amb cada targeta |
| Pauses del gestor de memòria de .NET al fil d'àudio | Clics | Camí d'àudio sense creació d'objectes; memòria intermèdia adequada; proves llargues |
| Fallada del motor durant un enregistrament | Pèrdua dels últims segons | Bolcat cada 2 s; marcador `.rec`; recuperació automàtica |
| Precisió dels talls MP3 (~25 ms) | Talls poc precisos | Suficient per a la parla; igual que mp3DirectCut |
| Abast massa ampli per a una sola persona | Projecte que no s'acaba | MVP estricte; fases amb criteri de validació; ampliacions només després de l'ús real |
| Dispositius USB que canvien d'identificador | Reproductor sense sortida en arrencar | Identificació per id i per nom; avís clar |
| Targetes integrades (Realtek) que fan desaparèixer el dispositiu en desendollar un cable | Reproductor o enregistrador sense dispositiu durant el directe | Desactivar la detecció de connectors (`docs/installacio-windows.md`); polítiques de 5.3; l'inventari ho detecta |
| Maquinari que canvia entre sessions (targetes afegides o tretes) | Elements sense dispositiu; so per una sortida inesperada | Detecció en arrencar, resum de canvis, cap reassignació automàtica (5.6) |
| Ordinadors domèstics amb pocs recursos | Talls, interfície lenta | Proves a l'ordinador més modest; lectura a trossos; mesuradors amb freqüència configurable |
| Encendre o apagar un monitor HDMI/DisplayPort (Windows torna a crear el seu dispositiu de so) | Uns ms d'àudio perduts a totes les targetes | Desactivar el so dels monitors a l'Administrador de dispositius; no tocar el monitor durant els programes (`docs/installacio-windows.md`); el motor registra cada pèrdua amb el moment exacte |
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
- Editor de dreceres de teclat (a la fase 2 hi ha un mapa fix, D-041).
- Dispositiu de preescolta de cada estudi.
- Convenció de noms i carpetes de destinació dels enregistraments.
- **Còpia automàtica en aturar** (petició del 4 de novembre de 2026; sense fase assignada): poder indicar, per a un enregistrament, una ruta i un nom de fitxer; en prémer Aturar, se'n desa una còpia allà. L'original es queda sempre a la carpeta de treball.
- **Enregistrament automàtic en començar a reproduir** (petició del 4 de novembre de 2026; sense fase assignada): opcionalment, quan s'arrenca l'aplicació i es posa a sonar una pista, un enregistrador comença a enregistrar sol. Queda per definir:
  - quin reproductor (o reproductors) el dispara i quin enregistrador enregistra;
  - si només ho fa la primera pista després d'arrencar o cada vegada que l'enregistrador està inactiu;
  - el nom i la carpeta del fitxer (els de l'enregistrador o uns de propis);
  - com es veu clarament a la interfície que està activat, i com s'atura (l'aturada continuaria sent manual i amb doble pulsació);
  - què passa si l'enregistrador ja enregistra o té un pendent (no s'ha de perdre ni sobreescriure res).
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
