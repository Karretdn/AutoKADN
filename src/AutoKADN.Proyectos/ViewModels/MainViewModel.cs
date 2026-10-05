using System.Collections.ObjectModel;
using System.IO;
using AutoKADN.Proyectos.Services;

namespace AutoKADN.Proyectos.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private RoleCatalog _roles = RoleCatalog.Empty;
    private bool _chaining;

    private string _pdfPath = string.Empty;
    private string _pdfWarning = string.Empty;
    private string _orden = string.Empty;
    private string _idProyecto = string.Empty;
    private string _proyecto = string.Empty;
    private string _localidad = string.Empty;
    private string _departamento = string.Empty;
    private InterventorItem? _interventor;
    private string _manualInterventor = string.Empty;
    private string? _supervisor;
    private string _tuberia;
    private bool _is34;
    private bool _includeLibro;
    private string _environmentWarning = string.Empty;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _tuberia = string.Equals(settings.Tuberia, TuberiaPavco, StringComparison.OrdinalIgnoreCase) ? TuberiaPavco : TuberiaExtrucol;
        _is34 = settings.Diameter != "1/2";
        _includeLibro = settings.IncludeLibro;

        DateRows = new ObservableCollection<DateRow>
        {
            new("InicioObra", "Inicio de obra", "Obra"),
            new("FinalObra", "Final de obra", null, "igual que el inicio"),
            new("PruebaInicial", "Prueba inicial", "Plano: pruebas y gasificado", "igual que el final de obra"),
            new("PruebaFinal", "Prueba final", null, "igual que la prueba inicial"),
            new("Gasificado", "Gasificado", null, "igual que la prueba final"),
        };
        foreach (DateRow row in DateRows) row.Changed += OnDateChanged;

        ReloadEnvironment();
    }

    public ObservableCollection<DateRow> DateRows { get; }
    public ObservableCollection<InterventorItem> Interventores { get; } = new();
    public ObservableCollection<string> Supervisores { get; } = new();

    // ── Orden de trabajo ────────────────────────────────────────────────────────────────────────
    public string PdfPath { get => _pdfPath; private set { if (Set(ref _pdfPath, value)) { OnPropertyChanged(nameof(HasPdf)); OnPropertyChanged(nameof(PdfTitle)); OnPropertyChanged(nameof(PdfHint)); } } }
    public bool HasPdf => !string.IsNullOrEmpty(_pdfPath);
    public string PdfTitle => HasPdf ? Path.GetFileName(_pdfPath) : "Elegir la orden de trabajo (PDF)";
    public string PdfHint => HasPdf ? "Clic o arrastra otro PDF para reemplazarla" : "Arrastra el PDF aquí o haz clic para elegirlo";
    public string PdfWarning { get => _pdfWarning; private set { if (Set(ref _pdfWarning, value)) OnPropertyChanged(nameof(HasPdfWarning)); } }
    public bool HasPdfWarning => !string.IsNullOrEmpty(_pdfWarning);

    public string Orden { get => _orden; set { if (Set(ref _orden, value.Trim())) Refresh(); } }
    public string IdProyecto { get => _idProyecto; set { if (Set(ref _idProyecto, value.Trim())) Refresh(); } }
    public string Proyecto { get => _proyecto; set { if (Set(ref _proyecto, value.Trim())) Refresh(); } }
    public string Localidad { get => _localidad; set { if (Set(ref _localidad, value.Trim())) Refresh(); } }
    public string Departamento { get => _departamento; set { if (Set(ref _departamento, value.Trim())) Refresh(); } }

    // ── Responsables ───────────────────────────────────────────────────────────────────────────
    public InterventorItem? Interventor
    {
        get => _interventor;
        set
        {
            if (!Set(ref _interventor, value)) return;
            OnPropertyChanged(nameof(NeedsManualInterventor));
            OnPropertyChanged(nameof(InterventorBadge));
            OnPropertyChanged(nameof(InterventorBadgeIsWarning));
            Refresh();
        }
    }

    /// <summary>La carpeta no está en roles.xlsx: hay que escribir el nombre completo con código.</summary>
    public bool NeedsManualInterventor => _interventor is not null && _interventor.FullName is null;
    public string ManualInterventor { get => _manualInterventor; set { if (Set(ref _manualInterventor, value.Trim())) Refresh(); } }
    public string InterventorBadge => _interventor is null ? string.Empty : _interventor.FullName ?? "No está en roles.xlsx: escribe su nombre completo con código";
    public bool InterventorBadgeIsWarning => _interventor is not null && _interventor.FullName is null;

    /// <summary>Texto que va al Excel y al plano (siempre con código).</summary>
    public string InterventorFullName => _interventor?.FullName ?? _manualInterventor;

    public string? Supervisor { get => _supervisor; set { if (Set(ref _supervisor, value)) Refresh(); } }

    // ── Plano ───────────────────────────────────────────────────────────────────────────────────
    public bool Is34 { get => _is34; set { if (Set(ref _is34, value)) { OnPropertyChanged(nameof(Is12)); Refresh(); } } }
    public bool Is12 { get => !_is34; set => Is34 = !value; }
    public string Diameter => _is34 ? "3/4" : "1/2";
    /// <summary>Marca de la tubería: EXTRUCOL o PAVCO.</summary>
    public string Tuberia => _tuberia;
    public bool IsExtrucol { get => _tuberia == TuberiaExtrucol; set { if (value) SetTuberia(TuberiaExtrucol); } }
    public bool IsPavco { get => _tuberia == TuberiaPavco; set { if (value) SetTuberia(TuberiaPavco); } }

    private void SetTuberia(string value)
    {
        if (!Set(ref _tuberia, value, nameof(Tuberia))) return;
        OnPropertyChanged(nameof(IsExtrucol));
        OnPropertyChanged(nameof(IsPavco));
        Refresh();
    }

    public const string TuberiaExtrucol = "EXTRUCOL";
    public const string TuberiaPavco = "PAVCO";
    public bool IncludeLibro { get => _includeLibro; set { if (Set(ref _includeLibro, value)) Refresh(); } }

    // ── Resultado ───────────────────────────────────────────────────────────────────────────────
    public string EnvironmentWarning { get => _environmentWarning; private set { if (Set(ref _environmentWarning, value)) OnPropertyChanged(nameof(HasEnvironmentWarning)); } }
    public bool HasEnvironmentWarning => !string.IsNullOrEmpty(_environmentWarning);

    public string FolderName => string.IsNullOrWhiteSpace(Orden) || string.IsNullOrWhiteSpace(Proyecto)
        ? string.Empty
        : FolderNames.Sanitize($"{Orden} - {Proyecto}");

    public string DestinationPath => _interventor is null || FolderName.Length == 0 || string.IsNullOrWhiteSpace(_settings.RootFolder)
        ? string.Empty
        : Path.Combine(_settings.RootFolder, _interventor.FolderName, FolderName);

    public bool FolderExists => DestinationPath.Length > 0 && Directory.Exists(DestinationPath);

    public string Missing
    {
        get
        {
            var missing = new List<string>();
            if (!HasPdf && string.IsNullOrWhiteSpace(Orden)) missing.Add("la orden en PDF");
            else
            {
                if (string.IsNullOrWhiteSpace(Orden)) missing.Add("N.º de orden");
                if (string.IsNullOrWhiteSpace(Proyecto)) missing.Add("proyecto");
                if (string.IsNullOrWhiteSpace(Localidad)) missing.Add("localidad");
                if (string.IsNullOrWhiteSpace(Departamento)) missing.Add("departamento");
            }
            if (_interventor is null) missing.Add("interventor");
            else if (string.IsNullOrWhiteSpace(InterventorFullName)) missing.Add("nombre completo del interventor");
            if (string.IsNullOrWhiteSpace(Supervisor)) missing.Add("supervisor");
            foreach (DateRow row in DateRows) if (row.Value is null) missing.Add(row.Label.ToLowerInvariant());
            return missing.Count == 0 ? string.Empty : "Falta: " + string.Join(", ", missing);
        }
    }

    public bool CanCreate => Missing.Length == 0 && !FolderExists && _settings.IsConfigured;

    // ── Acciones ────────────────────────────────────────────────────────────────────────────────
    public void LoadPdf(string path)
    {
        try
        {
            OrderInfo info = OrderPdfReader.Read(path);
            PdfPath = path;
            Orden = info.Orden;
            IdProyecto = info.IdProyecto;
            Proyecto = info.Proyecto;
            Localidad = info.Localidad;
            Departamento = info.Departamento;
            PdfWarning = string.Join("  ·  ", info.Warnings);
        }
        catch (Exception ex)
        {
            PdfWarning = ex.Message;
        }
        Refresh();
    }

    /// <summary>Vuelve a leer roles.xlsx y las carpetas de interventor (al iniciar y al cambiar las rutas).</summary>
    public void ReloadEnvironment()
    {
        var problems = new List<string>();

        _roles = RoleCatalog.Empty;
        string rolesPath = Path.Combine(_settings.ResourcesFolder ?? string.Empty, ResourceNames.Roles);
        if (File.Exists(rolesPath))
        {
            try { _roles = RoleCatalog.Load(rolesPath); }
            catch (Exception ex) { problems.Add("No se pudo leer roles.xlsx: " + ex.Message); }
        }
        else problems.Add("Falta roles.xlsx en la carpeta de recursos.");

        string previousInterventor = _interventor?.FolderName ?? _settings.LastInterventor;
        string? previousSupervisor = _supervisor ?? _settings.LastSupervisor;

        Interventores.Clear();
        if (Directory.Exists(_settings.RootFolder))
        {
            foreach (string dir in Directory.GetDirectories(_settings.RootFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(dir) & FileAttributes.Hidden) != 0) continue;
                string name = Path.GetFileName(dir);
                Interventores.Add(new InterventorItem(name, _roles.FindInterventor(name)));
            }
        }
        else problems.Add("Configura la carpeta raíz de proyectos (botón Rutas).");

        Supervisores.Clear();
        foreach (string supervisor in _roles.Supervisores) Supervisores.Add(supervisor);

        Interventor = Interventores.FirstOrDefault(i => string.Equals(i.FolderName, previousInterventor, StringComparison.OrdinalIgnoreCase));
        Supervisor = Supervisores.FirstOrDefault(s => string.Equals(s, previousSupervisor, StringComparison.OrdinalIgnoreCase));
        EnvironmentWarning = string.Join("  ", problems);
        Refresh();
    }

    /// <summary>Los datos del formulario listos para crear el proyecto (solo válido si CanCreate).</summary>
    public ProjectRequest BuildRequest() => new(
        RootFolder: _settings.RootFolder,
        ResourcesFolder: _settings.ResourcesFolder,
        InterventorFolder: _interventor!.FolderName,
        InterventorFullName: InterventorFullName,
        Supervisor: Supervisor!,
        FolderName: FolderName,
        Orden: Orden,
        IdProyecto: IdProyecto,
        Proyecto: Proyecto,
        Localidad: Localidad,
        Departamento: Departamento,
        InicioObra: DateRows[0].Value!.Value,
        FinalObra: DateRows[1].Value!.Value,
        PruebaInicial: DateRows[2].Value!.Value,
        PruebaFinal: DateRows[3].Value!.Value,
        Gasificado: DateRows[4].Value!.Value,
        Diametro: Diameter,
        Tuberia: Tuberia,
        IncludeLibro: IncludeLibro,
        OrderPdfPath: PdfPath);

    /// <summary>Deja el formulario listo para el siguiente proyecto (se conservan interventor, supervisor, diámetro y tubería).</summary>
    public void ResetForNewProject()
    {
        PdfPath = string.Empty;
        PdfWarning = string.Empty;
        Orden = IdProyecto = Proyecto = Localidad = Departamento = string.Empty;
        ManualInterventor = string.Empty;
        _chaining = true;
        try
        {
            foreach (DateRow row in DateRows) { row.IsManual = false; row.Value = null; }
        }
        finally { _chaining = false; }
        Refresh();
    }

    /// <summary>Guarda en los ajustes lo que conviene recordar para la próxima vez.</summary>
    public void RememberChoices()
    {
        _settings.LastInterventor = _interventor?.FolderName ?? string.Empty;
        _settings.LastSupervisor = _supervisor ?? string.Empty;
        _settings.Diameter = Diameter;
        _settings.Tuberia = Tuberia;
        _settings.IncludeLibro = IncludeLibro;
        _settings.Save();
    }

    // ── Fechas encadenadas ──────────────────────────────────────────────────────────────────────
    private void OnDateChanged(DateRow changed)
    {
        if (!_chaining)
        {
            _chaining = true;
            try
            {
                int index = DateRows.IndexOf(changed);
                // Una fecha automática que se borra vuelve a copiar la anterior.
                if (!changed.IsManual && changed.Value is null && index > 0) changed.Value = DateRows[index - 1].Value;

                for (int i = index + 1; i < DateRows.Count && !DateRows[i].IsManual; i++)
                    DateRows[i].Value = DateRows[i - 1].Value;
            }
            finally { _chaining = false; }
        }
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(DestinationPath));
        OnPropertyChanged(nameof(FolderExists));
        OnPropertyChanged(nameof(Missing));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(InterventorFullName));
        OnPropertyChanged(nameof(Diameter));
    }

    public bool HasMissing => Missing.Length > 0;
}
