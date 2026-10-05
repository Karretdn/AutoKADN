using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoKADN.Proyectos.Photos;
using AutoKADN.Proyectos.Services;
using AutoKADN.Proyectos.ViewModels;
using Microsoft.Win32;

namespace AutoKADN.Proyectos;

public partial class PhotosWindow : Window
{
    private readonly PhotosViewModel _viewModel;

    public PhotosWindow(AppSettings settings, string? folder = null)
    {
        InitializeComponent();
        _viewModel = new PhotosViewModel(settings);
        DataContext = _viewModel;

        // Sin carpeta preseleccionada: solo se carga una si viene indicada (el proyecto recién creado).
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) _viewModel.LoadFolder(folder);
    }

    public PhotosViewModel ViewModel => _viewModel;

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        string? chosen = FolderPicker.Pick(this, "Carpeta con las fotos", _viewModel.Folder);
        if (chosen is not null) _viewModel.LoadFolder(chosen);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolder(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DroppedFolder(e) is string folder) _viewModel.LoadFolder(folder);
        e.Handled = true;
    }

    // Se acepta una carpeta o un archivo (se usa su carpeta).
    private static string? DroppedFolder(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items || items.Length == 0) return null;
        if (Directory.Exists(items[0])) return items[0];
        return File.Exists(items[0]) ? Path.GetDirectoryName(items[0]) : null;
    }

    private static PhotoItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PhotoItem;

    private void OnMoveBefore(object sender, RoutedEventArgs e) { if (ItemOf(sender) is PhotoItem item) _viewModel.MoveBefore(item); }
    private void OnMoveAfter(object sender, RoutedEventArgs e) { if (ItemOf(sender) is PhotoItem item) _viewModel.MoveAfter(item); }
    private void OnRemove(object sender, RoutedEventArgs e) { if (ItemOf(sender) is PhotoItem item) _viewModel.Remove(item); }

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanGenerate) return;

        // Un Excel que ya tiene cambios propios (no es la plantilla en blanco) no se pisa sin preguntar.
        if (!_viewModel.OutputIsBlankTemplate())
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"Ya existe {PhotosViewModel.OutputFileName} en esta carpeta y no es la plantilla en blanco.\n\n¿Reemplazarlo con el nuevo registro fotográfico?",
                "AutoKADN", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        _viewModel.SetBusy(true, "Preparando…");
        try
        {
            PhotoBookResult result = await System.Threading.Tasks.Task.Run(() =>
                _viewModel.Generate(text => Dispatcher.BeginInvoke(new Action(() => _viewModel.ReportProgress(text)))));
            _viewModel.SetBusy(false, string.Empty);

            string pages = result.PhotosPerPage.Length == 1 ? "1 página" : $"{result.PhotosPerPage.Length} páginas ({string.Join(" + ", result.PhotosPerPage)})";
            MessageBoxResult open = MessageBox.Show(this,
                $"Registro fotográfico generado: {result.Photos} foto(s) en {pages}.\n\n{_viewModel.OutputPath}\n\n¿Abrirlo ahora?",
                "AutoKADN", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(_viewModel.OutputPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.SetBusy(false, string.Empty);
            MessageBox.Show(this, "No se pudo generar el Excel:\n\n" + ex.Message +
                                  "\n\nSi el archivo está abierto en Excel, ciérralo e inténtalo de nuevo.",
                "AutoKADN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
