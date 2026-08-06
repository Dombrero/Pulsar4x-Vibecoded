using System;
using System.IO;
using ImGuiNET;
using Pulsar4X.Blueprints;
using Pulsar4X.Modding;

namespace Pulsar4X.Client.ModFileEditing;

public class ModFileEditor : UniquePulsarGuiWindow<ModFileEditor>
{
    private ModInfoUI? _modInfoUI;
    private TechBlueprintUI? _techBlueprintUI;
    private TechCatBlueprintUI? _techCatBlueprintUI;
    private ComponentBluprintUI? _componentBluprintUI;
    private CargoTypeBlueprintUI? _cargoTypeBlueprintUI;
    private ArmorBlueprintUI? _armorBlueprintUI;
    private ProcessedMaterialsUI? _processedMaterialsUI;
    private MineralBlueprintUI? _mineralsBlueprintUI;
    private ShipDesignBlueprintUI? _shipDesignBlueprintUI;
    private ModFileEditor()
    {

    }
    internal static ModFileEditor GetInstance()
    {
        if (_uiState!.TryGetUniqueWindow<ModFileEditor>(out var existing))
            return existing;

        var instance = new ModFileEditor();
        ModLoader modLoader = new ModLoader();
        ModDataStore modDataStore = new ModDataStore();
        string? appDataDirectory = PulsarMainWindow.GetAppDataPath();
        if (string.IsNullOrEmpty(appDataDirectory))
            throw new InvalidOperationException("Application data path is not available.");
        string modPath = Path.Combine(appDataDirectory, PulsarMainWindow.ModsPath, "basemod/modInfo.json");
        modLoader.LoadModManifest(modPath, modDataStore);
        instance.Refresh(modDataStore);
        _uiState.AddUniqueWindow(instance);
        return instance;
    }

    public void Refresh(ModDataStore modDataStore)
    {
        _modInfoUI = new ModInfoUI(modDataStore);
        _techCatBlueprintUI = new TechCatBlueprintUI(modDataStore);
        _techBlueprintUI = new TechBlueprintUI(modDataStore);
        _componentBluprintUI = new ComponentBluprintUI(modDataStore);
        _cargoTypeBlueprintUI = new CargoTypeBlueprintUI(modDataStore);

        _armorBlueprintUI = new ArmorBlueprintUI(modDataStore);
        _processedMaterialsUI = new ProcessedMaterialsUI(modDataStore);
        _mineralsBlueprintUI = new MineralBlueprintUI(modDataStore);
        _shipDesignBlueprintUI = new ShipDesignBlueprintUI(modDataStore);
    }


    internal override void Display()
    {

        if (IsActive)
        {
            if (ImGui.Begin("Editor", ref IsActive))
            {
                (_modInfoUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Mod Info");
                ImGui.NewLine();
                (_techCatBlueprintUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Tech Categorys");
                ImGui.NewLine();
                (_techBlueprintUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Techs");
                ImGui.NewLine();
                (_componentBluprintUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Components");
                ImGui.NewLine();
                (_cargoTypeBlueprintUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Cargo Types");
                ImGui.NewLine();
                (_armorBlueprintUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Armor");
                ImGui.NewLine();
                (_processedMaterialsUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Processed Materials");
                ImGui.NewLine();
                (_mineralsBlueprintUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Minerals");
                ImGui.NewLine();
                (_shipDesignBlueprintUI ?? throw new InvalidOperationException("Mod editor not initialized.")).Display("Ship Designs");
                ImGui.NewLine();
            }

            ImGui.End();
        }
    }
}