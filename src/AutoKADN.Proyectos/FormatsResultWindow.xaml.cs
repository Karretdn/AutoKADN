using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using AutoKADN.Proyectos.Services.Formatos;

namespace AutoKADN.Proyectos;

public partial class FormatsResultWindow : Window
{
    private readonly string _projectFolder;

    public FormatsResultWindow(IReadOnlyList<FormatOutcome> outcomes, string projectFolder)
    {
        InitializeComponent();
        _projectFolder = projectFolder;

        int generated = outcomes.Count(o => o.Status == FormatStatus.Generated);
        TitleText.Text = generated == 0 ? "No se generó ningún formato" : $"Formatos generados: {generated} de {outcomes.Count}";
        PathText.Text = projectFolder;

        var ok = (Brush)FindResource("Ok");
        var bad = (Brush)FindResource("Danger");
        var muted = (Brush)FindResource("TextMuted");
        OutcomeList.ItemsSource = outcomes.Select(o => new
        {
            Mark = o.Status switch { FormatStatus.Generated => "✔", FormatStatus.Skipped => "–", _ => "✖" },
            MarkBrush = o.Status switch { FormatStatus.Generated => ok, FormatStatus.Skipped => muted, _ => bad },
            Title = o.Name,
            Detail = o.Detail ?? string.Empty,
            Path = o.OutputPath ?? string.Empty,
        }).ToList();

        var withoutData = outcomes.Where(o => o.Report is { NoData.Count: > 0 }).ToList();
        NoteText.Text = withoutData.Count > 0
            ? "Hay celdas conectadas cuyo dato no existe en los archivos del proyecto (datos_proyecto.json o resumen_obra.json): quedaron en blanco."
            : string.Empty;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_projectFolder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AutoKADN", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
