using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoKADN.Proyectos.Services.Formatos;

/// <summary>
/// Los datos con los que se rellenan los formatos. Salen de dos archivos de la carpeta PLANOS del proyecto:
/// <c>datos_proyecto.json</c> (lo escribe esta app) y <c>resumen_obra.json</c> (lo exportará AutoKADN con las
/// cantidades del dibujo). Ambos son objetos planos: texto/número → valor; arreglo de objetos → lista
/// (una fila por elemento, ej. los accesorios por diámetro).
/// </summary>
public sealed class FormatSource
{
    public const string SummaryFileName = "resumen_obra.json";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Dictionary<string, string>>> _lists = new(StringComparer.OrdinalIgnoreCase);

    public bool HasProjectData { get; private set; }
    public bool HasSummary { get; private set; }
    /// <summary>Archivo de resumen que se leyó (raíz del proyecto o PLANOS); null si no hay.</summary>
    public string? SummaryPath { get; private set; }
    /// <summary>Cuándo se generó ese resumen (campo "generado"), si lo trae.</summary>
    public DateTime? SummaryGenerated { get; private set; }

    public static FormatSource Load(string projectFolder)
    {
        var source = new FormatSource();
        // Carpeta raíz del proyecto; si el archivo no está ahí, PLANOS (proyectos creados antes de este cambio).
        source.HasProjectData = source.ReadFile(LocateFile(projectFolder, ProjectDataFile.FileName) ?? "");
        source.SummaryPath = LocateFile(projectFolder, SummaryFileName);
        source.HasSummary = source.ReadFile(source.SummaryPath ?? "");
        if (source.HasSummary && DateTime.TryParse(source.Value("generado"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime generated))
            source.SummaryGenerated = generated;
        if (!source.HasProjectData) source.FillFromFolderName(projectFolder);
        return source;
    }

    /// <summary>Ruta del archivo en la carpeta raíz del proyecto o, si no está, en PLANOS; null si no existe en ninguna.</summary>
    public static string? LocateFile(string projectFolder, string fileName)
    {
        string inRoot = Path.Combine(projectFolder, fileName);
        if (File.Exists(inRoot)) return inRoot;
        string inPlanos = Path.Combine(projectFolder, "PLANOS", fileName);
        return File.Exists(inPlanos) ? inPlanos : null;
    }

    /// <summary>Verdadero si los datos del proyecto se dedujeron del nombre de la carpeta (no hay datos_proyecto.json).</summary>
    public bool DerivedFromFolder { get; private set; }

    // Proyectos creados antes de datos_proyecto.json: la orden y el proyecto salen del nombre de la carpeta
    // ("418491323 - V POLONUEVO KM 1 - 844 (SANTO TOMAS)") y el interventor de la carpeta superior.
    private void FillFromFolderName(string projectFolder)
    {
        string trimmed = projectFolder.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+)\s*-\s*(.+)$");
        if (match.Success)
        {
            _values["orden"] = match.Groups[1].Value;
            _values["idProyecto"] = match.Groups[1].Value;
            _values["proyecto"] = match.Groups[2].Value.Trim();
        }
        else if (name.Length > 0) _values["proyecto"] = name;

        string? parent = Path.GetFileName(Path.GetDirectoryName(trimmed)?.TrimEnd('\\', '/'));
        if (!string.IsNullOrWhiteSpace(parent)) _values["interventor"] = parent;
        DerivedFromFolder = true;
    }

    private bool ReadFile(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            ReadObject(document.RootElement);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // Vuelca un objeto JSON en valores y listas; los arreglos de objetos también se guardan crudos para poder
    // abrir cada elemento como una hoja con su propio ámbito.
    private void ReadObject(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String: _values[property.Name] = property.Value.GetString() ?? ""; break;
                case JsonValueKind.Number: _values[property.Name] = property.Value.GetRawText(); break;
                case JsonValueKind.True: _values[property.Name] = "true"; break;
                case JsonValueKind.False: _values[property.Name] = "false"; break;
                case JsonValueKind.Array:
                    _lists[property.Name] = ReadList(property.Value);
                    _rawLists[property.Name] = property.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList();
                    break;
            }
        }
    }

    private readonly Dictionary<string, List<JsonElement>> _rawLists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cuántas hojas tiene el formato: los elementos de la lista indicada (mínimo 1).</summary>
    public int PageCount(string? listKey) =>
        listKey is not null && _rawLists.TryGetValue(listKey, out var pages) && pages.Count > 0 ? pages.Count : 1;

    /// <summary>La fuente de datos de una hoja: los valores y listas del elemento tienen prioridad sobre los generales.</summary>
    public FormatSource Page(string? listKey, int index)
    {
        if (listKey is null || !_rawLists.TryGetValue(listKey, out var pages) || index >= pages.Count) return this;
        var child = new FormatSource { HasProjectData = HasProjectData, HasSummary = HasSummary, DerivedFromFolder = DerivedFromFolder };
        foreach (var pair in _values) child._values[pair.Key] = pair.Value;
        foreach (var pair in _lists) child._lists[pair.Key] = pair.Value;
        child.ReadObject(pages[index]);
        return child;
    }

    private static List<Dictionary<string, string>> ReadList(JsonElement array)
    {
        var rows = new List<Dictionary<string, string>>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty p in item.EnumerateObject())
                row[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            rows.Add(row);
        }
        return rows;
    }

    public string? Value(string key) => _values.TryGetValue(key, out string? value) ? value : null;

    /// <summary>true si el resumen trae una lista (arreglo de objetos) con esa clave.</summary>
    public bool HasList(string key) => _rawLists.ContainsKey(key);

    // ---------- valor de una "fuente" del mapa ----------

    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}");

    /// <summary>
    /// Resuelve lo que se escribe en «Conectar con»: una clave (<c>orden</c>), una clave con formatos
    /// (<c>fechaGasificado|dia</c>, <c>interventor|sincodigo</c>, <c>fechaGasificado|mes|titulo</c>), un elemento de
    /// lista (<c>polivalvulas[0].cantidad</c>) o un texto con claves entre llaves precedido de «=»
    /// (<c>=ID-{idProyecto}</c>; si el texto no lleva claves es un valor fijo, ej. <c>=O.D.P.</c>).
    /// Devuelve null si falta algún dato.
    /// </summary>
    public string? Resolve(string? fuente)
    {
        if (string.IsNullOrWhiteSpace(fuente)) return null;
        string expression = fuente.Trim();
        return expression[0] == '=' ? Expand(expression[1..]) : ResolveKey(expression);
    }

    private string? ResolveKey(string expression)
    {
        string[] parts = expression.Split('|');
        string key = parts[0].Trim();
        string? value = Value(key) ?? IndexedValue(key);
        if (value is null) return null;
        for (int i = 1; i < parts.Length; i++) value = ApplyFormat(value, parts[i].Trim());
        return value;
    }

    private string? Expand(string template)
    {
        bool missing = false;
        string result = Placeholder.Replace(template, m =>
        {
            string? value = ResolveKey(m.Groups[1].Value.Trim());
            if (string.IsNullOrWhiteSpace(value)) { missing = true; return ""; }
            return value;
        });
        return missing ? null : result.Trim();
    }

    // Formatos con nombre en español; para fechas también sirve cualquier formato de .NET (ej. "MMMM").
    private static string ApplyFormat(string value, string format)
    {
        CultureInfo culture = DateParser.Culture;
        switch (format.ToLowerInvariant())
        {
            case "mayus": return value.ToUpper(culture);
            case "minus": return value.ToLower(culture);
            case "titulo": return culture.TextInfo.ToTitleCase(value.ToLower(culture));
            case "sincodigo": return Regex.Replace(value, @"\s*\(\s*\d+\s*\)\s*$", "").Trim();
            case "codigo":
                Match code = Regex.Match(value, @"\(\s*(\d+)\s*\)\s*$");
                return code.Success ? code.Groups[1].Value : "";
            case "marca":   // "X" si el valor es un número mayor que cero (ej. hay anillos, hay troncal); si no, vacío
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number > 0.0 ? "X" : "";
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)) return value;
        switch (format.ToLowerInvariant())
        {
            case "dia": return date.Day.ToString(CultureInfo.InvariantCulture);
            case "dia2": return date.ToString("dd", CultureInfo.InvariantCulture);
            case "mes": return culture.DateTimeFormat.GetMonthName(date.Month);
            case "mes2": return date.ToString("MM", CultureInfo.InvariantCulture);
            case "anio": return date.Year.ToString(CultureInfo.InvariantCulture);
            case "anio2": return date.ToString("yy", CultureInfo.InvariantCulture);
            default: return date.ToString(format.Length == 1 ? "%" + format : format, culture);
        }
    }

    public IReadOnlyList<Dictionary<string, string>>? List(string key) => _lists.TryGetValue(key, out var list) ? list : null;

    /// <summary>"accesorios.union.proyecto[].cantidad" → ("accesorios.union.proyecto", "cantidad").</summary>
    public static bool TryParseListRef(string fuente, out string listKey, out string field)
    {
        listKey = field = "";
        int index = fuente.IndexOf("[].", StringComparison.Ordinal);
        if (index <= 0 || index + 3 >= fuente.Length) return false;
        listKey = fuente[..index];
        field = fuente[(index + 3)..];
        return true;
    }

    /// <summary>"polivalvulas[0].cantidad" → el campo del elemento 0 de esa lista; null si no existe.</summary>
    public string? IndexedValue(string fuente)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(fuente, @"^(.+)\[(\d+)\]\.(.+)$");
        if (!m.Success) return null;
        IReadOnlyList<Dictionary<string, string>>? items = List(m.Groups[1].Value);
        int index = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (items is null || index >= items.Count) return null;
        return items[index].TryGetValue(m.Groups[3].Value, out string? value) ? value : null;
    }

    /// <summary>Da el formato final del valor según el tipo de celda (las fechas ISO pasan a dd/MM/aaaa).</summary>
    public static string Format(string raw, string tipo)
    {
        string text = raw.Trim();
        if (tipo == "fecha" && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return text;
    }
}
