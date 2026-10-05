using System.IO;
using System.Windows;
using AutoKADN.Proyectos.Services;
using AutoKADN.Proyectos.ViewModels;
using Microsoft.Win32;

namespace AutoKADN.Proyectos;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        // Solo desarrollo: raíz de prueba en memoria (no se guarda).
        string? devRoot = Environment.GetEnvironmentVariable("AUTOKADN_DEV_ROOT");
        if (!string.IsNullOrWhiteSpace(devRoot)) _settings.RootFolder = devRoot;
        _viewModel = new MainViewModel(_settings);
        DataContext = _viewModel;
        ContentRendered += OnFirstRender;
    }

    public MainViewModel ViewModel => _viewModel;

    // Primera vez (o rutas inválidas): se piden las rutas antes de empezar.
    private void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        if (!_settings.IsConfigured || AppSettings.MissingResources(_settings.ResourcesFolder).Count > 0) OpenSettings();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true) _viewModel.ReloadEnvironment();
    }

    private void OnPhotosClick(object sender, RoutedEventArgs e) => OpenPhotos(null);

    private void OpenPhotos(string? folder)
    {
        var window = new PhotosWindow(_settings, folder) { Owner = this };
        window.ShowDialog();
    }

    private void OnPickPdfClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Orden de trabajo", Filter = "Orden de trabajo (*.pdf)|*.pdf", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _viewModel.LoadPdf(dialog.FileName);
    }

    private static string? DroppedPdf(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 &&
            string.Equals(Path.GetExtension(files[0]), ".pdf", StringComparison.OrdinalIgnoreCase)) return files[0];
        return null;
    }

    private void OnPdfDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPdf(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnPdfDrop(object sender, DragEventArgs e)
    {
        if (DroppedPdf(e) is string path) _viewModel.LoadPdf(path);
        e.Handled = true;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanCreate) return;
        _viewModel.RememberChoices();

        CreationResult result;
        Cursor = System.Windows.Input.Cursors.Wait;
        try { result = ProjectCreator.Create(_viewModel.BuildRequest()); }
        finally { Cursor = null; }

        var window = new ResultWindow(result) { Owner = this };
        window.ShowDialog();

        if (result.Success)
        {
            _viewModel.ResetForNewProject();
            if (window.OpenPhotos) OpenPhotos(Path.Combine(result.ProjectFolder, "FOTOS"));
            else if (window.StartNew) OnPickPdfClick(this, new RoutedEventArgs());
        }
        else _viewModel.ReloadEnvironment();
    }
}
