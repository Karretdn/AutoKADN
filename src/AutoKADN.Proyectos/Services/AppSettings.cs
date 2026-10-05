using System.IO;
using System.Text.Json;

namespace AutoKADN.Proyectos.Services;

/// <summary>Nombres de los archivos que la app necesita en la carpeta de recursos.</summary>
public static class ResourceNames
{
    public const string Roles = "roles.xlsx";
    public const string Soportes = "FORMATO LEGALIZACION OBRA CIVIL_FT-09-PD-O-02 (V-2).xlsx";
    public const string Fotos = "FORMATO FOTOS.xlsx";
    public const string Libro = "LIBRO DE CANTIDADES.xlsx";
    public const string Plano = "Formato_para_planos.dwg";

    public static readonly string[] All = { Roles, Soportes, Fotos, Libro, Plano };
}

/// <summary>Ajustes persistidos en %AppData%\AutoKADN.Proyectos\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string RootFolder { get; set; } = string.Empty;
    public string ResourcesFolder { get; set; } = string.Empty;
    public string LastInterventor { get; set; } = string.Empty;
    public string LastSupervisor { get; set; } = string.Empty;
    public string Tuberia { get; set; } = "EXTRUCOL";
    public string Diameter { get; set; } = "3/4";
    public bool IncludeLibro { get; set; }

    /// <summary>
    /// Archivo de ajustes. La variable de entorno AUTOKADN_SETTINGS lo cambia: así las pruebas y las capturas
    /// de desarrollo nunca escriben sobre los ajustes reales de la persona.
    /// </summary>
    public static string FilePath
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("AUTOKADN_SETTINGS");
            return !string.IsNullOrWhiteSpace(custom)
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoKADN.Proyectos", "settings.json");
        }
    }

    public bool IsConfigured => Directory.Exists(RootFolder) && Directory.Exists(ResourcesFolder);

    /// <summary>Carpeta Recursos que viaja junto al ejecutable (plantillas y roles.xlsx).</summary>
    public static string BundledResourcesFolder => Path.Combine(AppContext.BaseDirectory, "Recursos");

    public static AppSettings Load()
    {
        AppSettings settings = new();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch { /* archivo dañado: se empieza con ajustes vacíos */ }

        // Primera vez: los archivos base de la app son los de su propia carpeta Recursos.
        if (string.IsNullOrWhiteSpace(settings.ResourcesFolder) && Directory.Exists(BundledResourcesFolder))
            settings.ResourcesFolder = BundledResourcesFolder;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Archivos de recursos que faltan en la carpeta indicada.</summary>
    public static List<string> MissingResources(string resourcesFolder)
    {
        var missing = new List<string>();
        foreach (string name in ResourceNames.All)
            if (!File.Exists(Path.Combine(resourcesFolder, name))) missing.Add(name);
        return missing;
    }
}
