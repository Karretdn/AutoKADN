using System.IO;
using System.Windows;
using AutoKADN.Proyectos.Services;
using AutoKADN.Proyectos.Services.Formatos;
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

    private void OnFormatsClick(object sender, RoutedEventArgs e) => RunFormats(null);

    // Genera los formatos de interventoría uno por uno: cada uno pregunta dónde guardarlo y, si se cancela, se omite.
    private void RunFormats(string? projectFolder)
    {
        projectFolder ??= FolderPicker.Pick(this, "Carpeta del proyecto (la que contiene PLANOS)", _settings.RootFolder);
        if (projectFolder is null) return;

        // Si eligieron la propia carpeta PLANOS, el proyecto es la de arriba.
        string trimmed = projectFolder.TrimEnd('\\', '/');
        if (string.Equals(Path.GetFileName(trimmed), "PLANOS", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(trimmed) is string up)
            projectFolder = up;

        if (FormatSource.LocateFile(projectFolder, ProjectDataFile.FileName) is null)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "En esta carpeta no está " + ProjectDataFile.FileName + " (los proyectos creados antes de esta función no lo tienen):\n" + projectFolder +
                "\n\n¿Generar los formatos de todas formas?\nLa orden y el proyecto se toman del nombre de la carpeta y el interventor de la carpeta superior; " +
                "las fechas y demás datos quedan en blanco.",
                "AutoKADN", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        List<FormatOutcome> outcomes = InterventoriaGenerator.Run(projectFolder, _settings.ResourcesFolder, AskFormatPath);
        new FormatsResultWindow(outcomes, projectFolder) { Owner = this }.ShowDialog();
    }

    private string? AskFormatPath(string suggestedName, string projectFolder)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Guardar formato - " + Path.GetFileNameWithoutExtension(suggestedName),
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = suggestedName,
            InitialDirectory = projectFolder,
            AddExtension = true,
            DefaultExt = ".pdf",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
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
            else if (window.OpenFormats) RunFormats(result.ProjectFolder);
            else if (window.StartNew) OnPickPdfClick(this, new RoutedEventArgs());
        }
        else _viewModel.ReloadEnvironment();
    }
}
