using System.Windows;
using System.Windows.Interop;

namespace AutoKADN.Core;

/// <summary>Diálogos simples que aparecen encima de la ventana de AutoCAD (no detrás).</summary>
internal static class Dialogs
{
    public static MessageBoxResult Ask(string message, string title, MessageBoxButton buttons, MessageBoxImage image = MessageBoxImage.Question)
    {
        var owner = new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, Opacity = 0
        };
        try { new WindowInteropHelper(owner).Owner = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle; } catch { }
        owner.Show();
        try { return MessageBox.Show(owner, message, title, buttons, image); }
        finally { owner.Close(); }
    }
}
