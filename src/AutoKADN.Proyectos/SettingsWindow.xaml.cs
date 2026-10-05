using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoKADN.Proyectos.Services;
using Microsoft.Win32;

namespace AutoKADN.Proyectos;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        RootBox.Text = settings.RootFolder;
        ResourcesBox.Text = settings.ResourcesFolder;
        Validate();
    }

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        if (RootStatus is not null && ResourcesStatus is not null) Validate();
    }

    private void OnPickRoot(object sender, RoutedEventArgs e)
    {
        string? chosen = FolderPicker.Pick(this, "Carpeta raíz de proyectos", RootBox.Text);
        if (chosen is not null) RootBox.Text = chosen;
    }

    private void OnPickResources(object sender, RoutedEventArgs e)
    {
        string? chosen = FolderPicker.Pick(this, "Carpeta con los archivos base", ResourcesBox.Text);
        if (chosen is not null) ResourcesBox.Text = chosen;
    }

    private bool Validate()
    {
        bool rootOk = Directory.Exists(RootBox.Text.Trim());
        if (rootOk)
        {
            int count = Directory.GetDirectories(RootBox.Text.Trim()).Length;
            Show(RootStatus, true, $"✔ Carpeta encontrada · {count} carpeta(s) de interventor");
        }
        else Show(RootStatus, false, RootBox.Text.Trim().Length == 0 ? "Elige la carpeta raíz." : "✖ Esa carpeta no existe.");

        bool resourcesOk = false;
        string resources = ResourcesBox.Text.Trim();
        if (!Directory.Exists(resources)) Show(ResourcesStatus, false, "✖ Esa carpeta no existe.");
        else
        {
            List<string> missing = AppSettings.MissingResources(resources);
            resourcesOk = missing.Count == 0;
            Show(ResourcesStatus, resourcesOk, resourcesOk ? "✔ Están todos los archivos base." : "✖ Faltan: " + string.Join(", ", missing));
        }

        SaveButton.IsEnabled = rootOk && resourcesOk;
        return rootOk && resourcesOk;
    }

    private void Show(TextBlock block, bool ok, string text)
    {
        block.Text = text;
        block.Foreground = (Brush)FindResource(ok ? "Ok" : "Danger");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        _settings.RootFolder = RootBox.Text.Trim();
        _settings.ResourcesFolder = ResourcesBox.Text.Trim();
        _settings.Save();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
