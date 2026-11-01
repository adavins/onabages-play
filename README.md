# Ona Bages Play

Aplicació d'escriptori per a Windows per a la producció de programes de ràdio en directe: reproductors amb sortides pròpies, preescolta, enregistradors MP3 simultanis i editor ràpid.

- Especificació: [`docs/especificacio.md`](docs/especificacio.md)
- Regles per a Claude Code: [`CLAUDE.md`](CLAUDE.md)
- Protocol interfície ↔ motor: [`docs/protocol.md`](docs/protocol.md)

**Estat:** fase 0. El motor arrenca i accepta connexions, i la interfície s'hi connecta. Encara no hi ha àudio.

---

## 1. Programes necessaris (una sola vegada)

1. **Git:** https://git-scm.com
2. **Node.js LTS:** https://nodejs.org
3. **.NET 10 SDK:** https://dotnet.microsoft.com/download
4. **Visual Studio Code** amb les extensions:
   - C# (Microsoft). La C# Dev Kit és opcional.
   - Claude Code.
   - EditorConfig.

En obrir la carpeta a VS Code, us proposarà instal·lar les extensions recomanades.

Per comprovar-ho, en un terminal:

```powershell
git --version
node --version
dotnet --version   # ha de començar per 10.
```

## 2. Primer cop

```powershell
cd C:\ruta\a\ona-bages-play
git init
git add .
git commit -m "Fase 0: esquelet inicial"
```

## 3. Motor

```powershell
dotnet build engine/OnaBages.slnx
dotnet test engine/OnaBages.slnx
```

Per arrencar-lo sol:

```powershell
dotnet run --project engine/OnaBages.Engine
```

La consola mostra el port i el testimoni. Per provar-lo sense interfície, obre `tools/consola-proves.html` al navegador, enganxa-hi el testimoni i prem **Connectar**.

## 4. Interfície

```powershell
cd app
npm install
npm start
```

La finestra ha de mostrar el rellotge i **«Motor connectat»** en verd. Si el motor no estava en marxa, la interfície l'arrenca sola (cal haver fet abans `dotnet build`).

En tancar la finestra, el motor també s'atura. A partir de la fase 2, si hi ha reproduccions o enregistraments actius, demanarà confirmació i el motor podrà continuar sol.

## 5. Primera petició a Claude Code

Aquest esquelet s'ha escrit sense poder compilar-lo. Abans de començar la fase 1, demana a Claude Code:

> Llegeix CLAUDE.md. Compila el motor amb `dotnet build engine/OnaBages.slnx`, executa les proves amb `dotnet test engine/OnaBages.slnx` i corregeix qualsevol error. Després comprova que `npm install` i `npm start` funcionen a la carpeta app.

## 6. Criteri per acabar la fase 0

- [ ] El motor compila i passa les proves.
- [ ] `npm start` mostra «Motor connectat».
- [ ] La consola de proves respon a `engine.hello`.
- [ ] Si es tanca el motor a mà, la interfície mostra «Motor no disponible» i el botó **Reiniciar el motor** el torna a arrencar.
- [ ] Inventari del maquinari omplert a `docs/installacio-windows.md`.
- [ ] Preparació de Windows aplicada a l'ordinador de proves.
