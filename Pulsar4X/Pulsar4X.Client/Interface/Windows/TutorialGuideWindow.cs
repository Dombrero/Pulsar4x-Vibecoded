using System;
using ImGuiNET;
using Pulsar4X.Client.Interface.Widgets;

namespace Pulsar4X.Client;

public enum TutorialLanguage
{
    German,
    English,
}

public class TutorialGuideWindow : UniquePulsarGuiWindow<TutorialGuideWindow>
{
    private readonly record struct TocSection(string Heading, int First, int Last);
    private readonly record struct TutorialPage(
        string TocLabel,
        string Title,
        string Body,
        string[] HintLabels,
        TutorialHighlightRegion[] Clicks);
    private readonly record struct TutorialLocale(
        string HighlightCheckbox,
        string TocIntro,
        string StartIntroButton,
        string NextHintFallback,
        string NextHintHelp,
        string PageComplete,
        string ButtonBackHint,
        string ButtonSkipHint,
        string ButtonContents,
        string ButtonBack,
        string ButtonStart,
        string ButtonNext,
        string ButtonClose,
        TutorialPage[] Pages,
        TocSection[] Sections);

    private int _stepIndex;
    private int _boundPageForPath = -1;
    private bool _showHighlight = true;
    private TutorialLanguage _language = TutorialLanguage.German;

    private static readonly TutorialHighlightRegion[] NoClicks = Array.Empty<TutorialHighlightRegion>();
    private static readonly string[] NoHints = Array.Empty<string>();

    private static readonly TutorialLocale German = new(
        HighlightCheckbox: "UI-Hervorhebung (nur aktueller Klick)",
        TocIntro: "Stabile Wirtschaft von Anfang an. Orange Markierung = nur der naechste Klick; nach dem Klick erscheint das folgende Ziel.",
        StartIntroButton: "Wirtschafts-Intro starten",
        NextHintFallback: "Markiertes Element klicken",
        NextHintHelp: "Klicke die gelbe Markierung. Fehlt sie: Fenster oder Tab erst oeffnen.",
        PageComplete: "Alle Klickziele dieser Seite erledigt - weiter fuer das naechste Thema.",
        ButtonBackHint: "Hinweis zurueck",
        ButtonSkipHint: "Hinweis ueberspringen",
        ButtonContents: "Inhalt",
        ButtonBack: "Zurueck",
        ButtonStart: "Start",
        ButtonNext: "Weiter",
        ButtonClose: "Schliessen",
        Pages:
        [
            new("Inhaltsverzeichnis", "Inhaltsverzeichnis", "", NoHints, NoClicks),
            new("S1 Pause & UI", "S1 - Pause & UI",
                "Ziel: eine stabile Wirtschaft aufbauen und die Kernsysteme einmal anfassen.\n\n"
                + "Folge den orangenen Markierungen der Reihe nach (nur eine gleichzeitig).\n"
                + "Klicke das markierte Element - dann erscheint das naechste.",
                ["Pause/Play oben links", "Zeit-Intervall (Hours/Days)", "Funds rechts (Geld)"],
                [TutorialHighlightRegion.TimeControlPlayPause, TutorialHighlightRegion.TimeControlInterval, TutorialHighlightRegion.RightSelectorFunds]),
            new("S2 Kolonie & Tabs", "S2 - Kolonie und Tabs",
                "Tabs:\n- Summary: Stockpile\n- Mining: Foerderung\n- Production: Refinery/Factory/Shipyard\n- Construction: Anlagen bauen\n\n"
                + "Stockpile ansehen: Was hast du, was fehlt?",
                ["Rechts: Colonies", "Toolbar: Colony Management", "Links: Kolonie waehlen", "Tab Summary", "Stockpile ansehen"],
                [TutorialHighlightRegion.RightSelectorColonies, TutorialHighlightRegion.LeftToolbarColony, TutorialHighlightRegion.ColonyList, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyStockpile]),
            new("S3 Energie", "S3 - Stromnetz der Kolonie",
                "Kolonien brauchen Strom (kW) fuer Minen, Raffinerie, Fabrik und Werft.\n\n"
                + "Quickstart liefert Kohlekraftwerk plus Kolonie-Batterie (Start voll).\n"
                + "Im Energy-Tab siehst du Erzeugung, Bedarf, Speicher und Verbraucher.\n\n"
                + "Regel: Demand dauerhaft ueber Output bedeutet instabile Wirtschaft.",
                ["Tab Energy", "Tab Production", "Tab Mining"],
                [TutorialHighlightRegion.ColonyTabEnergy, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyTabMining]),
            new("S4 Wirtschaftskette", "S4 - Wirtschaftskette",
                "Stabile Wirtschaft:\nMining -> Stockpile -> Refinery -> Factory -> Construction -> Shipyard.\n\n"
                + "Wenn ein Glied stockt (volles Lager, fehlendes Material, Idle-Line), leidet das Ganze.",
                ["Tab Mining", "Tab Production", "Tab Construction", "Stockpile (Summary)"],
                [TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyTabConstruction, TutorialHighlightRegion.ColonyStockpile]),
            new("S5 Mining verstehen", "S5 - Mining verstehen",
                "- Number of Mines = Foerderkapazitaet\n- Annual Production = Theorie pro Jahr\n- Stockpile = wirklich gelagert\n- Accessibility 0-1 beeinflusst die Rate\n\n"
                + "Volles Lager -> kaum Zuwachs. Loesung: Warehouse, dann mehr Minen.",
                ["Tab Mining oeffnen", "Tab Summary", "Stockpile vergleichen"],
                [TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyStockpile]),
            new("S6 Production bedienen", "S6 - Production bedienen",
                "Jeder Job folgt demselben Ablauf:\nProduction -> Line waehlen -> rechts Job anlegen (Design, Menge, Repeat).\n\n"
                + "Idle = ungenutzt. Missing Resources = Inputs fehlen.",
                ["Tab Production", "Production Line links", "Job-Panel rechts"],
                [TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob]),
            new("S7 Raffinerie anwerfen", "S7 - Raffinerie: Materialien",
                "Refinery-Jobs (Repeat):\n- Plastic <- Hydrocarbons\n- Stainless Steel <- Iron + Chromium + Hydrocarbons\n- Optional Fuel <- Hydrocarbons\n\n"
                + "Zeit laufen lassen, dann Stockpile pruefen.",
                ["Tab Production", "Refinery-Line", "Plastic/Steel-Job anlegen", "Play (Zeit)", "Tab Summary", "Stockpile: Plastic/Steel?"],
                [TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob, TutorialHighlightRegion.TimeControlPlayPause, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyStockpile]),
            new("S8 Construction & Warehouse", "S8 - Construction: Warehouse",
                "Construction baut aus dem Stockpile. Erstes Ziel: Warehouse.\n"
                + "Fehlt Plastic, geh zu S7 zurueck. Mehr Lager -> mehr Mining-Spielraum.",
                ["Tab Construction", "Design Warehouse waehlen", "Queue links pruefen"],
                [TutorialHighlightRegion.ColonyTabConstruction, TutorialHighlightRegion.ConstructionDesigns, TutorialHighlightRegion.ConstructionQueue]),
            new("S9 Foerderung ausbauen", "S9 - Mehr Minen",
                "Nur wenn Lagerplatz da ist: Construction -> Mine -> warten -> Mining pruefen.\n\n"
                + "Nicht bei vollem Lager blind Minen bauen.",
                ["Tab Construction", "Design Mine waehlen", "Tab Mining (Ergebnis)"],
                [TutorialHighlightRegion.ColonyTabConstruction, TutorialHighlightRegion.ConstructionDesigns, TutorialHighlightRegion.ColonyTabMining]),
            new("S10 Factory & Dauerbetrieb", "S10 - Factory & Dauerbetrieb",
                "Factory baut Komponenten, aber frueh sind Refinery und Construction wichtiger.\n\n"
                + "Stabil bedeutet: laufende Refinery-Jobs, sinnvolle Queue, Mining waechst oder wird nuetzlich verbraucht.",
                ["Tab Production", "Factory- oder Refinery-Line", "Job/Repeat pruefen"],
                [TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob]),
            new("S11 Forschung", "S11 - Forschung",
                "Forschung schaltet bessere Techs frei und kostet Funds pro Tag.\n"
                + "Wirtschaft aus S4-S10 parallel weiterlaufen lassen.",
                ["Toolbar Research", "Lab waehlen", "Tech doppelklicken", "Tech Queue"],
                [TutorialHighlightRegion.LeftToolbarResearch, TutorialHighlightRegion.ResearchLabList, TutorialHighlightRegion.ResearchAvailableTechs, TutorialHighlightRegion.ResearchTechQueue]),
            new("S12 Stabilitaets-Check", "S12 - Stabilitaets-Check",
                "1 Day -> Play -> Pause. Pruefen: Stockpile, Mining, Production nicht Idle/Missing, Construction laeuft, Funds nicht 0.\n\n"
                + "Engpass? Zurueck zu S7-S10.",
                ["Intervall auf Days", "Play", "Tab Mining", "Tab Production", "Stockpile (Summary)"],
                [TutorialHighlightRegion.TimeControlInterval, TutorialHighlightRegion.TimeControlPlayPause, TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyStockpile]),
            new("S13 Checkliste Wirtschaft", "S13 - Checkliste stabile Wirtschaft",
                "[ ] Stockpile gelesen\n[ ] Production-Jobs anlegen koennen\n[ ] Plastic + Steel online\n[ ] Warehouse gebaut\n[ ] Mining sinnvoll\n[ ] Forschung laeuft\n[ ] S12 bestanden\n\nDann Expansion ab S14.",
                ["Stockpile final pruefen"],
                [TutorialHighlightRegion.ColonyStockpile]),
            new("S14 Survey", "S14 - Survey",
                "Geo-Survey deckt Mineralien auf anderen Koerpern im Sol-System auf.\n"
                + "Nutze die Science Fleet und pruefe danach den System Viewer.",
                ["Toolbar Fleet Management", "Science Fleet", "Tab Issue Orders", "Geo Survey ...", "Zielkoerper klicken", "System Viewer (Baum)"],
                [TutorialHighlightRegion.LeftToolbarFleet, TutorialHighlightRegion.FleetList, TutorialHighlightRegion.FleetTabIssueOrders, TutorialHighlightRegion.FleetOrderGeoSurvey, TutorialHighlightRegion.FleetOrderTargets, TutorialHighlightRegion.LeftToolbarSystemTree]),
            new("S15 Standing Orders", "S15 - Standing Orders",
                "Create New Order -> Fuel <= 30 -> Refuel, optional Move to Nearest Geo Survey -> Save.\n"
                + "Nur wenn keine manuellen Orders aktiv sind.",
                ["Tab Standing Orders", "Save"],
                [TutorialHighlightRegion.FleetTabStandingOrders, TutorialHighlightRegion.FleetStandingSave]),
            new("S16 Transfer & Logistik", "S16 - Transfer & Logistik",
                "Stockpile -> Initiate Transfer oder Freight Fleet bewegen.\n"
                + "Neue Kolonien spaeter per Transfer oder Frachter versorgen.",
                ["Colony Management", "Tab Summary", "Initiate Transfer", "Freight Fleet (rechts)"],
                [TutorialHighlightRegion.LeftToolbarColony, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyTransferButton, TutorialHighlightRegion.RightSelectorFleets]),
            new("S17 Sprungpunkte", "S17 - Grav Survey & andere Systeme",
                "Gravitational Survey findet Sprungpunkte. Danach Galaxy Browser oeffnen und Route in andere Systeme verstehen.",
                ["Fleet Management", "Science Fleet", "Galaxy Browser", "System Viewer"],
                [TutorialHighlightRegion.LeftToolbarFleet, TutorialHighlightRegion.FleetList, TutorialHighlightRegion.LeftToolbarGalaxy, TutorialHighlightRegion.LeftToolbarSystemTree]),
            new("S18 Kampf", "S18 - Military Fleet & Kampf",
                "Du startest mit einer Military Fleet (zwei Gunships).\n\n"
                + "Schiff waehlen -> Fire Control -> Ziel -> Open Fire. Vorher Wirtschaft stabil halten.",
                ["Fleets: Military Fleet", "Toolbar Fleet Management"],
                [TutorialHighlightRegion.RightSelectorFleets, TutorialHighlightRegion.LeftToolbarFleet]),
            new("S19 Abschluss", "S19 - Du bist startklar",
                "End-Checkliste:\n[ ] Stabile Wirtschaft inkl. Strom\n[ ] Geo-Survey in Sol\n[ ] Standing Orders fuer Survey/Refuel\n[ ] Sprungroute erkundet\n[ ] Military Fleet getestet\n\nAb hier: normales Spiel.",
                NoHints,
                NoClicks),
            new("N: Wirtschaftskette", "Nachschlagen: Wirtschaftskette",
                "Mining -> Stockpile -> Refinery -> Factory -> Construction -> Shipyard.",
                ["Tab Mining", "Tab Production", "Tab Construction"],
                [TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyTabConstruction]),
            new("N: Materialien", "Nachschlagen: Materialien",
                "Plastic <- Hydrocarbons\nSteel <- Iron + Chromium + Hydrocarbons\nWarehouse <- Iron + Aluminium + Plastic",
                ["Production Job", "Construction Designs"],
                [TutorialHighlightRegion.ProductionNewJob, TutorialHighlightRegion.ConstructionDesigns]),
            new("N: Production", "Nachschlagen: Production",
                "Line -> Job -> Repeat. Missing Resources = Stockpile nachfuellen.",
                ["Lines", "Job-Panel"],
                [TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob]),
            new("N: Flotten", "Nachschlagen: Flotten",
                "Issue Orders / Standing Orders + Save.",
                ["Issue Orders", "Standing Orders"],
                [TutorialHighlightRegion.FleetTabIssueOrders, TutorialHighlightRegion.FleetTabStandingOrders]),
            new("N: Galaxie", "Nachschlagen: Galaxie",
                "System Viewer, Grav Survey, Galaxy Browser, Jumps und Transfer.",
                ["Galaxy Browser", "System Viewer", "Fleet Management"],
                [TutorialHighlightRegion.LeftToolbarGalaxy, TutorialHighlightRegion.LeftToolbarSystemTree, TutorialHighlightRegion.LeftToolbarFleet]),
            new("N: Energie", "Nachschlagen: Energie",
                "Energy-Tab: Generation vs Demand. Kraftwerk plus Batterie halten die Kolonie stabil.",
                ["Tab Energy"],
                [TutorialHighlightRegion.ColonyTabEnergy]),
        ],
        Sections:
        [
            new("Step-by-Step: Stabile Wirtschaft", 1, 13),
            new("Step-by-Step: Expansion & Flotte", 14, 19),
            new("Nachschlagen", 20, 25),
        ]);

    private static readonly TutorialLocale English = new(
        HighlightCheckbox: "UI highlight (current click only)",
        TocIntro: "Build a stable economy first. The orange marker always shows the next click target; after clicking it, the following target appears.",
        StartIntroButton: "Start economy intro",
        NextHintFallback: "Click the highlighted element",
        NextHintHelp: "Click the yellow highlight. If it is missing, open the required window or tab first.",
        PageComplete: "All click targets on this page are done - continue to the next topic.",
        ButtonBackHint: "Previous hint",
        ButtonSkipHint: "Skip hint",
        ButtonContents: "Contents",
        ButtonBack: "Back",
        ButtonStart: "Start",
        ButtonNext: "Next",
        ButtonClose: "Close",
        Pages:
        [
            new("Contents", "Contents", "", NoHints, NoClicks),
            new("S1 Pause & UI", "S1 - Pause & UI",
                "Goal: build a stable economy and touch each core system once.\n\n"
                + "Follow the orange highlights in order (only one at a time).\n"
                + "Click the highlighted element and the next one will appear.",
                ["Pause/Play top left", "Time interval (Hours/Days)", "Funds on the right"],
                [TutorialHighlightRegion.TimeControlPlayPause, TutorialHighlightRegion.TimeControlInterval, TutorialHighlightRegion.RightSelectorFunds]),
            new("S2 Colony & Tabs", "S2 - Colony and tabs",
                "Tabs:\n- Summary: stockpile\n- Mining: extraction\n- Production: refinery/factory/shipyard\n- Construction: build installations\n\n"
                + "Open the stockpile: what do you have and what are you missing?",
                ["Right side: Colonies", "Toolbar: Colony Management", "Pick colony on the left", "Summary tab", "Inspect stockpile"],
                [TutorialHighlightRegion.RightSelectorColonies, TutorialHighlightRegion.LeftToolbarColony, TutorialHighlightRegion.ColonyList, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyStockpile]),
            new("S3 Energy", "S3 - Colony power grid",
                "Colonies need power (kW) for mines, refineries, factories, and shipyards.\n\n"
                + "Quickstart gives you a coal power plant plus a colony battery.\n"
                + "Use the Energy tab to inspect generation, demand, storage, and consumers.\n\n"
                + "Rule: if demand stays above output, your economy will not run reliably.",
                ["Energy tab", "Production tab", "Mining tab"],
                [TutorialHighlightRegion.ColonyTabEnergy, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyTabMining]),
            new("S4 Economic chain", "S4 - Economic chain",
                "A stable economy works like this:\nMining -> Stockpile -> Refinery -> Factory -> Construction -> Shipyard.\n\n"
                + "If one link stalls (full storage, missing material, idle line), the chain breaks.",
                ["Mining tab", "Production tab", "Construction tab", "Stockpile (Summary)"],
                [TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyTabConstruction, TutorialHighlightRegion.ColonyStockpile]),
            new("S5 Understand mining", "S5 - Understand mining",
                "- Number of Mines = extraction capacity\n- Annual Production = theory per year\n- Stockpile = what is actually stored\n- Accessibility 0-1 affects rate\n\n"
                + "Full storage means almost no growth. Fix storage first.",
                ["Open Mining tab", "Summary tab", "Compare stockpile"],
                [TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyStockpile]),
            new("S6 Use production", "S6 - Use production",
                "Every job follows the same flow:\nProduction -> pick a line -> create a job on the right (design, quantity, repeat).\n\n"
                + "Idle = unused. Missing Resources = missing inputs.",
                ["Production tab", "Production line on the left", "Job panel on the right"],
                [TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob]),
            new("S7 Start refining", "S7 - Refinery: materials",
                "Refinery jobs (Repeat):\n- Plastic <- Hydrocarbons\n- Stainless Steel <- Iron + Chromium + Hydrocarbons\n- Optional Fuel <- Hydrocarbons\n\n"
                + "Let time run, then check the stockpile.",
                ["Production tab", "Refinery line", "Create Plastic/Steel job", "Play time", "Summary tab", "Stockpile: Plastic/Steel?"],
                [TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob, TutorialHighlightRegion.TimeControlPlayPause, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyStockpile]),
            new("S8 Construction & Warehouse", "S8 - Construction: warehouse",
                "Construction consumes the stockpile. First goal: build a warehouse.\n"
                + "If Plastic is missing, go back to S7.",
                ["Construction tab", "Choose Warehouse design", "Check queue on the left"],
                [TutorialHighlightRegion.ColonyTabConstruction, TutorialHighlightRegion.ConstructionDesigns, TutorialHighlightRegion.ConstructionQueue]),
            new("S9 Expand extraction", "S9 - Build more mines",
                "Only do this once storage exists: Construction -> Mine -> wait -> check Mining.\n\n"
                + "Do not build mines blindly while storage is full.",
                ["Construction tab", "Choose Mine design", "Mining tab (result)"],
                [TutorialHighlightRegion.ColonyTabConstruction, TutorialHighlightRegion.ConstructionDesigns, TutorialHighlightRegion.ColonyTabMining]),
            new("S10 Factory & steady state", "S10 - Factory & steady state",
                "Factories build components, but early on refineries and construction matter more.\n\n"
                + "Stable means repeating refinery jobs, a sensible queue, and mining that feeds the chain.",
                ["Production tab", "Factory or refinery line", "Check job/repeat"],
                [TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob]),
            new("S11 Research", "S11 - Research",
                "Research unlocks better tech and costs funds per day. Keep the economy from S4-S10 running in parallel.",
                ["Research toolbar", "Select lab", "Double-click a tech", "Tech queue"],
                [TutorialHighlightRegion.LeftToolbarResearch, TutorialHighlightRegion.ResearchLabList, TutorialHighlightRegion.ResearchAvailableTechs, TutorialHighlightRegion.ResearchTechQueue]),
            new("S12 Stability check", "S12 - Stability check",
                "Set interval to 1 Day -> Play -> Pause. Check: stockpile, mining, production not idle or missing, construction progressing, funds not at zero.\n\n"
                + "If not, return to S7-S10.",
                ["Set interval to Days", "Play", "Mining tab", "Production tab", "Stockpile (Summary)"],
                [TutorialHighlightRegion.TimeControlInterval, TutorialHighlightRegion.TimeControlPlayPause, TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyStockpile]),
            new("S13 Economy checklist", "S13 - Stable economy checklist",
                "[ ] Read the stockpile\n[ ] Can create production jobs\n[ ] Plastic + Steel online\n[ ] Warehouse built\n[ ] Mining makes sense\n[ ] Research running\n[ ] Passed S12\n\nThen move on to expansion in S14.",
                ["Final stockpile check"],
                [TutorialHighlightRegion.ColonyStockpile]),
            new("S14 Survey", "S14 - Survey",
                "Geo-survey reveals mineral data on other bodies in Sol. Use the Science Fleet and then inspect the System Viewer.",
                ["Fleet Management toolbar", "Science Fleet", "Issue Orders tab", "Geo Survey ...", "Click target body", "System Viewer (tree)"],
                [TutorialHighlightRegion.LeftToolbarFleet, TutorialHighlightRegion.FleetList, TutorialHighlightRegion.FleetTabIssueOrders, TutorialHighlightRegion.FleetOrderGeoSurvey, TutorialHighlightRegion.FleetOrderTargets, TutorialHighlightRegion.LeftToolbarSystemTree]),
            new("S15 Standing orders", "S15 - Standing orders",
                "Create New Order -> Fuel <= 30 -> Refuel, optionally Move to Nearest Geo Survey -> Save. Only do this when no manual orders are active.",
                ["Standing Orders tab", "Save"],
                [TutorialHighlightRegion.FleetTabStandingOrders, TutorialHighlightRegion.FleetStandingSave]),
            new("S16 Transfer & logistics", "S16 - Transfer & logistics",
                "Use Stockpile -> Initiate Transfer, or move the Freight Fleet. Later colonies need supply by transfer or freighter.",
                ["Colony Management", "Summary tab", "Initiate Transfer", "Freight Fleet (right side)"],
                [TutorialHighlightRegion.LeftToolbarColony, TutorialHighlightRegion.ColonyTabSummary, TutorialHighlightRegion.ColonyTransferButton, TutorialHighlightRegion.RightSelectorFleets]),
            new("S17 Jump points", "S17 - Grav survey & other systems",
                "Gravitational survey reveals jump points. Then open the Galaxy Browser and understand routes into other systems.",
                ["Fleet Management", "Science Fleet", "Galaxy Browser", "System Viewer"],
                [TutorialHighlightRegion.LeftToolbarFleet, TutorialHighlightRegion.FleetList, TutorialHighlightRegion.LeftToolbarGalaxy, TutorialHighlightRegion.LeftToolbarSystemTree]),
            new("S18 Combat", "S18 - Military fleet & combat",
                "You start with a Military Fleet.\n\nSelect a ship -> Fire Control -> target -> Open Fire. Keep the economy stable first.",
                ["Fleets: Military Fleet", "Fleet Management toolbar"],
                [TutorialHighlightRegion.RightSelectorFleets, TutorialHighlightRegion.LeftToolbarFleet]),
            new("S19 Finish", "S19 - You are ready",
                "End checklist:\n[ ] Stable economy including power\n[ ] Geo-survey in Sol\n[ ] Standing orders for survey/refuel\n[ ] Jump route explored\n[ ] Military Fleet tested\n\nFrom here: normal play.",
                NoHints,
                NoClicks),
            new("R: Economic chain", "Reference: economic chain",
                "Mining -> Stockpile -> Refinery -> Factory -> Construction -> Shipyard.",
                ["Mining tab", "Production tab", "Construction tab"],
                [TutorialHighlightRegion.ColonyTabMining, TutorialHighlightRegion.ColonyTabProduction, TutorialHighlightRegion.ColonyTabConstruction]),
            new("R: Materials", "Reference: materials",
                "Plastic <- Hydrocarbons\nSteel <- Iron + Chromium + Hydrocarbons\nWarehouse <- Iron + Aluminium + Plastic",
                ["Production job", "Construction designs"],
                [TutorialHighlightRegion.ProductionNewJob, TutorialHighlightRegion.ConstructionDesigns]),
            new("R: Production", "Reference: production",
                "Line -> Job -> Repeat. Missing Resources means refill the stockpile.",
                ["Lines", "Job panel"],
                [TutorialHighlightRegion.ProductionLines, TutorialHighlightRegion.ProductionNewJob]),
            new("R: Fleets", "Reference: fleets",
                "Issue Orders / Standing Orders + Save.",
                ["Issue Orders", "Standing Orders"],
                [TutorialHighlightRegion.FleetTabIssueOrders, TutorialHighlightRegion.FleetTabStandingOrders]),
            new("R: Galaxy", "Reference: galaxy",
                "System Viewer, grav survey, Galaxy Browser, jumps, and transfer logistics.",
                ["Galaxy Browser", "System Viewer", "Fleet Management"],
                [TutorialHighlightRegion.LeftToolbarGalaxy, TutorialHighlightRegion.LeftToolbarSystemTree, TutorialHighlightRegion.LeftToolbarFleet]),
            new("R: Energy", "Reference: energy",
                "The Energy tab compares generation vs demand. Power plants plus batteries keep the colony stable.",
                ["Energy tab"],
                [TutorialHighlightRegion.ColonyTabEnergy]),
        ],
        Sections:
        [
            new("Step-by-Step: Stable economy", 1, 13),
            new("Step-by-Step: Expansion & fleet", 14, 19),
            new("Reference", 20, 25),
        ]);

    private TutorialGuideWindow()
    {
    }

    internal static TutorialGuideWindow GetInstance()
    {
        if (_uiState.TryGetUniqueWindow<TutorialGuideWindow>(out var window))
            return window;

        return _uiState.AddUniqueWindow(new TutorialGuideWindow());
    }

    private TutorialLocale Locale => _language == TutorialLanguage.German ? German : English;

    internal static bool IsTutorialColonyId(string? colonyId) =>
        !string.IsNullOrEmpty(colonyId)
        && colonyId.Contains("tutorial-start-guided-economy", StringComparison.OrdinalIgnoreCase);

    internal void SetLanguage(TutorialLanguage language)
    {
        _language = language;
        _boundPageForPath = -1;
        SyncHighlightPath();
    }

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

        var pages = Locale.Pages;
        _stepIndex = Math.Clamp(_stepIndex, 0, pages.Length - 1);
        SyncHighlightPath();

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 540), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(40, 80), ImGuiCond.Appearing);

        if (!Window.Begin("Tutorial", ref IsActive))
        {
            Window.End();
            return;
        }

        if (ImGui.Checkbox(Locale.HighlightCheckbox, ref _showHighlight))
            SyncHighlightPath();

        ImGui.SameLine();
        int selectedLanguage = (int)_language;
        string[] languageItems = ["Deutsch", "English"];
        if (ImGui.Combo("##tutorial-language", ref selectedLanguage, languageItems, languageItems.Length))
            SetLanguage((TutorialLanguage)selectedLanguage);

        ImGui.Separator();

        if (_stepIndex == 0)
            DisplayTableOfContents();
        else
            DisplayContentPage(pages[_stepIndex]);

        ImGui.Spacing();
        ImGui.Separator();
        DisplayNavButtons();

        _stepIndex = Math.Clamp(_stepIndex, 0, pages.Length - 1);
        SyncHighlightPath();

        Window.End();
    }

    private void SyncHighlightPath()
    {
        var page = Locale.Pages[_stepIndex];
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
        ImGui.TextWrapped(Locale.TocIntro);
        ImGui.Spacing();

        if (ImGui.Button(Locale.StartIntroButton, new System.Numerics.Vector2(-1, 0)))
        {
            _stepIndex = 1;
            _boundPageForPath = -1;
        }

        ImGui.Spacing();
        foreach (var section in Locale.Sections)
            DrawTocSection(section.Heading, section.First, section.Last);
    }

    private void DrawTocSection(string heading, int firstIndex, int lastIndex)
    {
        var pages = Locale.Pages;
        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.55f, 0.75f, 1f, 1f), heading);
        for (int i = firstIndex; i <= lastIndex && i < pages.Length; i++)
        {
            if (ImGui.Selectable(pages[i].TocLabel))
            {
                _stepIndex = i;
                _boundPageForPath = -1;
            }
        }
    }

    private void DisplayContentPage(TutorialPage page)
    {
        ImGui.Text(page.Title);
        ImGui.SameLine();
        ImGui.TextDisabled($"({_stepIndex}/{Locale.Pages.Length - 1})");
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
                ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.9f, 0.5f, 1f), Locale.PageComplete);
            }
            else
            {
                string hint = idx < page.HintLabels.Length ? page.HintLabels[idx] : Locale.NextHintFallback;
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.82f, 0.2f, 1f), $"({idx + 1}/{total}) {hint}");
                ImGui.TextDisabled(Locale.NextHintHelp);
            }

            if (ImGui.Button(Locale.ButtonBackHint) && TutorialHighlight.PathIndex > 0)
                TutorialHighlight.Retreat();
            ImGui.SameLine();
            if (ImGui.Button(Locale.ButtonSkipHint) && !TutorialHighlight.PathComplete)
                TutorialHighlight.Advance();
        }
    }

    private void DisplayNavButtons()
    {
        if (_stepIndex > 0)
        {
            if (ImGui.Button(Locale.ButtonContents))
            {
                _stepIndex = 0;
                _boundPageForPath = -1;
            }
            ImGui.SameLine();
        }

        if (ImGui.Button(Locale.ButtonBack) && _stepIndex > 0)
        {
            _stepIndex--;
            _boundPageForPath = -1;
        }

        ImGui.SameLine();
        if (_stepIndex == 0)
        {
            if (ImGui.Button(Locale.ButtonStart))
            {
                _stepIndex = 1;
                _boundPageForPath = -1;
            }
        }
        else if (_stepIndex < Locale.Pages.Length - 1)
        {
            if (ImGui.Button(Locale.ButtonNext))
            {
                _stepIndex++;
                _boundPageForPath = -1;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Locale.ButtonClose))
            IsActive = false;
    }
}
