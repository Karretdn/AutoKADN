using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace AutoKADN.Proyectos.Services;

/// <summary>Selector de carpeta que no se cae si la ruta inicial es rara: la normaliza o la ignora.</summary>
public static class FolderPicker
{
    public static string? Pick(Window owner, string title, string? initialFolder)
    {
        string? initial = NormalizeExisting(initialFolder);
        try
        {
            return Show(owner, title, initial);
        }
        catch (ArgumentException)
        {
            // El cuadro de Windows rechazó la ruta inicial: se reintenta sin ella.
            return Show(owner, title, null);
        }
    }

    private static string? Show(Window owner, string title, string? initial)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initial is not null) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    private static string? NormalizeExisting(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            string full = Path.GetFullPath(folder.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch { return null; }
    }
}
