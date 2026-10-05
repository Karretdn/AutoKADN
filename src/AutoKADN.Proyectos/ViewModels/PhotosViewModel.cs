using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using AutoKADN.Proyectos.Photos;
using AutoKADN.Proyectos.Services;

namespace AutoKADN.Proyectos.ViewModels;

public sealed class PhotoItem : ObservableObject
{
    private int _number;
    private BitmapSource? _thumbnail;

    public PhotoItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string Name { get; }
    public int Number { get => _number; set => Set(ref _number, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
}

public sealed class PhotosViewModel : ObservableObject
{
    public const string OutputFileName = ResourceNames.Fotos;

    private readonly AppSettings _settings;
    private string _folder = string.Empty;
    private bool _isBusy;
    private string _progress = string.Empty;
    private int _loadVersion;

    public PhotosViewModel(AppSettings settings)
    {
        _settings = settings;
        Items.CollectionChanged += (_, _) => { Renumber(); RaiseSummary(); };
    }

    public ObservableCollection<PhotoItem> Items { get; } = new();

    public string Folder { get => _folder; private set { if (Set(ref _folder, value)) { OnPropertyChanged(nameof(HasFolder)); OnPropertyChanged(nameof(OutputPath)); } } }
    public bool HasFolder => _folder.Length > 0;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(CanGenerate)); OnPropertyChanged(nameof(IsIdle)); } } }
    public bool IsIdle => !_isBusy;
    public string Progress { get => _progress; private set => Set(ref _progress, value); }

    public string OutputPath => _folder.Length == 0 ? string.Empty : Path.Combine(_folder, OutputFileName);
    public bool HasPhotos => Items.Count > 0;
    public bool CanGenerate => Items.Count > 0 && !_isBusy && Directory.Exists(_folder);

    /// <summary>"12 fotos → 2 páginas (9 + 3)".</summary>
    public string Summary
    {
        get
        {
            if (Items.Count == 0) return HasFolder ? "No hay fotos en esta carpeta." : "Elige la carpeta con las fotos.";
            int[] plan = PhotoLayouts.PlanPages(Items.Count);
            string photos = Items.Count == 1 ? "1 foto" : $"{Items.Count} fotos";
            return plan.Length == 1 ? $"{photos} → 1 página" : $"{photos} → {plan.Length} páginas ({string.Join(" + ", plan)})";
        }
    }

    public string FolderLabel => _folder.Length == 0 ? "Sin carpeta" : _folder;

    /// <summary>Carga las fotos de la carpeta, por nombre; las miniaturas llegan después sin bloquear la ventana.</summary>
    public void LoadFolder(string folder)
    {
        folder = Path.GetFullPath(folder); // el cuadro de carpetas de Windows no acepta rutas con "/"
        Folder = folder;
        OnPropertyChanged(nameof(FolderLabel));
        Items.Clear();
        int version = ++_loadVersion;
        foreach (string path in PhotoLoader.ListPhotos(folder)) Items.Add(new PhotoItem(path));

        List<PhotoItem> snapshot = Items.ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (PhotoItem item in snapshot)
            {
                if (version != _loadVersion) return;
                BitmapSource? thumbnail = null;
                try { thumbnail = PhotoLoader.Thumbnail(item.Path); } catch { /* foto ilegible: queda sin miniatura */ }
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => item.Thumbnail = thumbnail));
            }
        });
        RaiseSummary();
    }

    public void MoveBefore(PhotoItem item)
    {
        int index = Items.IndexOf(item);
        if (index > 0) Items.Move(index, index - 1);
    }

    public void MoveAfter(PhotoItem item)
    {
        int index = Items.IndexOf(item);
        if (index >= 0 && index < Items.Count - 1) Items.Move(index, index + 1);
    }

    public void Remove(PhotoItem item) => Items.Remove(item);

    /// <summary>El Excel ya existente se puede reemplazar sin preguntar solo si es la plantilla en blanco.</summary>
    public bool OutputIsBlankTemplate()
    {
        string template = Path.Combine(_settings.ResourcesFolder, ResourceNames.Fotos);
        if (!File.Exists(OutputPath)) return true;
        if (!File.Exists(template)) return false;
        FileInfo existing = new(OutputPath), blank = new(template);
        return existing.Length == blank.Length && File.ReadAllBytes(OutputPath).AsSpan().SequenceEqual(File.ReadAllBytes(template));
    }

    /// <summary>Prepara las fotos (giro, reducción, JPEG) y arma el Excel. Se llama desde un hilo de fondo.</summary>
    public PhotoBookResult Generate(Action<string> report)
    {
        string template = Path.Combine(_settings.ResourcesFolder, ResourceNames.Fotos);
        if (!File.Exists(template)) throw new FileNotFoundException("Falta la plantilla FORMATO FOTOS.xlsx en la carpeta de recursos.", template);

        List<string> paths = Items.Select(i => i.Path).ToList();
        int[] plan = PhotoLayouts.PlanPages(paths.Count);
        var prepared = new List<PreparedPhoto>(paths.Count);
        int done = 0;
        foreach (int onPage in plan)
        {
            int side = PhotoLayouts.SuggestedMaxSide(onPage);
            for (int i = 0; i < onPage; i++)
            {
                report($"Preparando foto {done + 1} de {paths.Count}…");
                prepared.Add(PhotoLoader.Prepare(paths[done++], side));
            }
        }

        report("Armando el Excel…");
        return PhotoBookWriter.Write(template, OutputPath, prepared);
    }

    public void SetBusy(bool busy, string progress = "")
    {
        IsBusy = busy;
        Progress = progress;
    }

    public void ReportProgress(string text) => Progress = text;

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++) Items[i].Number = i + 1;
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasPhotos));
        OnPropertyChanged(nameof(CanGenerate));
    }
}
