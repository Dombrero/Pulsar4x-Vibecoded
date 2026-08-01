using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Pulsar4X.Client.Interface.Widgets;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Datablobs;
using Pulsar4X.Factions;
using Pulsar4X.Names;
using Pulsar4X.Interfaces;
using Pulsar4X.Storage;
using Pulsar4X.Client.Host;

namespace Pulsar4X.Client
{
    /// <summary>
    /// Aurora-style Spacemaster / God Mode: browse entities and freely edit cargo, transfer rates, etc.
    /// </summary>
    public class SMWindow : UniquePulsarGuiWindow<SMWindow>
    {
        private Game? _game;
        private StarSystem? _currentSystem;

        private int _selectedEntityIndex = -1;
        private Entity[] _systemEntities = Array.Empty<Entity>();
        private string[] _systemEntityNames = Array.Empty<string>();

        private int _smTab;
        private readonly Dictionary<int, int> _cargoEditBuffers = new();
        private int _transferRateEdit;
        private float _transferRangeEdit;
        private bool _transferStatsLoaded;

        Entity? SelectedEntity
        {
            get
            {
                if (_selectedEntityIndex >= 0 && _selectedEntityIndex < _systemEntities.Length)
                    return _systemEntities[_selectedEntityIndex];
                return null;
            }
        }

        private SMWindow()
        {
            HardRefresh();

            _uiState.OnStarSystemChanged += _ =>
            {
                _selectedEntityIndex = -1;
                _transferStatsLoaded = false;
                HardRefresh();
            };
        }

        public static SMWindow GetInstance()
        {
            if (_uiState.TryGetUniqueWindow<SMWindow>(out var window))
                return window;

            return _uiState.AddUniqueWindow(new SMWindow());
        }

        void HardRefresh()
        {
            if (GameLifecycle.Instance?.Game == null || GameLifecycle.Instance.SelectedSystem == null)
                return;

            _game = GameLifecycle.Instance.Game;
            _currentSystem = GameLifecycle.Instance.SelectedSystem;
            _systemEntities = _currentSystem.GetAllEntites().ToArray();
            _systemEntityNames = new string[_systemEntities.Length];
            for (int i = 0; i < _systemEntities.Length; i++)
            {
                var entity = _systemEntities[i];
                if (entity.HasDataBlob<NameDB>())
                    _systemEntityNames[i] = entity.GetDataBlob<NameDB>().OwnersName;
                else
                    _systemEntityNames[i] = "No NameDB";
            }
        }

        private bool _entityInspectorWindow = false;

        internal override void Display()
        {
            if (!_uiState.SMenabled || _game == null)
                return;

            ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(480, 320), new System.Numerics.Vector2(1200, 900));
            if (Window.Begin("Spacemaster (God Mode)", ref IsActive, _flags))
            {
                ImGui.TextDisabled("Alle Werte frei editierbar — nur im SM-Modus.");
                if (ImGui.Button("SM-Modus beenden"))
                {
                    _uiState.ToggleGameMaster();
                    SetActive(false);
                    Window.End();
                    return;
                }

                ImGui.SameLine();
                if (ImGui.Button("Liste aktualisieren"))
                    HardRefresh();

                ImGui.Separator();
                ImGui.Columns(2, "sm-cols", true);
                ImGui.SetColumnWidth(0, 220);

                ImGui.BeginChild("sm-entity-list", new System.Numerics.Vector2(0, 0), ImGuiChildFlags.None);
                for (int i = 0; i < _systemEntities.Length; i++)
                {
                    ImGui.PushID(_systemEntities[i].Id);
                    bool isSelected = _selectedEntityIndex == i;
                    string label = _systemEntityNames[i];
                    if (_systemEntities[i].HasDataBlob<ColonyInfoDB>())
                        label = "[Kolonie] " + label;
                    else if (_systemEntities[i].HasDataBlob<CargoStorageDB>())
                        label = "[Lager] " + label;

                    if (ImGui.Selectable(label, isSelected))
                    {
                        if (i == _selectedEntityIndex)
                        {
                            _selectedEntityIndex = -1;
                            _entityInspectorWindow = false;
                            _transferStatsLoaded = false;
                        }
                        else
                        {
                            _selectedEntityIndex = i;
                            _entityInspectorWindow = true;
                            _transferStatsLoaded = false;
                            _cargoEditBuffers.Clear();
                        }
                    }

                    ImGui.PopID();
                }
                ImGui.EndChild();

                ImGui.NextColumn();
                DisplaySelectedEntityPanel();
                ImGui.Columns(1);
            }
            Window.End();

            if (_entityInspectorWindow && SelectedEntity != null && _smTab == 1)
                EntityInspector.Begin(SelectedEntity);
        }

        void DisplaySelectedEntityPanel()
        {
            var entity = SelectedEntity;
            if (entity == null)
            {
                ImGui.TextWrapped("Entity links auswählen, um Ressourcen und Werte zu ändern.");
                return;
            }

            ImGui.Text(entity.HasDataBlob<NameDB>()
                ? entity.GetDataBlob<NameDB>().OwnersName
                : $"Entity {entity.Id}");
            ImGui.TextDisabled($"ID {entity.Id} · Faction {entity.GetFactionName()}");

            if (ImGui.BeginTabBar("sm-entity-tabs"))
            {
                if (ImGui.BeginTabItem("Ressourcen"))
                {
                    _smTab = 0;
                    DisplayCargoEditor(entity);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Inspector"))
                {
                    _smTab = 1;
                    ImGui.TextWrapped("Datablob-Inspector öffnet als Extra-Fenster.");
                    if (ImGui.Button("Entity Inspector öffnen"))
                        _entityInspectorWindow = true;
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        void DisplayCargoEditor(Entity entity)
        {
            if (!entity.TryGetDataBlob<CargoStorageDB>(out var storage))
            {
                ImGui.TextDisabled("Kein CargoStorageDB an dieser Entity.");
                return;
            }

            if (!_transferStatsLoaded)
            {
                _transferRateEdit = storage.TransferRate;
                _transferRangeEdit = (float)storage.TransferRangeDv_mps;
                _transferStatsLoaded = true;
            }

            ImGui.Text("Transfer");
            ImGui.InputInt("Rate (kg/s)##sm-rate", ref _transferRateEdit);
            ImGui.InputFloat("Range (Δv m/s)##sm-range", ref _transferRangeEdit);
            if (ImGui.Button("Transfer-Werte setzen"))
            {
                SpacemasterCargo.SetTransferStats(storage, _transferRateEdit, _transferRangeEdit);
            }

            ImGui.Separator();
            ImGui.Text("Lagerbestand (Einheiten)");

            var faction = entity.GetFactionOwner;
            var library = faction.GetDataBlob<FactionInfoDB>().Data.CargoGoods;
            var allGoods = library.GetAll().Values
                .OrderBy(g => g.Name)
                .ToList();

            // Also include anything already in stores that might not be in the unlocked list.
            var seen = new HashSet<int>();
            foreach (var store in storage.TypeStores.Values)
            {
                foreach (var cargoable in store.GetCargoables().Values)
                    seen.Add(cargoable.ID);
            }

            ImGui.BeginChild("sm-cargo-edit", new System.Numerics.Vector2(0, 0), ImGuiChildFlags.Borders);
            foreach (var good in allGoods)
            {
                if (!storage.TypeStores.ContainsKey(good.CargoTypeID))
                    continue;

                DrawCargoRow(storage, good);
                seen.Remove(good.ID);
            }

            foreach (var store in storage.TypeStores.Values)
            {
                foreach (var kvp in store.GetCargoables())
                {
                    if (!seen.Contains(kvp.Key))
                        continue;
                    DrawCargoRow(storage, kvp.Value);
                }
            }

            ImGui.EndChild();
        }

        void DrawCargoRow(CargoStorageDB storage, ICargoable good)
        {
            long current = SpacemasterCargo.GetUnits(storage, good);
            if (!_cargoEditBuffers.TryGetValue(good.ID, out int editVal))
            {
                editVal = (int)Math.Clamp(current, int.MinValue, int.MaxValue);
                _cargoEditBuffers[good.ID] = editVal;
            }

            ImGui.PushID(good.ID);
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt(good.Name, ref editVal))
                _cargoEditBuffers[good.ID] = editVal;

            ImGui.SameLine();
            if (ImGui.Button("Set"))
            {
                long applied = SpacemasterCargo.SetUnits(storage, good, editVal);
                _cargoEditBuffers[good.ID] = (int)Math.Clamp(applied, int.MinValue, int.MaxValue);
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"aktuell {current}");
            ImGui.PopID();
        }

        internal override void EntityClicked(EntityState entity, MouseButtons button)
        {
        }
    }
}
