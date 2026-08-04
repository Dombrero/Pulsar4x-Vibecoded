using System;
using System.IO;
using Pulsar4X.Client.Interface.Widgets;

namespace Pulsar4X.Client.Interface.Menus;

public class SaveGame : UniquePulsarGuiWindow<SaveGame>
{
    private string _filePath = Path.Combine(PulsarMainWindow.GetAppDataPath() ?? "", PulsarMainWindow.SavesPath);
    private string _fileName = "savegame.sav";

    private SaveGame() { }

    internal static SaveGame GetInstance()
    {
        if (_uiState.TryGetUniqueWindow<SaveGame>(out var window))
        {
            return window;
        }
        return _uiState.AddUniqueWindow(new SaveGame());
    }

    internal override void Display()
    {
        if (IsActive && FileDialog.DisplaySave(ref _filePath, ref _fileName, ref IsActive))
        {
            if (String.IsNullOrEmpty(_fileName) || String.IsNullOrEmpty(_filePath))
            {
                IsActive = false;
                return;
            }

            try
            {
                if (!Directory.Exists(_filePath))
                    Directory.CreateDirectory(_filePath);

                string name = _fileName;
                if (!name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
                    name += ".sav";

                _uiState.Lifecycle?.SaveGame(Path.Combine(_filePath, name));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SaveGame UI Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }

    public void UpdateSaveName(string name)
    {
        _fileName = name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ? name : name + ".sav";
    }
}
