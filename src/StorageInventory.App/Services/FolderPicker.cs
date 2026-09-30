using Microsoft.Win32;

namespace StorageInventory.App.Services;

/// <summary>Asks the user to choose a folder. Choosing only returns a path; nothing is created or opened.</summary>
public interface IFolderPicker
{
    string? PickFolder(string title, string? initialPath);
}

public sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string title, string? initialPath)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialPath) && System.IO.Directory.Exists(initialPath)) dialog.InitialDirectory = initialPath;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
