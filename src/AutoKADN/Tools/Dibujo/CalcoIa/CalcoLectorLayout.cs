using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Tools.Layouts;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Lee lo que ya está dibujado dentro del marco de un ANILLO n DETALLE para detallarlo (CalcoDetallador):
//  · tramos de tubería: tramos rectos en azul; continuos = nuevos (T1, T2… en orden de recorrido), punteados = existentes
//    (E1, E2…). Lo rojo es otra red y no cuenta.
//  · accesorios: bloques de la capa Mat (A1, A2… en el orden de los tramos), con su DIAMETRO. Un tramo recto se parte
//    donde tiene una tee o una reducción en medio (el cruce de vía: tramito 0.1 · cruce · tramito 0.1).
//  · diámetro de cada tramo nuevo según las reducciones de sus puntas (vacío = el del anillo).
//  · caras: todo tramo recto que no es tubería ni anotación (manzanas, predios, torres, cercas, bordillos); hacia ellas van
//    las cotas magenta y en ellas las marcas LC.
// Los ids son los que usa el detalle (CalcoDetalle) para decir a qué tramo o accesorio va cada número.
internal sealed class CalcoLectura
{
    public List<CalcoTramo> Tramos { get; } = new List<CalcoTramo>();
    public List<CalcoAccesorioLeido> Accesorios { get; } = new List<CalcoAccesorioLeido>();
    public List<CalcoCara> Caras { get; } = new List<CalcoCara>();
    public List<(string Texto, Point3d Posicion, double Giro)> Textos { get; } = new List<(string, Point3d, double)>();
    public string Anillo { get; set; } = "";
    public string Diametro { get; set; } = "";
    public string Ot { get; set; } = "";

    public CalcoTramo? Tramo(string id) => Tramos.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    public CalcoAccesorioLeido? Accesorio(string id) => Accesorios.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    // Resumen para la IA o para revisar a mano: un renglón por tramo y por accesorio.
    public string Describir()
    {
        var texto = new StringBuilder();
        foreach (CalcoTramo tramo in Tramos)
            texto.AppendLine($"{tramo.Id} {(tramo.Nuevo ? "nuevo" : "existente")} {N(tramo.Inicio)} -> {N(tramo.Fin)} largo {tramo.Largo.ToString("0.#", CultureInfo.InvariantCulture)}{(tramo.Diametro.Length > 0 ? " Ø" + tramo.Diametro : "")}");
        foreach (CalcoAccesorioLeido accesorio in Accesorios)
            texto.AppendLine($"{accesorio.Id} {accesorio.Nombre} {N(accesorio.Posicion)}{(accesorio.Tramo == null ? "" : " en " + accesorio.Tramo.Id)}");
        return texto.ToString();
    }

    // lectura.json para AutoKADN.CalcoIA (modo detallar). Giros en grados.
    public string AJson(string layout)
    {
        string T(string s) => CalcoIaTool.JsonTexto(s);
        string D(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string P(Point3d p) => "[" + D(p.X) + ", " + D(p.Y) + "]";
        var json = new StringBuilder("{\n");
        json.Append("  \"layout\": ").Append(T(layout)).Append(",\n");
        json.Append("  \"anillo\": ").Append(T(Anillo)).Append(", \"diametro\": ").Append(T(Diametro)).Append(", \"ot\": ").Append(T(Ot)).Append(",\n");
        json.Append("  \"tramos\": [\n").Append(string.Join(",\n", Tramos.Select(t =>
            $"    {{\"id\": {T(t.Id)}, \"nuevo\": {(t.Nuevo ? "true" : "false")}, \"inicio\": {P(t.Inicio)}, \"fin\": {P(t.Fin)}, \"diametro\": {T(t.Diametro)}}}"))).Append("\n  ],\n");
        json.Append("  \"accesorios\": [\n").Append(string.Join(",\n", Accesorios.Select(a =>
            $"    {{\"id\": {T(a.Id)}, \"nombre\": {T(a.Nombre)}, \"diametro\": {T(a.Diametro)}, \"x\": {D(a.Posicion.X)}, \"y\": {D(a.Posicion.Y)}, \"tramo\": {T(a.Tramo?.Id ?? "")}}}"))).Append("\n  ],\n");
        json.Append("  \"caras\": [").Append(string.Join(", ", Caras.Select(c => "[" + P(c.Inicio) + ", " + P(c.Fin) + "]"))).Append("],\n");
        json.Append("  \"textos\": [\n").Append(string.Join(",\n", Textos.Select(t =>
            $"    {{\"texto\": {T(t.Texto)}, \"x\": {D(t.Posicion.X)}, \"y\": {D(t.Posicion.Y)}, \"giro\": {D(t.Giro * 180.0 / Math.PI)}}}"))).Append("\n  ]\n");
        return json.Append("}\n").ToString();
    }

    private static string N(Point3d p) => "(" + p.X.ToString("0.##", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("0.##", CultureInfo.InvariantCulture) + ")";
}

internal sealed class CalcoTramo
{
    public string Id { get; set; } = "";
    public bool Nuevo { get; set; }
    public Point3d Inicio { get; set; }
    public Point3d Fin { get; set; }
    public string Diametro { get; set; } = "";
    public Vector3d Direccion => (Fin - Inicio).GetNormal();
    public double Largo => Inicio.DistanceTo(Fin);
    public Point3d En(double fraccion) => Inicio + (Fin - Inicio) * fraccion;
}

internal sealed class CalcoAccesorioLeido
{
    public string Id { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Diametro { get; set; } = "";   // 3/4, 1/2, 3/4x1/2
    public Point3d Posicion { get; set; }
    public ObjectId Bloque { get; set; }
    public CalcoTramo? Tramo { get; set; }
}

internal sealed class CalcoCara
{
    public int Indice { get; set; }
    public Point3d Inicio { get; set; }
    public Point3d Fin { get; set; }
    public Vector3d Direccion => (Fin - Inicio).GetNormal();
    public double Largo => Inicio.DistanceTo(Fin);
}

internal static class CalcoLectorLayout
{
    private const short Azul = 5;
    private const double Tolerancia = 0.05;
    private const string CapaAccesorios = "Mat";
    private static readonly string[] CapasAnotacion = { "COTAS MAGENTA", "MARQUILLA", "Textos", "Logo" };
    private static readonly Regex Ot = new Regex(@"O\.?\s*T\.?\s*#?\s*:?\s*(\d{6,})", RegexOptions.IgnoreCase);
    private static readonly Regex Diametro = new Regex(@"[∅Øø]\s*(?<d>[0-9][0-9/]*)");

    public static CalcoLectura Leer(Transaction transaction, BlockTableRecord espacio)
    {
        var lectura = new CalcoLectura();
        var nuevos = new List<(Point3d A, Point3d B)>();
        var existentes = new List<(Point3d A, Point3d B)>();
        var circulos = new List<Circle>();
        var bloques = new List<BlockReference>();
        var otros = new List<(Point3d A, Point3d B)>();

        foreach (ObjectId id in espacio)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entidad) continue;
            if (entidad is Circle circulo) { circulos.Add(circulo); continue; }
            if (entidad is DBText || entidad is MText)
            {
                string contenido = entidad is DBText dbText ? dbText.TextString : ((MText)entidad).Text;
                Point3d donde = entidad is DBText t1 ? (t1.Justify == AttachmentPoint.BaseLeft ? t1.Position : t1.AlignmentPoint) : ((MText)entidad).Location;
                double giro = entidad is DBText t2 ? t2.Rotation : ((MText)entidad).Rotation;
                if (lectura.Ot.Length == 0 && Ot.Match(contenido) is Match ot && ot.Success) lectura.Ot = ot.Groups[1].Value;
                if (!EsAnotacion(entidad.Layer) && DentroDelMarco(donde) && contenido.Trim().Length > 0) lectura.Textos.Add((contenido.Replace("\n", " ").Trim(), donde, giro));
                continue;
            }
            if (entidad is BlockReference bloque)
            {
                if (string.Equals(bloque.Layer, CapaAccesorios, StringComparison.OrdinalIgnoreCase) && DentroDelMarco(bloque.Position)) bloques.Add(bloque);
                continue;
            }
            if (EsAnotacion(entidad.Layer)) continue;
            List<(Point3d A, Point3d B)> segmentos = Segmentos(entidad);
            if (segmentos.Count == 0) continue;

            bool azul = Color(transaction, entidad) == Azul;
            bool continua = Continua(transaction, entidad);
            string tipoLinea = TipoDeLinea(transaction, entidad);
            foreach ((Point3d a, Point3d b) in segmentos)
            {
                if (!DentroDelMarco(a + (b - a) * 0.5, 0.5)) continue; // el borde del marco no cuenta
                if (azul && continua) nuevos.Add((a, b));
                else if (azul) existentes.Add((a, b));
                else if (continua || tipoLinea.IndexOf("X", StringComparison.OrdinalIgnoreCase) >= 0) otros.Add((a, b)); // cercas: linea_X_linea
            }
        }

        // El indicador del anillo: el círculo con un número y un diámetro adentro.
        foreach (Circle circulo in circulos.Where(c => DentroDelMarco(c.Center)))
        {
            var adentro = lectura.Textos.Where(t => t.Posicion.DistanceTo(circulo.Center) < circulo.Radius).ToList();
            var numeroAnillo = adentro.FirstOrDefault(t => Regex.IsMatch(t.Texto, @"^\d+$"));
            var diametro = adentro.Select(t => Diametro.Match(t.Texto)).FirstOrDefault(m => m.Success);
            if (numeroAnillo.Texto == null || diametro == null) continue;
            lectura.Anillo = numeroAnillo.Texto;
            lectura.Diametro = diametro.Groups["d"].Value;
            break;
        }

        // La raya del indicador (dentro de su círculo) no es una cara.
        otros.RemoveAll(s => circulos.Any(c => s.A.DistanceTo(c.Center) <= c.Radius + Tolerancia && s.B.DistanceTo(c.Center) <= c.Radius + Tolerancia));
        for (int i = 0; i < otros.Count; i++) lectura.Caras.Add(new CalcoCara { Indice = i, Inicio = otros[i].A, Fin = otros[i].B });

        // Un tramo recto se parte donde tiene una tee o una reducción en medio (los tramitos del cruce de vía).
        var cortes = bloques.Where(b => NombreBloque(transaction, b) is "TEE" or "REDUCCION").Select(b => b.Position).ToList();
        nuevos = nuevos.SelectMany(s => Partir(s.A, s.B, cortes)).ToList();

        int numero = 1;
        foreach (List<(Point3d A, Point3d B)> cadena in Cadenas(nuevos))
            foreach ((Point3d a, Point3d b) in cadena)
                lectura.Tramos.Add(new CalcoTramo { Id = "T" + numero++, Nuevo = true, Inicio = a, Fin = b });
        numero = 1;
        foreach ((Point3d a, Point3d b) in existentes.OrderByDescending(s => Math.Max(s.A.Y, s.B.Y)).ThenBy(s => Math.Min(s.A.X, s.B.X)))
            lectura.Tramos.Add(new CalcoTramo { Id = "E" + numero++, Nuevo = false, Inicio = a, Fin = b });

        // Accesorios en el orden de los tramos (y a lo largo de cada uno).
        var leidos = new List<(BlockReference Bloque, CalcoTramo? Tramo, double Orden)>();
        foreach (BlockReference bloque in bloques)
        {
            CalcoTramo? cercano = lectura.Tramos.Where(t => t.Nuevo).OrderBy(t => Distancia(bloque.Position, t.Inicio, t.Fin)).FirstOrDefault();
            if (cercano != null && Distancia(bloque.Position, cercano.Inicio, cercano.Fin) > 1.0) cercano = null;
            double orden = cercano == null ? double.MaxValue : lectura.Tramos.IndexOf(cercano) + (bloque.Position - cercano.Inicio).DotProduct(cercano.Direccion) / Math.Max(cercano.Largo, 1e-9);
            leidos.Add((bloque, cercano, orden));
        }
        numero = 1;
        foreach (var leido in leidos.OrderBy(l => l.Orden).ThenByDescending(l => l.Bloque.Position.Y))
            lectura.Accesorios.Add(new CalcoAccesorioLeido
            {
                Id = "A" + numero++,
                Nombre = NombreBloque(transaction, leido.Bloque),
                Bloque = leido.Bloque.ObjectId,
                Diametro = DiametroBloque(leido.Bloque),
                Posicion = leido.Bloque.Position,
                Tramo = leido.Tramo,
            });

        // Diámetro de los tramos del cruce: entre dos reducciones, el mayor (el cruce de 3/4"); entre una reducción y la
        // tee de la red menor, el menor (el tramito de 0.1); entre una reducción y otra cosa, el mayor.
        foreach (CalcoTramo tramo in lectura.Tramos.Where(t => t.Nuevo))
        {
            CalcoAccesorioLeido? EnPunta(Point3d p, string nombre) => lectura.Accesorios.FirstOrDefault(a => a.Nombre == nombre && a.Posicion.DistanceTo(p) <= 0.5);
            CalcoAccesorioLeido? r0 = EnPunta(tramo.Inicio, "REDUCCION"), r1 = EnPunta(tramo.Fin, "REDUCCION");
            CalcoAccesorioLeido? reduccion = r0 ?? r1;
            if (reduccion == null) continue;
            string[] partes = reduccion.Diametro.Split('x', 'X');
            if (partes.Length != 2) continue;
            if (r0 != null && r1 != null) { tramo.Diametro = partes[0]; continue; }
            CalcoAccesorioLeido? tee = EnPunta(r0 != null ? tramo.Fin : tramo.Inicio, "TEE");
            tramo.Diametro = tee != null && tee.Diametro == partes[1] ? partes[1] : partes[0];
        }
        return lectura;
    }

    public static bool DentroDelMarco(Point3d punto, double margen = 0.0) =>
        punto.X >= ClonarUcTool.FrameMinX + margen && punto.X <= ClonarUcTool.FrameMaxX - margen
        && punto.Y >= ClonarUcTool.DetalleFrameMinY + margen && punto.Y <= ClonarUcTool.DetalleFrameMaxY - margen;

    public static double Distancia(Point3d punto, Point3d a, Point3d b)
    {
        Vector3d ab = b - a;
        double t = ab.Length < 1e-9 ? 0.0 : Math.Max(0.0, Math.Min(1.0, (punto - a).DotProduct(ab) / ab.DotProduct(ab)));
        return punto.DistanceTo(a + ab * t);
    }

    private static bool EsAnotacion(string capa) =>
        CapasAnotacion.Any(c => string.Equals(c, capa, StringComparison.OrdinalIgnoreCase))
        || capa.StartsWith("COTA_", StringComparison.OrdinalIgnoreCase)
        || capa.StartsWith("UC_", StringComparison.OrdinalIgnoreCase);

    private static List<(Point3d A, Point3d B)> Segmentos(Entity entidad)
    {
        var segmentos = new List<(Point3d A, Point3d B)>();
        if (entidad is Line linea)
        {
            if (linea.Length > Tolerancia) segmentos.Add((linea.StartPoint, linea.EndPoint));
        }
        else if (entidad is Polyline polilinea)
        {
            int cuantos = polilinea.Closed ? polilinea.NumberOfVertices : polilinea.NumberOfVertices - 1;
            for (int i = 0; i < cuantos; i++)
            {
                if (polilinea.GetSegmentType(i) != SegmentType.Line) continue;
                Point3d a = polilinea.GetPoint3dAt(i), b = polilinea.GetPoint3dAt((i + 1) % polilinea.NumberOfVertices);
                if (a.DistanceTo(b) > Tolerancia) segmentos.Add((a, b));
            }
        }
        return segmentos;
    }

    // Tramos nuevos encadenados por sus extremos. Cada cadena empieza en su extremo suelto de más arriba (y más a la
    // izquierda), así T1 es el primer tramo bajando desde arriba a la izquierda.
    private static List<List<(Point3d A, Point3d B)>> Cadenas(List<(Point3d A, Point3d B)> segmentos)
    {
        var pendientes = new List<(Point3d A, Point3d B)>(segmentos);
        var cadenas = new List<List<(Point3d A, Point3d B)>>();
        while (pendientes.Count > 0)
        {
            var extremos = new List<Point3d>();
            foreach ((Point3d a, Point3d b) in pendientes) { extremos.Add(a); extremos.Add(b); }
            List<Point3d> sueltos = extremos.Where(p => extremos.Count(q => q.DistanceTo(p) <= Tolerancia) == 1).ToList();
            Point3d inicio = (sueltos.Count > 0 ? sueltos : extremos).OrderByDescending(p => Math.Round(p.Y, 3)).ThenBy(p => p.X).First();

            var cadena = new List<(Point3d A, Point3d B)>();
            Point3d actual = inicio;
            while (true)
            {
                int i = pendientes.FindIndex(s => s.A.DistanceTo(actual) <= Tolerancia || s.B.DistanceTo(actual) <= Tolerancia);
                if (i < 0) break;
                (Point3d a, Point3d b) = pendientes[i];
                pendientes.RemoveAt(i);
                if (b.DistanceTo(actual) <= Tolerancia) (a, b) = (b, a);
                cadena.Add((a, b));
                actual = b;
            }
            if (cadena.Count == 0) { cadena.Add(pendientes[0]); pendientes.RemoveAt(0); }
            cadenas.Add(cadena);
        }
        return cadenas.OrderByDescending(c => Math.Round(c[0].A.Y, 3)).ThenBy(c => c[0].A.X).ToList();
    }

    private static IEnumerable<(Point3d A, Point3d B)> Partir(Point3d a, Point3d b, List<Point3d> cortes)
    {
        Vector3d d = b - a;
        double largo = d.Length;
        List<double> en = cortes.Select(c => (c - a).DotProduct(d) / (largo * largo))
            .Where(t => t * largo > 0.5 && (1 - t) * largo > 0.5)
            .Where(t => Distancia(a + d * t, a, b) < 1e-6 && cortes.Any(c => c.DistanceTo(a + d * t) <= 0.5))
            .Distinct().OrderBy(t => t).ToList();
        Point3d desde = a;
        foreach (double t in en) { Point3d hasta = a + d * t; yield return (desde, hasta); desde = hasta; }
        yield return (desde, b);
    }

    // DIAMETRO del bloque dinámico, sin comillas: «3/4"» → «3/4», «3/4x1/2"» → «3/4x1/2».
    private static string DiametroBloque(BlockReference bloque)
    {
        if (!bloque.IsDynamicBlock) return "";
        foreach (DynamicBlockReferenceProperty propiedad in bloque.DynamicBlockReferencePropertyCollection)
            if (string.Equals(propiedad.PropertyName, "DIAMETRO", StringComparison.OrdinalIgnoreCase))
                return (propiedad.Value?.ToString() ?? "").Replace("\"", "").Replace(" ", "").Trim();
        return "";
    }

    private static short Color(Transaction transaction, Entity entidad)
    {
        if (!entidad.Color.IsByLayer) return entidad.Color.ColorIndex;
        var capa = (LayerTableRecord)transaction.GetObject(entidad.LayerId, OpenMode.ForRead);
        return capa.Color.ColorIndex;
    }

    private static string TipoDeLinea(Transaction transaction, Entity entidad)
    {
        ObjectId id = entidad.LinetypeId;
        var registro = (LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead);
        if (string.Equals(registro.Name, "ByLayer", StringComparison.OrdinalIgnoreCase))
        {
            var capa = (LayerTableRecord)transaction.GetObject(entidad.LayerId, OpenMode.ForRead);
            registro = (LinetypeTableRecord)transaction.GetObject(capa.LinetypeObjectId, OpenMode.ForRead);
        }
        return registro.Name;
    }

    private static bool Continua(Transaction transaction, Entity entidad) =>
        string.Equals(TipoDeLinea(transaction, entidad), "Continuous", StringComparison.OrdinalIgnoreCase);

    private static string NombreBloque(Transaction transaction, BlockReference bloque)
    {
        ObjectId id = bloque.IsDynamicBlock ? bloque.DynamicBlockTableRecord : bloque.BlockTableRecord;
        return ((BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name;
    }
}
