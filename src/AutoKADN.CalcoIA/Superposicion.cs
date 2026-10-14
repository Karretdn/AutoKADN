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
                Poli(t.Puntos, Lapiz(cruce ? magenta : azul, nueva || cruce ? 3.0 : 2.0, punteada: !nueva && !cruce), t.Cerrada);
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
