using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoKADN.CalcoIA;

// «Corregir con IA»: el usuario señaló una zona (en el dibujo o en la hoja de referencia) y escribió qué está mal. La IA
// parte del croquis del trabajo anterior, cambia lo que pide la nota y entrega el croquis completo; el plugin borra el
// calco anterior y dibuja el nuevo.
//  · ver_correccion (Ver): la nota, la zona en el dibujo y en la hoja, lado a lado, y el croquis anterior en JSON.
//  · La zona pasa de un lado al otro con la escala del enderezado del croquis anterior (Mapa): exacta en la manzana del
//    anillo, aproximada en las vecinas (por eso se muestra con margen).
//  · Al entregar se resume qué cambió del croquis (Cambios) y se anota en correcciones.jsonl, junto a la carpeta de
//    trabajos (Registrar): las correcciones que se repiten son reglas que faltan.
public static class Correcciones
{
    private const int LadoPanel = 700;   // px: cada mitad de la imagen de ver_correccion

    // Píxel de la hoja derecha ↔ unidades del dibujo, con la escala del enderezado (ver ResultadoEnderezado.Ox).
    internal sealed class Mapa
    {
        private readonly ResultadoEnderezado _r;
        private readonly int _giro;
        private readonly double _ancho, _alto;   // hoja derecha

        public Mapa(ResultadoEnderezado r, int giro, double ancho, double alto)
        {
            _r = r;
            _giro = Giro.Normalizar(giro);
            _ancho = ancho;
            _alto = alto;
        }

        public (double X, double Y) AlDibujo(double x, double y)
        {
            var g = Giro.Punto(x, y, _giro, _ancho, _alto);
            return (_r.Ox + (g.X - _r.Izquierda) * _r.Sx, _r.Oy - (g.Y - _r.Arriba) * _r.Sy);
        }

        public (double X, double Y) ALaHoja(double x, double y)
        {
            double gx = _r.Izquierda + (x - _r.Ox) / _r.Sx, gy = _r.Arriba + (_r.Oy - y) / _r.Sy;
            bool acostada = _giro == 90 || _giro == 270;
            return Giro.Punto(gx, gy, (360 - _giro) % 360, acostada ? _alto : _ancho, acostada ? _ancho : _alto);
        }

        public double[] CajaAlDibujo(Rect r)
        {
            var a = AlDibujo(r.X, r.Y);
            var b = AlDibujo(r.X1, r.Y1);
            return [Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)];
        }

        public Rect CajaALaHoja(double[] c)
        {
            var a = ALaHoja(c[0], c[1]);
            var b = ALaHoja(c[2], c[3]);
            int x0 = (int)Math.Floor(Math.Min(a.X, b.X)), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y));
            return new Rect(x0, y0, (int)Math.Ceiling(Math.Max(a.X, b.X)) - x0, (int)Math.Ceiling(Math.Max(a.Y, b.Y)) - y0);
        }
    }

    // El mapa del trabajo anterior: se endereza otra vez su croquis (da lo mismo que dibujó, con los mismos parámetros).
    internal static Mapa? CrearMapa(Trabajo trabajo)
    {
        try
        {
            // Sobre una copia: el croquis anterior se compara después con el corregido.
            ResultadoEnderezado r = trabajo.PorLados
                ? Herramientas.EnderezarLados(trabajo, JsonSerializer.Deserialize<CroquisLados>(trabajo.CroquisAnteriorJson, Json.Opciones)!)
                : Herramientas.EnderezarCroquis(trabajo, JsonSerializer.Deserialize<Croquis>(trabajo.CroquisAnteriorJson, Json.Opciones)!);
            if (r.Sx <= 0 || r.Sy <= 0) return null;
            return new Mapa(r, trabajo.Pedido.Giro, trabajo.Imagen.Ancho, trabajo.Imagen.Alto);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IndexOutOfRangeException)
        {
            trabajo.Registrar("sin mapa de la corrección: " + ex.Message);
            return null;
        }
    }

    // La zona en el dibujo (unidades, [x0, y0, x1, y1]) y en la hoja (px de la hoja derecha). Todo: el marco y el recorte.
    internal static (double[]? Dibujo, Rect? Hoja) Zonas(Trabajo trabajo, Mapa? mapa)
    {
        PedidoCorreccion c = trabajo.Correccion;
        Marco m = trabajo.Parametros.Marco;
        if (c.Caja is not { Length: >= 4 } k || c.Zona == "todo") return ([m.X0, m.Y0, m.X1, m.Y1], trabajo.Recorte);
        if (c.Zona == "hoja")
        {
            Imagen hoja = trabajo.Imagen;
            int x0 = (int)Math.Floor(Math.Clamp(Math.Min(k[0], k[2]), 0, 1) * hoja.Ancho), y0 = (int)Math.Floor(Math.Clamp(Math.Min(k[1], k[3]), 0, 1) * hoja.Alto);
            int x1 = (int)Math.Ceiling(Math.Clamp(Math.Max(k[0], k[2]), 0, 1) * hoja.Ancho), y1 = (int)Math.Ceiling(Math.Clamp(Math.Max(k[1], k[3]), 0, 1) * hoja.Alto);
            var zona = hoja.Recortar(new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)));
            return (mapa?.CajaAlDibujo(zona), zona);
        }
        double[] dibujo = [Math.Min(k[0], k[2]), Math.Min(k[1], k[3]), Math.Max(k[0], k[2]), Math.Max(k[1], k[3])];
        return (dibujo, mapa == null ? null : trabajo.Imagen.Recortar(mapa.CajaALaHoja(dibujo)));
    }

    // ver_correccion: la nota, la zona en el dibujo (lo que está dibujado ahora) y en la hoja (con el croquis anterior
    // encima), y el croquis anterior para partir de él.
    public static ResultadoHerramienta Ver(Trabajo trabajo)
    {
        PedidoCorreccion c = trabajo.Correccion;
        Mapa? mapa = CrearMapa(trabajo);
        (double[]? dibujo, Rect? hoja) = Zonas(trabajo, mapa);
        bool todo = c.Caja is not { Length: >= 4 } || c.Zona == "todo";

        var texto = new StringBuilder();
        texto.AppendLine($"Corrección que pide el usuario: «{c.Nota.Trim()}»");
        texto.AppendLine(todo ? "No señaló una zona: la corrección es para todo el calco."
            : c.Zona == "hoja" ? "Señaló la zona en la hoja de referencia (el rectángulo rojo de la derecha); en el dibujo es aproximada."
            : "Señaló la zona en el dibujo (el rectángulo rojo de la izquierda); en la hoja es aproximada (las vecinas se corren con el ancho de su vía).");
        if (dibujo != null) texto.AppendLine(FormattableString.Invariant($"Zona en el dibujo: x {dibujo[0]:0.#}–{dibujo[2]:0.#}, y {dibujo[1]:0.#}–{dibujo[3]:0.#} (unidades del dibujo)."));
        // Una zona de la hoja que en el dibujo cae fuera del marco: no hay nada dibujado que mostrar.
        if (dibujo != null && !EnElMarco(dibujo, trabajo.Parametros.Marco))
        {
            texto.AppendLine("Esa zona cae fuera del marco del dibujo: lo que hay ahí en la hoja no quedó dibujado.");
            dibujo = null;
        }
        if (hoja is Rect h) texto.AppendLine(FormattableString.Invariant($"Zona en la hoja: x {h.X}–{h.X1}, y {h.Y}–{h.Y1} (px de la hoja). Amplíela con ampliar o superponer para ver el detalle."));
        else texto.AppendLine("No se pudo ubicar la zona en la hoja: búsquela comparando el dibujo con la hoja (ver_hoja).");
        AgregarAnterior(trabajo, texto);
        texto.AppendLine("Croquis anterior (parta de él, cambie lo que pide la corrección y lo que dependa de ello, y entregue el croquis COMPLETO):");
        texto.AppendLine(JsonNode.Parse(trabajo.CroquisAnteriorJson)?.ToJsonString() ?? trabajo.CroquisAnteriorJson);

        return new ResultadoHerramienta { Texto = texto.ToString(), Png = Paneles(trabajo, dibujo, hoja) };
    }

    // Al menos una cuarta parte de la zona cae dentro del marco.
    private static bool EnElMarco(double[] z, Marco m)
    {
        double ancho = Math.Min(z[2], m.X1) - Math.Max(z[0], m.X0), alto = Math.Min(z[3], m.Y1) - Math.Max(z[1], m.Y0);
        double area = Math.Max(1e-6, (z[2] - z[0]) * (z[3] - z[1]));
        return ancho > 0 && alto > 0 && ancho * alto >= 0.25 * area;
    }

    // Lo que avisó y dudó el trabajo anterior (resultado.json), por si la corrección tiene que ver.
    private static void AgregarAnterior(Trabajo trabajo, StringBuilder texto)
    {
        string archivo = Path.Combine(trabajo.Correccion.Anterior, "resultado.json");
        if (!File.Exists(archivo)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(archivo)) is not JsonObject r) return;
            foreach ((string campo, string titulo) in new[] { ("avisos", "Avisos del calco anterior"), ("dudas", "Dudas que dejó el calco anterior") })
            {
                List<string> lineas = (r[campo] as JsonArray)?.Select(n => n?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList() ?? [];
                if (lineas.Count == 0) continue;
                texto.AppendLine(titulo + ":");
                foreach (string l in lineas) texto.AppendLine("- " + l);
            }
        }
        catch (JsonException) { /* sin avisos anteriores */ }
    }

    // Izquierda: el dibujo actual alrededor de la zona; derecha: la hoja alrededor de la zona con el croquis anterior
    // encima. La zona va en rojo en las dos.
    private static byte[] Paneles(Trabajo trabajo, double[]? dibujo, Rect? hoja)
    {
        var paneles = new List<(BitmapSource Imagen, System.Windows.Rect Zona, string Titulo)>();
        Marco m = trabajo.Parametros.Marco;

        if (dibujo != null)
        {
            byte[] vista = Vista.Dibujar(trabajo.PlanoAnterior, trabajo.Parametros, trabajo.Pedido.Tamanos);
            AutoKADN.CalcoIA.Imagen imagen = AutoKADN.CalcoIA.Imagen.Cargar(new MemoryStream(vista));
            double ancho = dibujo[2] - dibujo[0], alto = dibujo[3] - dibujo[1];
            double margen = Math.Max(10, 0.35 * Math.Max(ancho, alto));
            double x0 = Math.Max(m.X0, dibujo[0] - margen), x1 = Math.Min(m.X1, dibujo[2] + margen);
            double y0 = Math.Max(m.Y0, dibujo[1] - margen), y1 = Math.Min(m.Y1, dibujo[3] + margen);
            (double, double) Px(double x, double y) => (Vista.Margen + (x - m.X0) * Vista.PxPorUnidad, Vista.Margen + (m.Y1 - y) * Vista.PxPorUnidad);
            var (ax, ay) = Px(x0, y1);
            var (bx, by) = Px(x1, y0);
            Rect corte = imagen.Recortar(new Rect((int)ax, (int)ay, Math.Max(1, (int)(bx - ax)), Math.Max(1, (int)(by - ay))));
            var (zx, zy) = Px(dibujo[0], dibujo[3]);
            var (wx, wy) = Px(dibujo[2], dibujo[1]);
            paneles.Add((imagen.Corte(corte), new System.Windows.Rect(zx - corte.X, zy - corte.Y, wx - zx, wy - zy), "EN EL DIBUJO (lo que está dibujado ahora)"));
        }

        if (hoja is Rect zona)
        {
            AutoKADN.CalcoIA.Imagen fuente = trabajo.Imagen;
            int margen = (int)Math.Max(60, 0.35 * Math.Max(zona.Ancho, zona.Alto));
            Rect corte = fuente.Recortar(new Rect(zona.X - margen, zona.Y - margen, zona.Ancho + 2 * margen, zona.Alto + 2 * margen));
            byte[] png = trabajo.PorLados
                ? SuperposicionLados.Dibujar(fuente, corte, 2000, JsonSerializer.Deserialize<CroquisLados>(trabajo.CroquisAnteriorJson, Json.Opciones)!, out double escala)
                : Superposicion.Dibujar(fuente, corte, 2000, trabajo.CroquisAnterior, out escala);
            BitmapSource sobre = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            paneles.Add((sobre, new System.Windows.Rect((zona.X - corte.X) * escala, (zona.Y - corte.Y) * escala, zona.Ancho * escala, zona.Alto * escala),
                FormattableString.Invariant($"EN LA HOJA x {corte.X}–{corte.X1}, y {corte.Y}–{corte.Y1} (croquis anterior encima)")));
        }

        const int cabeza = 24, separacion = 16;
        var escalas = paneles.Select(p => Math.Min(1.0 * LadoPanel / p.Imagen.PixelWidth, 1.0 * LadoPanel / p.Imagen.PixelHeight)).ToList();
        FormattedText Titulo(string t) => new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 13, Brushes.DarkRed, 1.0);
        // Cada panel ocupa lo que su imagen o su título, lo que sea más ancho (los títulos no se montan).
        var anchos = paneles.Select((p, i) => Math.Max(p.Imagen.PixelWidth * escalas[i], Titulo(p.Titulo).Width + 6)).ToList();
        int anchoTotal = (int)Math.Ceiling(anchos.Sum() + separacion * Math.Max(0, paneles.Count - 1));
        int altoTotal = cabeza + (int)Math.Ceiling(paneles.Select((p, i) => p.Imagen.PixelHeight * escalas[i]).DefaultIfEmpty(1).Max());
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, Math.Max(1, anchoTotal), altoTotal));
            var rojo = new Pen(new SolidColorBrush(Color.FromRgb(230, 0, 0)), 3) { DashStyle = new DashStyle([5, 3], 0) };
            double x = 0;
            for (int i = 0; i < paneles.Count; i++)
            {
                (BitmapSource img, System.Windows.Rect z, string titulo) = paneles[i];
                double k = escalas[i], w = img.PixelWidth * k, h = img.PixelHeight * k;
                dc.DrawText(Titulo(titulo), new Point(x + 2, 4));
                dc.DrawImage(img, new System.Windows.Rect(x, cabeza, w, h));
                dc.DrawRectangle(null, rojo, new System.Windows.Rect(x + z.X * k, cabeza + z.Y * k, Math.Max(2, z.Width * k), Math.Max(2, z.Height * k)));
                x += anchos[i] + separacion;
            }
        }
        return AutoKADN.CalcoIA.Imagen.Png(visual, Math.Max(1, anchoTotal), altoTotal);
    }

    // Qué cambió entre el croquis anterior y el corregido, por parte: «tuberías», «accesorios (3 → 4)».
    public static List<string> Cambios(Croquis antes, Croquis despues) =>
        Cambios(JsonSerializer.SerializeToNode(antes, Json.Opciones), JsonSerializer.SerializeToNode(despues, Json.Opciones));

    // Lo mismo para cualquiera de las dos lecturas; en la de lados, por lado («lado abajo»).
    public static List<string> Cambios(JsonNode? a, JsonNode? d)
    {
        var nombres = new (string Campo, string Nombre)[]
        {
            ("manzana", "manzana"), ("predios", "líneas de predio"), ("construcciones", "construcciones"), ("casas", "casas"),
            ("cercas", "cercas"), ("textos", "textos de predio"), ("vecinos", "manzanas vecinas"), ("indicadores", "indicadores"),
            ("vias", "vías"), ("tuberias", "tuberías"), ("accesorios", "accesorios"), ("abierta", "lados abiertos"),
            ("superficie", "superficie"), ("cuadro", "cuadro de tubería"), ("cuadroAccesorios", "cuadro de accesorios"),
            ("notas", "notas"), ("bordillos", "bordillos"),
        };
        var cambios = new List<string>();
        if (a?["lados"] is JsonObject la || d?["lados"] is JsonObject)
        {
            var ladosA = a?["lados"] as JsonObject; var ladosD = d?["lados"] as JsonObject;
            foreach (string lado in (ladosA?.Select(kv => kv.Key) ?? []).Union(ladosD?.Select(kv => kv.Key) ?? []))
                if (!JsonNode.DeepEquals(ladosA?[lado], ladosD?[lado])) cambios.Add("lado " + lado);
        }
        foreach ((string campo, string nombre) in nombres)
        {
            JsonNode? x = a?[campo], y = d?[campo];
            if (JsonNode.DeepEquals(x, y)) continue;
            cambios.Add(x is JsonArray ax && y is JsonArray ay && ax.Count != ay.Count ? $"{nombre} ({ax.Count} → {ay.Count})" : nombre);
        }
        return cambios;
    }

    // Una línea por corrección en correcciones.jsonl (junto a la carpeta de trabajos): fecha, trabajos, nota, zona,
    // qué cambió y lo que costó.
    public static void Registrar(Trabajo trabajo)
    {
        try
        {
            PedidoCorreccion c = trabajo.Correccion;
            string archivoCroquis = trabajo.Archivo("croquis.json");
            List<string> cambios = File.Exists(archivoCroquis)
                ? Cambios(JsonNode.Parse(trabajo.CroquisAnteriorJson), JsonNode.Parse(File.ReadAllText(archivoCroquis)))
                : [];
            var linea = new JsonObject
            {
                ["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["trabajo"] = Path.GetFileName(trabajo.Carpeta),
                ["anterior"] = Path.GetFileName(c.Anterior.TrimEnd('\\', '/')),
                ["layout"] = trabajo.Pedido.Layout,
                ["nota"] = c.Nota,
                ["zona"] = c.Zona,
                ["caja"] = c.Caja == null ? null : new JsonArray(c.Caja.Select(v => (JsonNode?)JsonValue.Create(Math.Round(v, 4))).ToArray()),
                ["cambios"] = new JsonArray(cambios.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["costo"] = trabajo.Costo == null ? null : JsonSerializer.SerializeToNode(trabajo.Costo, Json.Opciones),
            };
            string? trabajos = Path.GetDirectoryName(trabajo.Carpeta.TrimEnd('\\', '/'));
            string? raiz = trabajos == null ? null : Path.GetDirectoryName(trabajos);
            if (raiz == null) return;
            File.AppendAllText(Path.Combine(raiz, "correcciones.jsonl"), linea.ToJsonString(Json.Opciones) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            trabajo.Registrar("no se anotó la corrección: " + ex.Message);
        }
    }
}
