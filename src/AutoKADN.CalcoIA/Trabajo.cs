using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;

namespace AutoKADN.CalcoIA;

// Parámetros del calco (kit\parametros.json, junto al ejecutable). Unidades del dibujo de la plantilla.
public sealed class Parametros
{
    public Marco Marco { get; set; } = new();
    public double Margen { get; set; } = 3.0;
    public double Separacion { get; set; } = 8.5;
    public double OffsetCota { get; set; } = 5.5;
    public double EstirarMaximo { get; set; } = 1.0;   // 1 = sin estirar (por ahora se prefiere así)
    // Un lado de una manzana vecina a menos de esta fracción del tamaño de la manzana del anillo de su borde se alinea con él.
    public double SimetriaVecinos { get; set; } = 0.12;
    public double ViaMinima { get; set; } = 18.0;   // ancho final de una vía entre manzanas (unidades del dibujo)
    public double ViaMaxima { get; set; } = 35.0;
    public double RadioIndicador { get; set; } = 6.746;
    // Cruce de vía: cada tramito de 0.1 (con su reducción) se dibuja de una fracción del cruce, con un mínimo para que
    // su cota se lea, y sin pasar de tramitoMaximo del cruce para que quepa la cota del medio.
    public double TramitoFraccion { get; set; } = 0.25;
    public double TramitoMinimo { get; set; } = 6.0;
    public double TramitoMaximo { get; set; } = 0.35;
    public double SeparacionViviendas { get; set; } = 2.2;
    public double NombreViaLateral { get; set; } = 7.0;
    public double NombreViaSobreCota { get; set; } = 5.0;
    public string ViaSinNombre { get; set; } = "VIA INTERNA - T.N.";   // vía junto a la tubería que el croquis no nombra
    public string Superficie { get; set; } = "ZONA VERDE";
    public MotorConfig Motor { get; set; } = new();

    // Los de fábrica (kitparametros.json) con lo que el usuario cambió en «Ajustes de la IA» encima (Ajustes). Con un
    // archivo explícito (pruebas) solo ese archivo.
    public static Parametros Cargar(string? archivo = null)
    {
        if (archivo != null) return File.Exists(archivo) ? JsonSerializer.Deserialize<Parametros>(File.ReadAllText(archivo), Json.Opciones) ?? new Parametros() : new Parametros();
        System.Text.Json.Nodes.JsonObject parametros = Ajustes.DeFabrica();
        if (Ajustes.Leer()["parametros"] is System.Text.Json.Nodes.JsonObject propios) Ajustes.Mezclar(parametros, propios);
        return parametros.Deserialize<Parametros>(Json.Opciones) ?? new Parametros();
    }
}

public sealed class Marco
{
    public double X0 { get; set; } = 4.5;
    public double Y0 { get; set; } = 92.221;
    public double X1 { get; set; } = 210.989;
    public double Y1 { get; set; } = 224.023;
    [JsonIgnore] public double Ancho => X1 - X0;
    [JsonIgnore] public double Alto => Y1 - Y0;
}

public sealed class MotorConfig
{
    public string Tipo { get; set; } = "claude-code";   // claude-code | api
    public string Modelo { get; set; } = string.Empty;  // vacío = el predeterminado del motor
    public string Esfuerzo { get; set; } = string.Empty;
    public int MinutosMaximos { get; set; } = 20;
    public int TurnosMaximos { get; set; } = 60;
}

// Tamaños de TAMAÑOS con que se dibujará (para que la vista previa se parezca al resultado).
public sealed class TamanosPedido
{
    public double Vial { get; set; } = 2.4;
    public double Predial { get; set; } = 2.4;
    public double Cotas { get; set; } = 0.05;
    public double Bloques { get; set; } = 1.0;
}

// Lo que pide el plugin (pedido.json en la carpeta del trabajo).
public sealed class Pedido
{
    public string Fuente { get; set; } = string.Empty;   // imagen o PDF de la hoja
    public int Pagina { get; set; } = 1;                  // si es PDF
    public double[]? Recorte { get; set; }                // [x0, y0, x1, y1] en fracción de la hoja (0..1, arriba a la izquierda)
    public string Layout { get; set; } = "ANILLO 1 DETALLE";
    public TamanosPedido Tamanos { get; set; } = new();
    // calcar: dibujar el anillo desde la hoja; detallar: el anillo ya está dibujado (lectura.json) y solo se acota.
    public string Modo { get; set; } = "calcar";
    // Cómo está girada la hoja en el dibujo (grados antihorarios: 0, 90, 180, 270). La IA la lee derecha (el recorte
    // viene en la hoja derecha) y el enderezado gira lo que leyó (ver Giro).
    public int Giro { get; set; }
    // Diámetro del anillo según el título del layout («ANILLO 1 - 3/4"»); vacío si el layout no lo dice.
    public string Diametro { get; set; } = string.Empty;
}

// Carpeta de un trabajo: pedido.json (del plugin) y lo que deja el calco: hoja.*, croquis.json, plano.json, vista.png,
// resultado.json y registro.txt. En modo detallar: lectura.json (del plugin) y detalle.json.
public sealed class Trabajo
{
    private Imagen? _imagen;
    private LecturaDibujo? _lectura;
    private readonly Dictionary<int, Imagen> _paginas = [];

    private Trabajo(string carpeta, Pedido pedido, Parametros parametros)
    {
        Carpeta = carpeta;
        Pedido = pedido;
        Parametros = parametros;
    }

    public string Carpeta { get; }
    public Pedido Pedido { get; }
    public Parametros Parametros { get; }
    public Costo? Costo { get; set; }   // lo que gastó la corrida (lo pone el motor al terminar)

    public string Archivo(string nombre) => Path.Combine(Carpeta, nombre);

    public bool EsDetalle => string.Equals(Pedido.Modo, "detallar", StringComparison.OrdinalIgnoreCase);

    // Lo que ya está dibujado en el layout (modo detallar): lectura.json, que escribe el plugin.
    public LecturaDibujo Lectura => _lectura ??= JsonSerializer.Deserialize<LecturaDibujo>(File.ReadAllText(Archivo("lectura.json")), Json.Opciones)
        ?? throw new InvalidDataException("lectura.json vacío");

    public static Trabajo Abrir(string carpeta, Parametros? parametros = null)
    {
        carpeta = Path.GetFullPath(carpeta);
        string archivo = Path.Combine(carpeta, "pedido.json");
        if (!File.Exists(archivo)) throw new FileNotFoundException("no está pedido.json en la carpeta del trabajo", archivo);
        Pedido pedido = JsonSerializer.Deserialize<Pedido>(File.ReadAllText(archivo), Json.Opciones) ?? throw new InvalidDataException("pedido.json vacío");
        return new Trabajo(carpeta, pedido, parametros ?? Parametros.Cargar());
    }

    // La imagen de la hoja a resolución completa (la de la página del PDF escaneado, tal cual).
    public Imagen Imagen => _imagen ??= Imagen.Cargar(ArchivoHoja());

    public bool EsPdf => string.Equals(Path.GetExtension(Pedido.Fuente), ".pdf", StringComparison.OrdinalIgnoreCase);

    // Páginas del PDF del que sale la hoja (el gasificado completo); 1 si la hoja es una imagen.
    public int Paginas
    {
        get
        {
            if (!EsPdf) return 1;
            using PdfDocument pdf = PdfDocument.Open(Pedido.Fuente);
            return pdf.NumberOfPages;
        }
    }

    // Otra página del mismo PDF (el plano del proyecto, el informe…), en memoria: no se guarda en la carpeta del trabajo.
    public Imagen Pagina(int numero)
    {
        if (numero == Pedido.Pagina) return Imagen;
        if (_paginas.TryGetValue(numero, out Imagen? imagen)) return imagen;
        using PdfDocument pdf = AbrirPdf();
        (byte[] bytes, _) = Extraer(pdf, numero);
        return _paginas[numero] = Imagen.Cargar(new MemoryStream(bytes));
    }

    // Todas las páginas del PDF, en bruto (PNG o JPEG), una por una, para hacer miniaturas.
    public IEnumerable<(int Numero, byte[] Bytes)> TodasLasPaginas()
    {
        using PdfDocument pdf = AbrirPdf();
        for (int n = 1; n <= pdf.NumberOfPages; n++)
        {
            byte[]? bytes = null;
            try { bytes = Extraer(pdf, n).Bytes; }
            catch (InvalidDataException) { /* una página sin imagen escaneada */ }
            if (bytes != null) yield return (n, bytes);
        }
    }

    private PdfDocument AbrirPdf()
    {
        if (!EsPdf) throw new InvalidDataException("la hoja no viene de un PDF: no tiene otras páginas");
        if (!File.Exists(Pedido.Fuente)) throw new FileNotFoundException("no se encontró el PDF de la hoja", Pedido.Fuente);
        return PdfDocument.Open(Pedido.Fuente);
    }

    // La imagen escaneada de una página (la más grande): PNG si se puede, si no el JPEG tal cual.
    private static (byte[] Bytes, string Extension) Extraer(PdfDocument pdf, int numero)
    {
        if (numero < 1 || numero > pdf.NumberOfPages) throw new InvalidDataException($"el PDF tiene {pdf.NumberOfPages} páginas; no existe la {numero}");
        var pagina = pdf.GetPage(numero);
        var imagen = pagina.GetImages().OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples).FirstOrDefault()
            ?? throw new InvalidDataException($"la página {numero} del PDF no tiene una imagen escaneada (¿es un PDF de AutoCAD?)");
        if (imagen.TryGetPng(out byte[]? png) && png != null) return (png, ".png");
        return (Jpeg(imagen.RawBytes.ToArray()), ".jpg"); // los escaneos traen la página como JPEG (DCT): se guarda tal cual
    }

    // Zona de la hoja que eligió el usuario en CALCAR (px de la hoja completa); toda la hoja si no recortó.
    public Rect Recorte
    {
        get
        {
            Imagen imagen = Imagen;
            double[]? r = Pedido.Recorte;
            if (r is null || r.Length < 4) return new Rect(0, 0, imagen.Ancho, imagen.Alto);
            int x0 = (int)Math.Floor(Math.Clamp(Math.Min(r[0], r[2]), 0, 1) * imagen.Ancho);
            int y0 = (int)Math.Floor(Math.Clamp(Math.Min(r[1], r[3]), 0, 1) * imagen.Alto);
            int x1 = (int)Math.Ceiling(Math.Clamp(Math.Max(r[0], r[2]), 0, 1) * imagen.Ancho);
            int y1 = (int)Math.Ceiling(Math.Clamp(Math.Max(r[1], r[3]), 0, 1) * imagen.Alto);
            if (x1 - x0 < 20 || y1 - y0 < 20) return new Rect(0, 0, imagen.Ancho, imagen.Alto);
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }
    }

    public void Registrar(string linea)
    {
        try { File.AppendAllText(Archivo("registro.txt"), $"[{DateTime.Now:HH:mm:ss}] {linea}{Environment.NewLine}"); }
        catch (IOException) { /* el registro no debe tumbar el calco */ }
    }

    // Copia la hoja a la carpeta del trabajo (la primera vez): de un PDF escaneado se saca la imagen de la página.
    private string ArchivoHoja()
    {
        string? existente = Directory.EnumerateFiles(Carpeta, "hoja.*").FirstOrDefault();
        if (existente != null) return existente;

        string fuente = Pedido.Fuente;
        if (!File.Exists(fuente)) throw new FileNotFoundException("no se encontró la hoja", fuente);
        if (!string.Equals(Path.GetExtension(fuente), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            string copia = Archivo("hoja" + Path.GetExtension(fuente).ToLowerInvariant());
            File.Copy(fuente, copia, overwrite: true);
            return copia;
        }

        // La hoja escaneada es la imagen más grande de la página.
        using PdfDocument pdf = PdfDocument.Open(fuente);
        (byte[] bytes, string extension) = Extraer(pdf, Pedido.Pagina);
        string archivo = Archivo("hoja" + extension);
        File.WriteAllBytes(archivo, bytes);
        return archivo;
    }

    // Algunos PDF «comprimidos» guardan el JPEG de la página comprimido otra vez (filtros [/FlateDecode /DCTDecode]): los
    // bytes empiezan con la cabecera zlib (0x78) en lugar de la del JPEG (FF D8). Se descomprime esa capa.
    private static byte[] Jpeg(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x78 || (bytes[0] << 8 | bytes[1]) % 31 != 0) return bytes;
        try
        {
            using var entrada = new System.IO.Compression.ZLibStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            using var salida = new MemoryStream();
            entrada.CopyTo(salida);
            byte[] jpeg = salida.ToArray();
            return jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 ? jpeg : bytes;
        }
        catch (InvalidDataException) { return bytes; }
    }
}

public readonly record struct Rect(int X, int Y, int Ancho, int Alto)
{
    public int X1 => X + Ancho;
    public int Y1 => Y + Alto;
}

public static class Json
{
    public static readonly JsonSerializerOptions Opciones = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indentado = new(Opciones) { WriteIndented = true };
}
