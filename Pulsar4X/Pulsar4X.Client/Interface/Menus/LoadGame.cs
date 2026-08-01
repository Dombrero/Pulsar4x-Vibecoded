using System;
using System.IO;
using Pulsar4X.Client.Interface.Widgets;

namespace Pulsar4X.Client.Interface.Menus;

public class LoadGame : UniquePulsarGuiWindow<LoadGame>
{
    private string _filePath = Path.Combine(PulsarMainWindow.GetAppDataPath() ?? "", PulsarMainWindow.SavesPath);
    private string _fileName = "savegame.sav";

    private LoadGame() {}

    internal static LoadGame GetInstance()
    {
        if(_uiState.TryGetUniqueWindow<LoadGame>(out var window))
        {
            return window;
        }
        return _uiState.AddUniqueWindow(new LoadGame());
    }

    internal void LoadLatest()
    {
        try
        {
            if (!Directory.Exists(_filePath))
            {
                Console.WriteLine($"LoadLatest: saves folder missing: {_filePath}");
                return;
            }

            string? fileToLoad = null;
            DateTime best = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(_filePath, "*.sav"))
            {
                var write = File.GetLastWriteTime(file);
                if (write > best)
                {
                    best = write;
                    fileToLoad = file;
                }
            }

            if (!string.IsNullOrEmpty(fileToLoad))
                LoadFile(fileToLoad);
            else
                Console.WriteLine("LoadLatest: no .sav files found");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LoadLatest Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    internal void LoadFile(string filenamepath)
    {
        try
        {
            var activation = _uiState.Lifecycle?.LoadGame(filenamepath);
            if (activation == null)
            {
                // Lifecycle.LoadGame already logged the root cause (LoadGame Error: …).
                Console.WriteLine($"LoadFile failed: {filenamepath} (see preceding LoadGame Error for details)");
                return;
            }

            _uiState.ActivateGameUI(activation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LoadFile Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    internal override void Display()
    {
        if (IsActive && FileDialog.DisplayLoad(ref _filePath, ref _fileName, ref IsActive))
        {
            if (String.IsNullOrEmpty(_fileName) || String.IsNullOrEmpty(_filePath))
            {
                IsActive = false;
                return;
            }
            LoadFile(Path.Combine(_filePath, _fileName));

            IsActive = false;
        }
    }
}
