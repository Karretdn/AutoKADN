using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using AutoKADN.Proyectos.Services;

namespace AutoKADN.Proyectos;

public partial class ResultWindow : Window
{
    private readonly CreationResult _result;

    public ResultWindow(CreationResult result)
    {
        InitializeComponent();
        _result = result;

        TitleText.Text = result.Success ? "Proyecto creado" : "No se pudo crear el proyecto";
        TitleText.Foreground = (Brush)FindResource(result.Success ? "Ok" : "Danger");
        PathText.Text = result.ProjectFolder;

        var okBrush = (Brush)FindResource("Ok");
        var badBrush = (Brush)FindResource("Danger");
        StepsList.ItemsSource = result.Steps.Select(s => new
        {
            Mark = s.Ok ? "✔" : "✖",
            MarkBrush = s.Ok ? okBrush : badBrush,
            s.Description,
            Detail = s.Detail ?? string.Empty,
        }).ToList();

        NoteText.Text = result.Success
            ? "Abre el DWG de PLANOS en AutoCAD y usa el botón «Rellenar datos» de AutoKADN para completar el cajetín."
            : result.Error ?? string.Empty;

        OpenButton.IsEnabled = Directory.Exists(result.ProjectFolder);
        NewButton.Visibility = result.Success ? Visibility.Visible : Visibility.Collapsed;
        PhotosButton.Visibility = result.Success ? Visibility.Visible : Visibility.Collapsed;
        FormatsButton.Visibility = result.Success ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>true si la persona pidió empezar otro proyecto.</summary>
    public bool StartNew { get; private set; }

    /// <summary>true si la persona quiere ir al registro fotográfico del proyecto recién creado.</summary>
    public bool OpenPhotos { get; private set; }

    /// <summary>true si la persona quiere generar los formatos de interventoría del proyecto recién creado.</summary>
    public bool OpenFormats { get; private set; }

    private void OnFormats(object sender, RoutedEventArgs e)
    {
        OpenFormats = true;
        Close();
    }

    private void OnPhotos(object sender, RoutedEventArgs e)
    {
        OpenPhotos = true;
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_result.ProjectFolder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AutoKADN", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnNew(object sender, RoutedEventArgs e)
    {
        StartNew = true;
        Close();
    }
}
