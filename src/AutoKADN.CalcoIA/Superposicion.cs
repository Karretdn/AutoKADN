using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AutoKADN.CalcoIA;

// El croquis que leyó la IA dibujado encima de la hoja escaneada (que queda aclarada), para que compare y vea lo que le
// falta o está corrido: manzana en rojo, predios en naranja, vecinos en café, tubería en azul (continua la nueva con su
// cota en medio de cada tramo, punteada la existente, magenta el cruce), accesorios en círculos con su letra, textos de
// predio en verde, vías en morado, indicadores, cercas en verde azulado (con x) y casas en café claro con su techo y su
// rótulo. Todo en píxeles de la hoja completa, como el croquis.
public static class Superposicion
{
    public static byte[] Dibujar(Imagen hoja, Rect zona, int maxLado, Croquis c, out double escala)
    {
        zona = hoja.Recortar(zona);
        escala = Math.Min(1.0, (double)maxLado / Math.Max(zona.Ancho, zona.Alto));
        double k = escala;
        int ancho = Math.Max(1, (int)Math.Round(zona.Ancho * k)), alto = Math.Max(1, (int)Math.Round(zona.Alto * k));
        Point P(double x, double y) => new((x - zona.X) * k, (y - zona.Y) * k);
        Point Q(double[] q) => P(q[0], q[1]);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, ancho, alto));
            dc.PushOpacity(0.45); // la hoja aclarada: lo dibujado encima resalta
            dc.DrawImage(hoja.Corte(zona), new System.Windows.Rect(0, 0, ancho, alto));
            dc.Pop();

            Pen Lapiz(Color color, double grosor, bool punteada = false) =>
                new(new SolidColorBrush(color), grosor) { DashStyle = punteada ? new DashStyle([4, 3], 0) : DashStyles.Solid };
            var rojo = Color.FromRgb(220, 20, 20);
            var naranja = Color.FromRgb(240, 130, 0);
            var cafe = Color.FromRgb(130, 80, 30);
            var azul = Color.FromRgb(0, 70, 230);
            var magenta = Color.FromRgb(200, 0, 200);
            var verde = Color.FromRgb(0, 140, 0);
            var morado = Color.FromRgb(110, 40, 170);

            void Poli(double[][] puntos, Pen lapiz, bool cerrada)
            {
                var validos = puntos.Where(q => q.Length >= 2).ToList();
                if (validos.Count < 2) return;
                var figura = new StreamGeometry();
                using (StreamGeometryContext g = figura.Open())
                {
                    g.BeginFigure(Q(validos[0]), false, cerrada);
                    foreach (double[] q in validos.Skip(1)) g.LineTo(Q(q), true, false);
                }
                dc.DrawGeometry(null, lapiz, figura);
            }

            Poli(c.Manzana, Lapiz(rojo, 2.6), true);
            foreach (double[][] p in c.Predios) Poli(p, Lapiz(naranja, 1.8), false);
            foreach (double[][] p in c.Construcciones) Poli(p, Lapiz(naranja, 1.4, true), true);
            foreach (VecinoCroquis v in c.Vecinos) Poli(v.Borde, Lapiz(cafe, 2.2), false);
            var turquesa = Color.FromRgb(0, 150, 150);
            // Líneas de bordillo (LB): negras punteadas finas, con «LB».
            foreach (double[][] lb in c.Bordillos)
            {
                Poli(lb, Lapiz(Color.FromRgb(20, 20, 20), 1.6, punteada: true), false);
                if (lb.Length >= 2) dc.DrawText(new FormattedText("LB", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 13, Brushes.Black, 1.0), P((lb[0][0] + lb[1][0]) / 2, (lb[0][1] + lb[1][1]) / 2));
            }
            // Notas: un recuadro donde está escrita y la flecha.
            foreach (NotaCroquis n in c.Notas)
            {
                Point o = P(n.X, n.Y);
                dc.DrawRectangle(null, Lapiz(Color.FromRgb(90, 90, 90), 1.2, punteada: true), new System.Windows.Rect(o.X, o.Y, 60, 30));
                dc.DrawText(new FormattedText("nota", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 12, Brushes.DimGray, 1.0), new Point(o.X + 2, o.Y + 2));
                if (n.Flecha is { Length: >= 2 } fl) dc.DrawLine(Lapiz(Color.FromRgb(90, 90, 90), 1.4), o, P(fl[0], fl[1]));
            }
            foreach (double[][] cerca in c.Cercas)
            {
                Poli(cerca, Lapiz(turquesa, 2.0, punteada: true), false);
                foreach (double[] q in cerca.Where(q => q.Length >= 2)) Etiqueta(dc, "x", Q(q), turquesa, 11, false);
            }
            var cafeClaro = Color.FromRgb(170, 110, 50);
            foreach (CasaCroquis casa in c.Casas.Where(x => x.Caja.Length >= 4))
            {
                double x0 = Math.Min(casa.Caja[0], casa.Caja[2]), x1 = Math.Max(casa.Caja[0], casa.Caja[2]), y0 = Math.Min(casa.Caja[1], casa.Caja[3]), y1 = Math.Max(casa.Caja[1], casa.Caja[3]);
                Pen lapiz = Lapiz(cafeClaro, 2.0, punteada: casa.EnConstruccion);
                dc.DrawRectangle(null, lapiz, new System.Windows.Rect(P(x0, y0), P(x1, y1)));
                if (casa.Techo)
                {
                    // Techo como en el plano: cumbrera a lo largo y las esquinas a ella.
                    double w = x1 - x0, h = y1 - y0, m = 0.3 * Math.Min(w, h);
                    if (w >= h) { double ym = (y0 + y1) / 2; Poli([[x0, y0], [x0 + m, ym], [x0, y1]], lapiz, false); Poli([[x1, y0], [x1 - m, ym], [x1, y1]], lapiz, false); Poli([[x0 + m, ym], [x1 - m, ym]], lapiz, false); }
                    else { double xm = (x0 + x1) / 2; Poli([[x0, y0], [xm, y0 + m], [x1, y0]], lapiz, false); Poli([[x0, y1], [xm, y1 - m], [x1, y1]], lapiz, false); Poli([[xm, y0 + m], [xm, y1 - m]], lapiz, false); }
                }
                string rotulo = (casa.Rotulo ?? string.Empty) + (casa.EnConstruccion ? " (en constr.)" : string.Empty);
                if (rotulo.Trim().Length > 0) Etiqueta(dc, rotulo.Trim(), P((x0 + x1) / 2, (y0 + y1) / 2), cafeClaro, 11, true);
            }

            foreach (TuberiaCroquis t in c.Tuberias)
            {
                bool cruce = string.Equals(t.Tipo, "cruce", StringComparison.OrdinalIgnoreCase);
                bool nueva = string.Equals(t.Tipo, "nueva", StringComparison.OrdinalIgnoreCase);
                bool matriz = string.Equals(t.Tipo, "matriz", StringComparison.OrdinalIgnoreCase);
                Poli(t.Puntos, Lapiz(cruce ? magenta : matriz ? rojo : azul, nueva || cruce || matriz ? 3.0 : 2.0, punteada: !nueva && !cruce && !matriz), t.Cerrada);
                var pts = t.Puntos.Where(q => q.Length >= 2).ToList();
                int tramos = t.Cerrada ? pts.Count : pts.Count - 1;
                for (int i = 0; i < tramos && i < t.Cotas.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(t.Cotas[i])) continue;
                    double[] a = pts[i], b = pts[(i + 1) % pts.Count];
                    Etiqueta(dc, t.Cotas[i]!, P((a[0] + b[0]) / 2, (a[1] + b[1]) / 2), cruce ? magenta : azul, 13, true);
                }
            }

            foreach (AccesorioCroquis a in c.Accesorios)
            {
                Point centro = P(a.X, a.Y);
                dc.DrawEllipse(null, Lapiz(magenta, 2.2), centro, 9, 9);
                string sigla = a.Nombre.Trim().ToUpperInvariant() switch { "TAPON" => "Tp", "TEE" => "T", "UNION" => "U", "REDUCCION" => "R", "VALVULA" => "V", "SILLETA" => "S", var n => n.Length > 0 ? n[..1] : "?" };
                Etiqueta(dc, sigla, centro + new Vector(14, -12), magenta, 12, true);
            }
            foreach (TextoCroquis t in c.Textos) Etiqueta(dc, t.Texto, P(t.X, t.Y), verde, 12, true);
            foreach (ViaCroquis v in c.Vias) Etiqueta(dc, v.Nombre, P(v.X, v.Y), morado, 12, true);
            foreach (IndicadorCroquis i in c.Indicadores)
            {
                Point centro = P(i.X, i.Y);
                dc.DrawEllipse(null, Lapiz(verde, 2.0), centro, 16, 16);
                Etiqueta(dc, i.Numero + " Ø" + i.Diametro + (string.IsNullOrWhiteSpace(i.Viviendas) ? "" : " · " + i.Viviendas), centro + new Vector(0, 26), verde, 12, true);
            }
        }
        return Imagen.Png(visual, ancho, alto);
    }

    private static void Etiqueta(DrawingContext dc, string texto, Point centro, Color color, double px, bool fondo)
    {
        var f = new FormattedText(texto, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), px, new SolidColorBrush(color), 1.0);
        var caja = new System.Windows.Rect(centro.X - f.Width / 2 - 2, centro.Y - f.Height / 2 - 1, f.Width + 4, f.Height + 2);
        if (fondo) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), null, caja);
        dc.DrawText(f, new Point(caja.X + 2, caja.Y + 1));
    }
}

// La lectura por lados encima de la hoja: la manzana, los predios y los textos donde están; de cada lado, los carriles
// a poca distancia de la cara (en su orden, no donde están dibujados: lo que se compara es hasta dónde llegan y dónde van
// los accesorios), la LB punteada, la vía y lo de enfrente.
public static class SuperposicionLados
{
    public static byte[] Dibujar(Imagen hoja, Rect zona, int maxLado, CroquisLados c, out double escala)
    {
        double mx0 = c.Manzana.Min(q => q[0]), mx1 = c.Manzana.Max(q => q[0]), my0 = c.Manzana.Min(q => q[1]), my1 = c.Manzana.Max(q => q[1]);
        double paso = Math.Max(12, 0.025 * Math.Min(mx1 - mx0, my1 - my0));
        var croquis = new Croquis
        {
            Manzana = c.Manzana, Predios = c.Predios, Textos = c.Textos, Indicadores = c.Indicadores,
        };
        var bordillos = new List<double[][]>();
        foreach (var (nombre, l) in c.Lados)
        {
            string n = nombre.Trim().ToLowerInvariant();
            bool h = n is "arriba" or "abajo";
            double cara = n switch { "arriba" => my0, "abajo" => my1, "izquierda" => mx0, _ => mx1 };
            double signo = n is "arriba" or "izquierda" ? -1 : 1;   // hacia afuera en píxeles de la hoja
            double a0 = h ? mx0 : my0, a1 = h ? mx1 : my1, largo = a1 - a0;
            double[] Pt(double enLargo, double fuera) => h ? [enLargo, cara + signo * fuera] : [cara + signo * fuera, enLargo];
            double Pos(System.Text.Json.JsonElement? e, double defecto, double borde) =>
                e is System.Text.Json.JsonElement v ? v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble()
                : v.ValueKind == System.Text.Json.JsonValueKind.String && string.Equals(v.GetString(), "borde", StringComparison.OrdinalIgnoreCase) ? borde : defecto : defecto;
            for (int k = 1; k <= l.Carriles.Count; k++)
            {
                CarrilCroquis carril = l.Carriles[k - 1];
                double a = Pos(carril.Desde, a0, a0 - 0.3 * largo), b = Pos(carril.Hasta, a1, a1 + 0.3 * largo);
                bool troncal = string.Equals(carril.Red, "troncal", StringComparison.OrdinalIgnoreCase);
                // Cada tramo con su estado (un carril puede seguir como existente).
                var limites = new List<double> { a };
                foreach (TramoLado t in carril.Tramos.Take(Math.Max(0, carril.Tramos.Count - 1))) limites.Add(t.Hasta);
                limites.Add(b);
                for (int t = 0; t + 1 < limites.Count; t++)
                {
                    string estado = carril.Tramos.Count > t && !string.IsNullOrWhiteSpace(carril.Tramos[t].Estado) ? carril.Tramos[t].Estado! : carril.Estado;
                    bool esNueva = string.Equals(estado, "nueva", StringComparison.OrdinalIgnoreCase);
                    string? cota = carril.Tramos.Count > t ? carril.Tramos[t].Cota : carril.Cota;
                    croquis.Tuberias.Add(new TuberiaCroquis { Tipo = troncal ? "matriz" : esNueva ? "nueva" : "existente", Puntos = [Pt(limites[t], k * paso), Pt(limites[t + 1], k * paso)], Cotas = esNueva ? [cota] : [] });
                }
            }
            double fuera = (Math.Max(1, l.Carriles.Count) + 1) * paso;
            if (l.Bordillo) bordillos.Add([Pt(a0, fuera), Pt(a1, fuera)]);
            if (!string.IsNullOrWhiteSpace(l.Via)) croquis.Vias.Add(new ViaCroquis { Nombre = l.Via!, X = Pt((a0 + a1) / 2, fuera + 2 * paso)[0], Y = Pt((a0 + a1) / 2, fuera + 2 * paso)[1] });
            if (string.Equals(l.Enfrente, "manzana", StringComparison.OrdinalIgnoreCase)) croquis.Vecinos.Add(new VecinoCroquis { Borde = [Pt(a0, fuera + 4 * paso), Pt(a1, fuera + 4 * paso)] });
            if (string.Equals(l.Enfrente, "pegada", StringComparison.OrdinalIgnoreCase)) croquis.Vecinos.Add(new VecinoCroquis { Borde = [Pt(a0 - 0.2 * largo, 0), Pt(a1 + 0.2 * largo, 0)] });
            foreach (AccesorioLado acc in c.Accesorios.Where(x => string.Equals(x.Lado, nombre, StringComparison.OrdinalIgnoreCase)))
            {
                double[] q = Pt(acc.En, Math.Max(1, acc.Carril) * paso);
                croquis.Accesorios.Add(new AccesorioCroquis { Nombre = acc.Nombre, X = q[0], Y = q[1] });
            }
            foreach (NotaLado nota in c.Notas.Where(x => string.Equals(x.Lado, nombre, StringComparison.OrdinalIgnoreCase)))
            {
                double[] q = Pt(nota.En, Math.Max(1, nota.Carril) * paso), o = Pt(nota.En + 3 * paso, fuera + 3 * paso);
                croquis.Notas.Add(new NotaCroquis { Texto = nota.Texto, X = o[0], Y = o[1], Flecha = q });
            }
        }
        croquis.Bordillos = bordillos.ToArray();
        return Superposicion.Dibujar(hoja, zona, maxLado, croquis, out escala);
    }
}
