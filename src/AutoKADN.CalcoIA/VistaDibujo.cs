using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AutoKADN.CalcoIA;

// Imagen del anillo ya dibujado (lectura.json) para el modo «detallar»: caras en negro, tramos nuevos en azul con su id y
// una flecha de inicio a fin, existentes punteados, accesorios en rojo con su id. Con un detalle encima (revisar_detalle)
// dibuja dónde caería cada número: largos en verde junto a su tramo, ubicaciones en magenta.
public static class VistaDibujo
{
    private const double PxPorUnidad = 7.0;

    public static byte[] Dibujar(LecturaDibujo l, Marco m, DetalleIa? detalle = null)
    {
        int ancho = (int)Math.Ceiling(m.Ancho * PxPorUnidad) + 20, alto = (int)Math.Ceiling(m.Alto * PxPorUnidad) + 20;
        Point P(double x, double y) => new(10 + (x - m.X0) * PxPorUnidad, 10 + (m.Y1 - y) * PxPorUnidad);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, ancho, alto));
            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new System.Windows.Rect(P(m.X0, m.Y1), P(m.X1, m.Y0)));
            var negro = new Pen(Brushes.Black, 1.1);
            foreach (double[][] c in l.Caras) dc.DrawLine(negro, P(c[0][0], c[0][1]), P(c[1][0], c[1][1]));
            foreach (TextoLeido t in l.Textos) Texto(dc, t.Texto, P(t.X, t.Y), t.Giro, 11, Brushes.DimGray, false);

            var azul = new Pen(Brushes.Blue, 2.4);
            var punteada = new Pen(new SolidColorBrush(Color.FromRgb(70, 110, 230)), 1.6) { DashStyle = new DashStyle([5, 4], 0) };
            foreach (TramoLeido t in l.Tramos)
            {
                Point a = P(t.Inicio[0], t.Inicio[1]), b = P(t.Fin[0], t.Fin[1]);
                dc.DrawLine(t.Nuevo ? azul : punteada, a, b);
                Vector v = b - a;
                if (v.Length < 1) continue;
                v.Normalize();
                var n = new Vector(-v.Y, v.X);
                if (t.Nuevo)
                {
                    Point punta = a + v * Math.Min(28, (b - a).Length * 0.3);   // flecha cerca del inicio: el sentido de «en»
                    dc.DrawLine(azul, punta, punta - v * 9 + n * 5);
                    dc.DrawLine(azul, punta, punta - v * 9 - n * 5);
                    Texto(dc, "0", a + n * 12, 0, 11, Brushes.Blue, true);
                    Texto(dc, "1", b + n * 12, 0, 11, Brushes.Blue, true);
                }
                Point medio = a + (b - a) / 2;
                Texto(dc, t.Id, medio + n * 16, 0, 15, t.Nuevo ? Brushes.Blue : Brushes.SteelBlue, true);
            }

            var rojo = new SolidColorBrush(Color.FromRgb(210, 0, 0));
            foreach (AccesorioLeido a in l.Accesorios)
            {
                Point c = P(a.X, a.Y);
                dc.DrawEllipse(rojo, null, c, 4, 4);
                Texto(dc, a.Id + " " + a.Nombre, c + new Vector(30, -12), 0, 12, rojo, true);
            }

            if (detalle != null) DibujarDetalle(dc, l, detalle, P);
        }
        return Imagen.Png(visual, ancho, alto);
    }

    private static void DibujarDetalle(DrawingContext dc, LecturaDibujo l, DetalleIa d, Func<double, double, Point> P)
    {
        var tramos = l.Tramos.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var verde = new SolidColorBrush(Color.FromRgb(0, 140, 0));
        var magenta = new SolidColorBrush(Color.FromRgb(200, 0, 200));
        foreach (LargoIa largo in d.Largos)
        {
            if (!tramos.TryGetValue(largo.Tramo, out TramoLeido? t)) continue;
            Point a = P(t.Inicio[0], t.Inicio[1]), b = P(t.Fin[0], t.Fin[1]);
            Vector v = b - a; v.Normalize();
            Texto(dc, largo.Texto, a + (b - a) / 2 - new Vector(-v.Y, v.X) * 22, 0, 14, verde, true);
        }
        foreach (UbicacionIa u in d.Ubicaciones)
        {
            if (!tramos.TryGetValue(u.Tramo, out TramoLeido? t)) continue;
            Point a = P(t.Inicio[0], t.Inicio[1]), b = P(t.Fin[0], t.Fin[1]);
            Point punto = a + (b - a) * Math.Clamp(u.En, 0, 1);
            dc.DrawEllipse(magenta, null, punto, 3, 3);
            Texto(dc, u.Texto, punto + new Vector(10, -10), 0, 12, magenta, true);
        }
        foreach (CotaAccesorioIa c in d.Accesorios)
        {
            AccesorioLeido? a = l.Accesorios.FirstOrDefault(x => string.Equals(x.Id, c.Accesorio, StringComparison.OrdinalIgnoreCase));
            if (a == null) continue;
            Vector hacia = c.Hacia.Trim().ToLowerInvariant() switch { "arriba" => new(0, -1), "abajo" => new(0, 1), "izquierda" => new(-1, 0), "derecha" => new(1, 0), _ => new(0, 0) };
            Point punto = P(a.X, a.Y);
            if (hacia.Length > 0) dc.DrawLine(new Pen(magenta, 1.4) { DashStyle = new DashStyle([3, 3], 0) }, punto, punto + hacia * 40);
            Texto(dc, c.Texto, punto + hacia * 46, 0, 12, magenta, true);
        }
    }

    private static void Texto(DrawingContext dc, string texto, Point c, double giro, double px, Brush tinta, bool fondo)
    {
        var f = new FormattedText(texto, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), px, tinta, 1.0);
        dc.PushTransform(new RotateTransform(-giro, c.X, c.Y));
        if (fondo) dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(c.X - f.Width / 2 - 1, c.Y - f.Height / 2, f.Width + 2, f.Height));
        dc.DrawText(f, new Point(c.X - f.Width / 2, c.Y - f.Height / 2));
        dc.Pop();
    }
}
