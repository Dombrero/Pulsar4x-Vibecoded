# Pulsar4X lokal einrichten und modden

## Kurzfassung

`Pulsar4X` ist auf dem aktuellen `DevBranch` ein nativer `.NET 8`-Desktopclient.

Zum Modden ist das Repo lokal bereits vorbereitet: `DevBranch` ist ausgecheckt.

## Was ich bereits fuer dich gemacht habe

- Repository nach `Pulsar4x` geklont
- Auf `DevBranch` gewechselt
- Die Projektstruktur geprueft
- Die Mod-Struktur in `Pulsar4X/GameData` geprueft
- Ein Starter-Mod-Template unter `Pulsar4X/GameData/my-first-mod` angelegt

## Wichtige Erkenntnisse

### 1. Das Projekt ist ein lokaler nativer Client

Der Client besteht aus nativen `.NET`-, `ImGui`- und `SDL`-Teilen. Fuer dich heisst das jetzt einfach: alles lokal bauen, starten und modden.

### 2. Zum Modden brauchst du lokal .NET 8

Auf diesem Rechner ist `dotnet` aktuell noch nicht installiert oder nicht im `PATH`.

Du brauchst:

1. [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
2. Optional: [Visual Studio 2022](https://visualstudio.microsoft.com/) oder [Rider](https://www.jetbrains.com/rider/)
3. Optional: GitHub Desktop oder GitHub CLI

### 3. Die aktuelle Mod-Struktur liegt in `Pulsar4X/GameData`

Relevante Ordner:

- `Pulsar4X/GameData/basemod`
- `Pulsar4X/GameData/testingmod`
- `Pulsar4X/GameData/my-first-mod`

Jede Mod braucht mindestens:

- eine `modInfo.json`
- mindestens eine JSON-Datei, die in `DataFiles` eingetragen ist

## Schritt fuer Schritt: lokale Einrichtung

### Schritt 1: .NET 8 installieren

Installiere das `.NET 8 SDK`.

Danach in PowerShell pruefen:

```powershell
dotnet --version
```

Wenn eine `8.x.x`-Version erscheint, passt es.

### Schritt 2: Repo oeffnen

Projektpfad:

```text
C:\Users\PCUser\Desktop\Coding\incrematal game(vercel)\Pulsar4x
```

### Schritt 3: Branch pruefen

Im Repo sollte aktuell `DevBranch` aktiv sein.

Zur Kontrolle:

```powershell
git status
```

### Schritt 4: Loesung oeffnen

Oeffne:

```text
Pulsar4x\Pulsar4X\Pulsar4X.sln
```

### Schritt 5: Restore/Build ausfuehren

Im Ordner `Pulsar4x\Pulsar4X`:

```powershell
dotnet restore
dotnet build
```

Wenn du direkt starten willst:

```powershell
dotnet run --project .\Pulsar4X.Client.Host\Pulsar4X.Client.Host.csproj
```

Oder direkt vom Repo-Root aus:

```powershell
.\start-pulsar4x.ps1
```

## Schritt fuer Schritt: deine erste Mod

Ich habe bereits einen Startordner angelegt:

```text
Pulsar4x\Pulsar4X\GameData\my-first-mod
```

Dateien:

- `modInfo.json`
- `theme-tweak.json`

### So aktivierst du die Mod

In `modInfo.json` steht aktuell:

```json
"DefaultEnabled": false
```

Du kannst das auf `true` setzen, oder die Mod spaeter im Client aktivieren, falls der Mod-Screen im aktuellen Build verfuegbar ist.

### Was das Beispiel tut

Die Beispielmod erweitert den Theme-Datensatz `default-theme` um zusaetzliche Vornamen.

Das ist bewusst ein kleiner, risikoarmer Einstieg.

### So baust du darauf auf

1. Kopiere weitere Ideen aus `testingmod`
2. Schaue in `basemod/modInfo.json`, welche Basisdateien geladen werden
3. Passe bestehende Eintraege ueber `UniqueID` an
4. Nutze `CollectionOperation` fuer Listen oder Dictionaries:
   - `Add`
   - `Remove`
   - `Overwrite`

## Empfohlener Gesamt-Workflow

1. Spiel lokal kompilieren und starten
2. Mod in `GameData/my-first-mod` entwickeln
3. Aenderungen in eigenem Fork committen
4. Builds lokal oder via CI erzeugen
5. Spielbare Artefakte spaeter lokal testen und dann verteilen

## Optionaler naechster sinnvoller Schritt

Wenn du willst, kann ich als naechstes deine erste echte Mod in `my-first-mod` bauen oder dir den lokalen Build so vorbereiten, dass du nur noch `dotnet run` ausfuehren musst.
