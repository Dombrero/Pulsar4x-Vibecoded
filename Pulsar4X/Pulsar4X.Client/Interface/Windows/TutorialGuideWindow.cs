using System;
using ImGuiNET;
using Pulsar4X.Client.Interface.Widgets;

namespace Pulsar4X.Client
{
    /// <summary>
    /// Ingame-Tutorial: stabile Wirtschaft, dann Expansion.
    /// Highlights: immer nur das aktuelle Klickziel; naechstes nach Klick darauf.
    /// </summary>
    public class TutorialGuideWindow : UniquePulsarGuiWindow<TutorialGuideWindow>
    {
        private int _stepIndex;
        private int _boundPageForPath = -1;
        private bool _showHighlight = true;

        private static readonly (string Heading, int First, int Last)[] TocSections =
        [
            ("Step-by-Step: Stabile Wirtschaft", 1, 12),
            ("Step-by-Step: Expansion", 13, 16),
            ("Nachschlagen", 17, 21),
        ];

        /// <summary>Kurzer Text fuer das aktuelle Highlight (gleiche Reihenfolge wie Clicks).</summary>
        private readonly (string TocLabel, string Title, string Body, string[] HintLabels, TutorialHighlightRegion[] Clicks)[] _pages =
        [
            ("Inhaltsverzeichnis", "Inhaltsverzeichnis", "", Array.Empty<string>(), Array.Empty<TutorialHighlightRegion>()),

            (
                "S1 Pause & UI",
                "S1 – Pause & UI",
                "Ziel: stabile Wirtschaft aufbauen, Kernsysteme einmal anfassen.\n\n"
                + "Folge den orangen Markierungen der Reihe nach (nur eine gleichzeitig).\n"
                + "Klicke das markierte Element – dann erscheint das naechste.",
                [
                    "Pause/Play oben links",
                    "Zeit-Intervall (Hours/Days)",
                    "Funds rechts (Geld)",
                ],
                [
                    TutorialHighlightRegion.TimeControlPlayPause,
                    TutorialHighlightRegion.TimeControlInterval,
                    TutorialHighlightRegion.RightSelectorFunds,
                ]
            ),
            (
                "S2 Kolonie & Tabs",
                "S2 – Kolonie und Tabs",
                "Tabs:\n"
                + "• Summary – Stockpile\n"
                + "• Mining – Foerderung\n"
                + "• Production – Refinery/Factory/Shipyard\n"
                + "• Construction – Anlagen bauen\n\n"
                + "Stockpile ansehen: Was hast du, was fehlt?",
                [
                    "Rechts: Colonies",
                    "Toolbar: Colony Management",
                    "Links: Kolonie waehlen",
                    "Tab Summary",
                    "Stockpile ansehen",
                ],
                [
                    TutorialHighlightRegion.RightSelectorColonies,
                    TutorialHighlightRegion.LeftToolbarColony,
                    TutorialHighlightRegion.ColonyList,
                    TutorialHighlightRegion.ColonyTabSummary,
                    TutorialHighlightRegion.ColonyStockpile,
                ]
            ),
            (
                "S3 Die Wirtschaftskette",
                "S3 – Wirtschaftskette (Konzept)",
                "Stabile Wirtschaft:\n"
                + "Mining → Stockpile → Refinery → Factory → Construction → Shipyard.\n\n"
                + "Stockt ein Glied (volles Lager, fehlendes Material, Idle-Line),\n"
                + "bricht die Stabilitaet.\n\n"
                + "Engpass-Regel: beheben, was ZUERST leer/idle wird.\n"
                + "Klicke der Reihe nach die Tabs, um sie zu finden.",
                [
                    "Tab Mining",
                    "Tab Production",
                    "Tab Construction",
                    "Stockpile (Summary)",
                ],
                [
                    TutorialHighlightRegion.ColonyTabMining,
                    TutorialHighlightRegion.ColonyTabProduction,
                    TutorialHighlightRegion.ColonyTabConstruction,
                    TutorialHighlightRegion.ColonyStockpile,
                ]
            ),
            (
                "S4 Mining verstehen",
                "S4 – Mining verstehen",
                "• Number of Mines = Foerderkapazitaet\n"
                + "• Annual Production = Theorie / Jahr\n"
                + "• Stockpile = wirklich gelagert (nur bei Platz)\n"
                + "• Accessibility 0–1 beeinflusst Rate\n\n"
                + "Foerderung ~1×/Tag. Volles Lager → kaum Zuwachs.\n"
                + "Loesung: Warehouse (S7), mehr Minen (S8).",
                [
                    "Tab Mining oeffnen",
                    "Tab Summary (fuer Stockpile)",
                    "Stockpile vergleichen",
                ],
                [
                    TutorialHighlightRegion.ColonyTabMining,
                    TutorialHighlightRegion.ColonyTabSummary,
                    TutorialHighlightRegion.ColonyStockpile,
                ]
            ),
            (
                "S5 Production bedienen",
                "S5 – Production bedienen",
                "Jeder Job so:\n"
                + "Tab Production → Line waehlen → rechts Job anlegen\n"
                + "(Design, Menge, Repeat).\n\n"
                + "Idle = ungenutzt. Missing Resources = Inputs fehlen.",
                [
                    "Tab Production",
                    "Production Line links",
                    "Job-Panel rechts",
                ],
                [
                    TutorialHighlightRegion.ColonyTabProduction,
                    TutorialHighlightRegion.ProductionLines,
                    TutorialHighlightRegion.ProductionNewJob,
                ]
            ),
            (
                "S6 Raffinerie anwerfen",
                "S6 – Raffinerie: Materialien",
                "Refinery-Jobs (Repeat):\n"
                + "• Plastic ← Hydrocarbons (fuer Warehouse/Bau)\n"
                + "• Stainless Steel ← Iron + Chromium + Hydrocarbons\n"
                + "• Optional Fuel ← Hydrocarbons\n\n"
                + "Zeit laufen lassen, dann Stockpile pruefen.",
                [
                    "Tab Production",
                    "Refinery-Line",
                    "Plastic/Steel-Job anlegen",
                    "Play (Zeit)",
                    "Tab Summary",
                    "Stockpile: Plastic/Steel?",
                ],
                [
                    TutorialHighlightRegion.ColonyTabProduction,
                    TutorialHighlightRegion.ProductionLines,
                    TutorialHighlightRegion.ProductionNewJob,
                    TutorialHighlightRegion.TimeControlPlayPause,
                    TutorialHighlightRegion.ColonyTabSummary,
                    TutorialHighlightRegion.ColonyStockpile,
                ]
            ),
            (
                "S7 Construction & Warehouse",
                "S7 – Construction: Warehouse",
                "Construction baut aus dem Stockpile.\n"
                + "Erstes Ziel: Warehouse (Iron, Aluminium, Plastic).\n"
                + "Fehlt Plastic → zurueck zu S6.\n\n"
                + "Mehr Lager → Mining kann wieder steigen.",
                [
                    "Tab Construction",
                    "Design Warehouse waehlen",
                    "Queue links pruefen",
                ],
                [
                    TutorialHighlightRegion.ColonyTabConstruction,
                    TutorialHighlightRegion.ConstructionDesigns,
                    TutorialHighlightRegion.ConstructionQueue,
                ]
            ),
            (
                "S8 Foerderung ausbauen",
                "S8 – Mehr Minen",
                "Nur wenn Lagerplatz da ist:\n"
                + "Construction → Mine → warten → Mining: Number of Mines steigt.\n\n"
                + "Nicht bei vollem Lager blind Minen bauen.",
                [
                    "Tab Construction",
                    "Design Mine waehlen",
                    "Tab Mining (Ergebnis)",
                ],
                [
                    TutorialHighlightRegion.ColonyTabConstruction,
                    TutorialHighlightRegion.ConstructionDesigns,
                    TutorialHighlightRegion.ColonyTabMining,
                ]
            ),
            (
                "S9 Factory & Dauerbetrieb",
                "S9 – Factory & Dauerbetrieb",
                "Factory: Komponenten (spaeter fuer Schiffe).\n"
                + "Am Anfang: Refinery + Construction wichtiger.\n\n"
                + "Stabil = dauerhafte Refinery-Jobs (Plastic/Steel),\n"
                + "sinnvolle Construction-Queue, Mining waechst oder wird verbraucht.",
                [
                    "Tab Production",
                    "Factory- oder Refinery-Line",
                    "Job/Repeat pruefen",
                ],
                [
                    TutorialHighlightRegion.ColonyTabProduction,
                    TutorialHighlightRegion.ProductionLines,
                    TutorialHighlightRegion.ProductionNewJob,
                ]
            ),
            (
                "S10 Forschung (Wirtschaft)",
                "S10 – Forschung",
                "Schaltet bessere Techs frei. Kostet Funds/Tag.\n"
                + "Wirtschaft (S4–S9) parallel weiterlaufen lassen.",
                [
                    "Toolbar Research",
                    "Lab waehlen",
                    "Tech doppelklicken",
                    "Tech Queue",
                ],
                [
                    TutorialHighlightRegion.LeftToolbarResearch,
                    TutorialHighlightRegion.ResearchLabList,
                    TutorialHighlightRegion.ResearchAvailableTechs,
                    TutorialHighlightRegion.ResearchTechQueue,
                ]
            ),
            (
                "S11 Stabilitaets-Check",
                "S11 – Stabilitaets-Check",
                "1 Days → Play → Pause. Pruefen:\n"
                + "Stockpile, Mining, Production nicht Idle/Missing,\n"
                + "Construction kommt voran, Funds nicht 0.\n\n"
                + "Engpass → S6–S9.",
                [
                    "Intervall auf Days",
                    "Play",
                    "Tab Mining",
                    "Tab Production",
                    "Stockpile (Summary)",
                ],
                [
                    TutorialHighlightRegion.TimeControlInterval,
                    TutorialHighlightRegion.TimeControlPlayPause,
                    TutorialHighlightRegion.ColonyTabMining,
                    TutorialHighlightRegion.ColonyTabProduction,
                    TutorialHighlightRegion.ColonyStockpile,
                ]
            ),
            (
                "S12 Checkliste Wirtschaft",
                "S12 – Checkliste stabile Wirtschaft",
                "☐ Stockpile gelesen\n"
                + "☐ Production-Jobs anlegen koennen\n"
                + "☐ Plastic + Steel (Refinery)\n"
                + "☐ Warehouse gebaut\n"
                + "☐ Mining sinnvoll\n"
                + "☐ Research ohne Funds-Ruin\n"
                + "☐ S11 bestanden\n\n"
                + "Dann Expansion ab S13.",
                [
                    "Stockpile final pruefen",
                ],
                [
                    TutorialHighlightRegion.ColonyStockpile,
                ]
            ),

            (
                "S13 Survey",
                "S13 – Survey",
                "Geo-Survey fuer Mineralien auf anderen Koerpern.\n"
                + "Danach System Viewer: GeoSurvey-%.",
                [
                    "Toolbar Fleet Management",
                    "Tutorial Survey Fleet",
                    "Tab Issue Orders",
                    "Geo Survey ...",
                    "Zielkoerper klicken",
                    "System Viewer (Baum)",
                ],
                [
                    TutorialHighlightRegion.LeftToolbarFleet,
                    TutorialHighlightRegion.FleetList,
                    TutorialHighlightRegion.FleetTabIssueOrders,
                    TutorialHighlightRegion.FleetOrderGeoSurvey,
                    TutorialHighlightRegion.FleetOrderTargets,
                    TutorialHighlightRegion.LeftToolbarSystemTree,
                ]
            ),
            (
                "S14 Standing Orders",
                "S14 – Standing Orders",
                "Create New Order → Fuel <= 30 → Refuel,\n"
                + "optional Move to Nearest Geo Survey → Save.\n"
                + "Nur wenn keine manuellen Orders aktiv.\n"
                + "Stuck Orders: unter Fleet Orders mit X / Clear All entfernen.",
                [
                    "Tab Standing Orders",
                    "Save",
                ],
                [
                    TutorialHighlightRegion.FleetTabStandingOrders,
                    TutorialHighlightRegion.FleetStandingSave,
                ]
            ),
            (
                "S15 Transfer",
                "S15 – Transfer",
                "Stockpile → Initiate Transfer, oder Freighter Move to.\n"
                + "Neue Kolonien selbst beladen.",
                [
                    "Colony Management",
                    "Tab Summary",
                    "Initiate Transfer",
                    "Logistics Fleet (rechts)",
                ],
                [
                    TutorialHighlightRegion.LeftToolbarColony,
                    TutorialHighlightRegion.ColonyTabSummary,
                    TutorialHighlightRegion.ColonyTransferButton,
                    TutorialHighlightRegion.RightSelectorFleets,
                ]
            ),
            (
                "S16 Design & Kampf",
                "S16 – Design & Kampf",
                "Erst Wirtschaft (S12), dann Design → Shipyard → Launch.\n"
                + "Kampf: Schiff → Fire Control → Ziel → Open Fire.",
                [
                    "Production / Shipyard",
                    "Fleets (Schiff finden)",
                ],
                [
                    TutorialHighlightRegion.ColonyTabProduction,
                    TutorialHighlightRegion.RightSelectorFleets,
                ]
            ),

            (
                "N: Wirtschaftskette",
                "Nachschlagen: Wirtschaftskette",
                "Mining → Stockpile → Refinery → Factory → Construction → Shipyard.\n"
                + "Engpass: Lager → Plastic/Steel → Minen → Refine.",
                [
                    "Tab Mining",
                    "Tab Production",
                    "Tab Construction",
                ],
                [
                    TutorialHighlightRegion.ColonyTabMining,
                    TutorialHighlightRegion.ColonyTabProduction,
                    TutorialHighlightRegion.ColonyTabConstruction,
                ]
            ),
            (
                "N: Materialien",
                "Nachschlagen: Materialien",
                "Plastic ← Hydrocarbons\n"
                + "Steel ← Iron+Chromium+Hydrocarbons\n"
                + "Warehouse ← Iron+Aluminium+Plastic",
                [
                    "Production Job",
                    "Construction Designs",
                ],
                [
                    TutorialHighlightRegion.ProductionNewJob,
                    TutorialHighlightRegion.ConstructionDesigns,
                ]
            ),
            (
                "N: Production",
                "Nachschlagen: Production",
                "Line → Job → Repeat. Missing Resources = Stockpile nachfuellen.",
                [
                    "Lines",
                    "Job-Panel",
                ],
                [
                    TutorialHighlightRegion.ProductionLines,
                    TutorialHighlightRegion.ProductionNewJob,
                ]
            ),
            (
                "N: Flotten",
                "Nachschlagen: Flotten",
                "Issue Orders / Standing Orders + Save.",
                [
                    "Issue Orders",
                    "Standing Orders",
                ],
                [
                    TutorialHighlightRegion.FleetTabIssueOrders,
                    TutorialHighlightRegion.FleetTabStandingOrders,
                ]
            ),
            (
                "N: Galaxie",
                "Nachschlagen: Galaxie",
                "System Viewer, Grav Survey, Jump, Kolonisieren mit Transfer.",
                [
                    "System Viewer",
                    "Fleet Management",
                ],
                [
                    TutorialHighlightRegion.LeftToolbarSystemTree,
                    TutorialHighlightRegion.LeftToolbarFleet,
                ]
            ),
        ];

        private TutorialGuideWindow()
        {
        }

        internal static TutorialGuideWindow GetInstance()
        {
            if (_uiState.TryGetUniqueWindow<TutorialGuideWindow>(out var window))
                return window;

            return _uiState.AddUniqueWindow(new TutorialGuideWindow());
        }

        internal static bool IsTutorialColonyId(string? colonyId) =>
            !string.IsNullOrEmpty(colonyId)
            && colonyId.Contains("tutorial-start-guided-economy", StringComparison.OrdinalIgnoreCase);

        internal void ResetToFirstStep()
        {
            _stepIndex = 0;
            _boundPageForPath = -1;
            TutorialHighlight.ClearPath();
        }

        internal override void Display()
        {
            if (!IsActive)
            {
                TutorialHighlight.Enabled = false;
                TutorialHighlight.ClearPath();
                _boundPageForPath = -1;
                return;
            }

            _stepIndex = Math.Clamp(_stepIndex, 0, _pages.Length - 1);
            SyncHighlightPath();

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 540), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(40, 80), ImGuiCond.Appearing);

            if (!Window.Begin("Tutorial", ref IsActive))
            {
                Window.End();
                return;
            }

            if (ImGui.Checkbox("UI-Hervorhebung (nur aktueller Klick)", ref _showHighlight))
                SyncHighlightPath();

            ImGui.Separator();

            if (_stepIndex == 0)
                DisplayTableOfContents();
            else
                DisplayContentPage(_pages[_stepIndex]);

            ImGui.Spacing();
            ImGui.Separator();
            DisplayNavButtons();

            _stepIndex = Math.Clamp(_stepIndex, 0, _pages.Length - 1);
            SyncHighlightPath();

            Window.End();
        }

        private void SyncHighlightPath()
        {
            var page = _pages[_stepIndex];
            bool on = IsActive && _showHighlight && _stepIndex > 0 && page.Clicks.Length > 0;
            TutorialHighlight.Enabled = on;

            if (!on)
            {
                TutorialHighlight.ClearPath();
                _boundPageForPath = -1;
                return;
            }

            if (_boundPageForPath != _stepIndex)
            {
                TutorialHighlight.SetPath(page.Clicks);
                _boundPageForPath = _stepIndex;
            }
        }

        private void DisplayTableOfContents()
        {
            ImGui.TextWrapped(
                "Stabile Wirtschaft von Anfang an. Orange Markierung = nur der naechste Klick; "
                + "nach dem Klick erscheint das folgende Ziel.");
            ImGui.Spacing();

            if (ImGui.Button("Wirtschafts-Intro starten", new System.Numerics.Vector2(-1, 0)))
            {
                _stepIndex = 1;
                _boundPageForPath = -1;
            }

            ImGui.Spacing();
            foreach (var (heading, first, last) in TocSections)
                DrawTocSection(heading, first, last);
        }

        private void DrawTocSection(string heading, int firstIndex, int lastIndex)
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(0.55f, 0.75f, 1f, 1f), heading);
            for (int i = firstIndex; i <= lastIndex && i < _pages.Length; i++)
            {
                if (ImGui.Selectable(_pages[i].TocLabel))
                {
                    _stepIndex = i;
                    _boundPageForPath = -1;
                }
            }
        }

        private void DisplayContentPage(
            (string TocLabel, string Title, string Body, string[] HintLabels, TutorialHighlightRegion[] Clicks) page)
        {
            ImGui.Text(page.Title);
            ImGui.SameLine();
            ImGui.TextDisabled($"({_stepIndex}/{_pages.Length - 1})");
            ImGui.Spacing();
            ImGui.TextWrapped(page.Body);

            if (page.Clicks.Length > 0 && _showHighlight)
            {
                ImGui.Spacing();
                ImGui.Separator();
                int idx = TutorialHighlight.PathIndex;
                int total = TutorialHighlight.PathLength;

                if (TutorialHighlight.PathComplete)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.9f, 0.5f, 1f),
                        "Alle Klickziele dieser Seite erledigt – Weiter fuer naechstes Thema.");
                }
                else
                {
                    string hint = idx < page.HintLabels.Length ? page.HintLabels[idx] : "Markiertes Element klicken";
                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.82f, 0.2f, 1f),
                        $"Als Naechstes ({idx + 1}/{total}): {hint}");
                    ImGui.TextDisabled("Klicke die gelbe Markierung. Fehlt sie: Fenster/Tab erst oeffnen.");
                }

                if (ImGui.Button("Hinweis zurueck") && TutorialHighlight.PathIndex > 0)
                    TutorialHighlight.Retreat();
                ImGui.SameLine();
                if (ImGui.Button("Hinweis ueberspringen") && !TutorialHighlight.PathComplete)
                    TutorialHighlight.Advance();
            }
        }

        private void DisplayNavButtons()
        {
            if (_stepIndex > 0)
            {
                if (ImGui.Button("Inhalt"))
                {
                    _stepIndex = 0;
                    _boundPageForPath = -1;
                }
                ImGui.SameLine();
            }

            if (ImGui.Button("Zurueck") && _stepIndex > 0)
            {
                _stepIndex--;
                _boundPageForPath = -1;
            }

            ImGui.SameLine();
            if (_stepIndex == 0)
            {
                if (ImGui.Button("Start"))
                {
                    _stepIndex = 1;
                    _boundPageForPath = -1;
                }
            }
            else if (_stepIndex < _pages.Length - 1)
            {
                if (ImGui.Button("Weiter"))
                {
                    _stepIndex++;
                    _boundPageForPath = -1;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Schliessen"))
                IsActive = false;
        }
    }
}
