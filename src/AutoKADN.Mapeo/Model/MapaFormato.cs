using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoKADN.Mapeo.Model;

// Mapa de un formato PDF: dónde está cada celda que se rellena y cómo se llama.
// Todas las medidas van en puntos PDF (1 pt = 1/72 pulgada) con el origen en la esquina SUPERIOR izquierda
// de la página y Y creciendo hacia abajo (como en pantalla). Quien rellene el PDF convierte con
// yPdf = alto de página - y - alto.
public sealed class MapaFormato
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("codigo")] public string Codigo { get; set; } = "";
    [JsonPropertyName("nombre")] public string Nombre { get; set; } = "";
    [JsonPropertyName("archivoPdf")] public string ArchivoPdf { get; set; } = "";
    [JsonPropertyName("generado")] public string Generado { get; set; } = "";
    // Avisos del detector (por ejemplo, que el texto se leyó con OCR y puede traer errores).
    [JsonPropertyName("notas")] public List<string> Notas { get; set; } = new();
    // Si el formato se imprime en varias hojas iguales con datos distintos (ej. FT-T-127), la clave de la lista
    // de resumen_obra.json que tiene un elemento por hoja. Cada hoja resuelve sus claves primero en su elemento.
    [JsonPropertyName("paginaPorLista")] public string? PaginaPorLista { get; set; }
    [JsonPropertyName("paginas")] public List<PaginaMapa> Paginas { get; set; } = new();
    [JsonPropertyName("campos")] public List<CampoMapa> Campos { get; set; } = new();
    [JsonPropertyName("tablas")] public List<TablaMapa> Tablas { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // acentos legibles
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static MapaFormato FromJson(string json) =>
        JsonSerializer.Deserialize<MapaFormato>(json, Options) ?? new MapaFormato();
}

public sealed class PaginaMapa
{
    [JsonPropertyName("numero")] public int Numero { get; set; }
    [JsonPropertyName("ancho")] public double Ancho { get; set; }
    [JsonPropertyName("alto")] public double Alto { get; set; }
}

public static class TipoCampo
{
    public const string Texto = "texto";
    public const string Numero = "numero";
    public const string Fecha = "fecha";
    public const string Check = "check";
    public const string Multilinea = "multilinea";

    public static readonly string[] Todos = { Texto, Numero, Fecha, Check, Multilinea };
}

public static class Alineacion
{
    public const string Izquierda = "izquierda";
    public const string Centro = "centro";
    public const string Derecha = "derecha";

    public static readonly string[] Todas = { Izquierda, Centro, Derecha };
}

// Un rectángulo suelto: un campo del encabezado, una casilla, una raya para firmar, un cuadro de observaciones...
public sealed class CampoMapa
{
    // Nombre corto y estable con el que el flujo se va a referir a la celda (ej. "orden_de_trabajo").
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    // Cómo se lee en el formato (ej. "NÚMERO DE ANILLOS › Ø ½\" › CANTIDAD"): ayuda para revisar.
    [JsonPropertyName("etiqueta")] public string Etiqueta { get; set; } = "";
    // Para los espacios dentro de un párrafo: el texto que los rodea.
    [JsonPropertyName("contexto")] public string? Contexto { get; set; }
    [JsonPropertyName("tipo")] public string Tipo { get; set; } = TipoCampo.Texto;
    [JsonPropertyName("pagina")] public int Pagina { get; set; } = 1;
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("ancho")] public double Ancho { get; set; }
    [JsonPropertyName("alto")] public double Alto { get; set; }
    [JsonPropertyName("alineacion")] public string Alineacion { get; set; } = Model.Alineacion.Izquierda;
    [JsonPropertyName("tamanoFuente")] public double TamanoFuente { get; set; } = 9;
    // Texto ya impreso dentro de la celda antes del valor (ej. "Ø" en las cabeceras de diámetro).
    [JsonPropertyName("prefijo")] public string? Prefijo { get; set; }
    // Ancho en puntos que ocupa el texto previo; vacío = se calcula con la letra de la celda.
    [JsonPropertyName("prefijoAncho")] public double? PrefijoAncho { get; set; }
    // Giro del texto en grados contra el reloj (90 = se lee de abajo hacia arriba, como las celdas altas del FT-O-108).
    [JsonPropertyName("rotacion")] public double? Rotacion { get; set; }
    // Clave del flujo (datos_proyecto.json u otra) que rellena esta celda. Vacío = sin conectar todavía.
    [JsonPropertyName("fuente")] public string Fuente { get; set; } = "";
    // Para campos de varias líneas: cada raya o renglón donde se escribe, de arriba abajo.
    [JsonPropertyName("lineas")] public List<RectMapa>? Lineas { get; set; }
    // "auto" = lo propuso el detector, "manual" = lo creó o lo cambió una persona.
    [JsonPropertyName("origen")] public string Origen { get; set; } = "auto";
    [JsonPropertyName("confianza")] public double Confianza { get; set; } = 0.5;
    [JsonPropertyName("revisado")] public bool Revisado { get; set; }

    [JsonIgnore] public double Derecha => X + Ancho;
    [JsonIgnore] public double Abajo => Y + Alto;
}

public sealed class RectMapa
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("ancho")] public double Ancho { get; set; }
    [JsonPropertyName("alto")] public double Alto { get; set; }
}

// Tabla con filas que se van llenando una por una (registro de excavación, de tubería...).
public sealed class TablaMapa
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("etiqueta")] public string Etiqueta { get; set; } = "";
    [JsonPropertyName("pagina")] public int Pagina { get; set; } = 1;
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("ancho")] public double Ancho { get; set; }
    [JsonPropertyName("alto")] public double Alto { get; set; }
    [JsonPropertyName("columnas")] public List<ColumnaMapa> Columnas { get; set; } = new();
    // Una entrada por renglón disponible, de arriba abajo.
    [JsonPropertyName("filas")] public List<FilaMapa> Filas { get; set; } = new();
    [JsonPropertyName("origen")] public string Origen { get; set; } = "auto";
    [JsonPropertyName("revisado")] public bool Revisado { get; set; }
}

public sealed class ColumnaMapa
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("etiqueta")] public string Etiqueta { get; set; } = "";
    [JsonPropertyName("tipo")] public string Tipo { get; set; } = TipoCampo.Texto;
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("ancho")] public double Ancho { get; set; }
    [JsonPropertyName("alineacion")] public string Alineacion { get; set; } = Model.Alineacion.Centro;
    [JsonPropertyName("tamanoFuente")] public double TamanoFuente { get; set; } = 8;
    // Texto ya impreso en cada celda de la columna antes del valor (ej. "Ø" en una columna de diámetros).
    [JsonPropertyName("prefijo")] public string? Prefijo { get; set; }
    [JsonPropertyName("prefijoAncho")] public double? PrefijoAncho { get; set; }
    [JsonPropertyName("fuente")] public string Fuente { get; set; } = "";
}

public sealed class FilaMapa
{
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("alto")] public double Alto { get; set; }
}
