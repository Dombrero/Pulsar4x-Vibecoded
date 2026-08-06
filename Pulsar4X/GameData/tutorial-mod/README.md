# Tutorial Mod

Guided new-player start aligned with **Quickstart** (Sol / Earth, `colony-earth` equivalent data).

## Play

1. **Hauptmenü → „Tutorial (geführt)“** — lädt Basemod + Tutorial-Mod, startet wie Quickstart, öffnet das Tutorial-Fenster.
2. Oder **New Game…** → Tutorial-Mod aktivieren → Startkolonie **Tutorial Start - Guided Economy** → Sol / Earth.

## Inhalt

- `tutorial-starts.json`: Kolonie-Blueprint (identisch zu Default Earth Start, inkl. Stromnetz, Flotten: Freight / Military / Science).
- Ingame-Anleitung: Wirtschaft (Mining → Refinery → Construction, **Energy-Tab**), dann Survey, Standing Orders, Sprungpunkte/Galaxy, Military Fleet.

## Dev

Regression: `TutorialModStartTests` (Mod laden + `ColonyFactory.CreateFromBlueprint`).
