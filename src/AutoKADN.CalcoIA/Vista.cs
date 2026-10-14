using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AutoKADN.CalcoIA;

// Vista previa del plano enderezado (PNG): parecida a lo que dejará el plugin en el layout, para que la IA compare con la
// hoja antes de entregar. Cotas como en COTAK (a offsetCota de la tubería, hacia afuera de la manzana, texto horizontal).
public static class Vista
{
    internal const double PxPorUnidad = 7.0;
    internal const double Margen = 10;   // px alrededor del marco

    public static byte[] Dibujar(Plano plano, Parametros p, TamanosPedido tamanos)
    {
        Marco m = p.Marco;
        int ancho = (int)Math.Ceiling(m.Ancho * PxPorUnidad + 2 * Margen), alto = (int)Math.Ceiling(m.Alto * PxPorUnidad + 2 * Margen);
        Point P(double x, double y) => new(Margen + (x - m.X0) * PxPorUnidad, Margen + (m.Y1 - y) * PxPorUnidad);
        Point Q(double[] q) => P(q[0], q[1]);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, ancho, alto));
            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new System.Windows.Rect(P(m.X0, m.Y1), P(m.X1, m.Y0)));

            var negro = new Pen(Brushes.Black, 1.2);
            var gris = new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 90)), 1);
            var azul = new Pen(Brushes.Blue, 1.6);
            var punteada = new Pen(Brushes.Blue, 1.4) { DashStyle = new DashStyle([6, 4], 0) };
            var verde = new Pen(new SolidColorBrush(Color.FromRgb(0, 150, 0)), 0.8);
            var fina = new Pen(new SolidColorBrush(Color.FromRgb(60, 60, 60)), 0.8);
            foreach (LineaPlano l in plano.Lineas)
            {
                if (l.Tipo == "cerca") { Cerca(dc, l, Q); continue; }
                Pen lapiz = l.Tipo switch { "tuberia_nueva" => azul, "tuberia_existente" => punteada, "troncal_nueva" => new Pen(Brushes.Red, 1.6), "troncal_existente" => new Pen(Brushes.Red, 1.4) { DashStyle = new DashStyle([6, 4], 0) }, "bordillo" => fina, "vecino" => gris, "predio" or "construccion" => gris, "casa" => fina, _ => negro };
                var figura = new StreamGeometry();
                using (StreamGeometryContext g = figura.Open())
                {
                    g.BeginFigure(Q(l.Puntos[0]), false, l.Cerrada);
                    foreach (double[] q in l.Puntos.Skip(1)) g.LineTo(Q(q), true, false);
                }
                dc.DrawGeometry(null, lapiz, figura);
            }

            double altoCota = 2.5 * tamanos.Cotas / 0.055;
            foreach (CotaPlano c in plano.Cotas)
            {
                double dx = c.B[0] - c.A[0], dy = c.B[1] - c.A[1], largo = Math.Sqrt(dx * dx + dy * dy);
                if (largo < 1e-6) continue;
                double nx = -dy / largo, ny = dx / largo, mx = (c.A[0] + c.B[0]) / 2, my = (c.A[1] + c.B[1]) / 2;
                if (c.Lado != null) { if ((c.Lado[0] - mx) * nx + (c.Lado[1] - my) * ny < 0) { nx = -nx; ny = -ny; } }
                else if ((plano.CentroManzana[0] - mx) * nx + (plano.CentroManzana[1] - my) * ny > 0) { nx = -nx; ny = -ny; }
                double o = p.OffsetCota;
                Point a = P(c.A[0] + nx * o, c.A[1] + ny * o), b = P(c.B[0] + nx * o, c.B[1] + ny * o);
                dc.DrawLine(verde, P(c.A[0], c.A[1]), a);
                dc.DrawLine(verde, P(c.B[0], c.B[1]), b);
                dc.DrawLine(verde, a, b);
                Texto(dc, c.Texto, mx + nx * o, my + ny * o, 0, altoCota, Brushes.Green, P, fondo: true);
            }

            foreach (TextoPlano t in plano.Textos)
                Texto(dc, t.Texto, t.X, t.Y, t.Giro, (t.Tipo == "vial" ? tamanos.Vial : tamanos.Predial) * (t.Escala ?? 1), t.Tipo == "vial" ? Brushes.DarkSlateBlue : Brushes.Black, P, fondo: false);

            // Notas con flecha (el material de un empalme).
            foreach (NotaPlano n in plano.Notas)
            {
                string[] renglones = n.Texto.Split('\n');
                double altoNota = tamanos.Predial * 0.8;
                for (int k = 0; k < renglones.Length; k++) Texto(dc, renglones[k], n.X + renglones[k].Length * 0.33 * altoNota, n.Y - (k + 0.5) * 1.5 * altoNota, 0, altoNota, Brushes.Black, P, fondo: false);
                dc.DrawLine(negro, P(n.X, n.Y), P(n.Flecha[0], n.Flecha[1]));
            }

            foreach (IndicadorPlano i in plano.Indicadores)
            {
                double r = p.RadioIndicador;
                dc.DrawEllipse(null, negro, P(i.X, i.Y), r * PxPorUnidad, r * PxPorUnidad);
                dc.DrawLine(negro, P(i.X - r, i.Y), P(i.X + r, i.Y));
                Texto(dc, i.Numero, i.X, i.Y + r * 0.45, 0, 4.8, Brushes.Black, P, fondo: false);
                Texto(dc, "Ø" + i.Diametro + "\"", i.X, i.Y - r * 0.45, 0, 2.6, Brushes.Black, P, fondo: false);
                if (!string.IsNullOrEmpty(i.Viviendas)) Texto(dc, i.Viviendas!, i.X, i.Y - r - p.SeparacionViviendas, 0, tamanos.Predial, Brushes.Black, P, fondo: false);
            }

            var rojo = new SolidColorBrush(Color.FromRgb(210, 0, 0));
            foreach (AccesorioPlano a in plano.Accesorios)
            {
                Point c = P(a.X, a.Y);
                double rad = a.Direccion * Math.PI / 180;
                dc.DrawEllipse(rojo, null, c, 3.2, 3.2);
                dc.DrawLine(new Pen(rojo, 1.6), c, new Point(c.X + Math.Cos(rad) * 12, c.Y - Math.Sin(rad) * 12));
                string sigla = a.Nombre switch { "TAPON" => "Tp", "TEE" => "T", "UNION" => "U", "REDUCCION" => "R", "VALVULA" => "V", "SILLETA" => "S", _ => a.Nombre[..1] };
                var f = new FormattedText(sigla, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 11, rojo, 1.0);
                dc.DrawText(f, new Point(c.X + 4, c.Y + 2));
            }
        }
        return Imagen.Png(visual, ancho, alto);
    }

    // Cerca «x—x—x»: la línea cortada con una x en cada hueco (como linea_X_linea).
    private static void Cerca(DrawingContext dc, LineaPlano l, Func<double[], Point> Q)
    {
        var lapiz = new Pen(Brushes.Black, 1.0);
        for (int s = 0; s + 1 < l.Puntos.Count; s++)
        {
            Point a = Q(l.Puntos[s]), b = Q(l.Puntos[s + 1]);
            Vector d = b - a;
            double largo = d.Length;
            if (largo < 1) continue;
            d /= largo;
            const double paso = 22, hueco = 9, brazo = 2.6;
            for (double t = 0; t < largo; t += paso)
            {
                double fin = Math.Min(largo, t + paso - hueco);
                dc.DrawLine(lapiz, a + d * t, a + d * fin);
                if (fin + hueco / 2 >= largo) break;
                Point c = a + d * (fin + hueco / 2);
                dc.DrawLine(lapiz, c + new Vector(-brazo, -brazo), c + new Vector(brazo, brazo));
                dc.DrawLine(lapiz, c + new Vector(-brazo, brazo), c + new Vector(brazo, -brazo));
            }
        }
    }

    private static void Texto(DrawingContext dc, string texto, double x, double y, double giro, double alto, Brush tinta, Func<double, double, Point> P, bool fondo)
    {
        var f = new FormattedText(texto, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Math.Max(6, alto * PxPorUnidad * 1.05), tinta, 1.0);
        Point c = P(x, y);
        dc.PushTransform(new RotateTransform(-giro, c.X, c.Y));
        if (fondo) dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(c.X - f.Width / 2 - 1, c.Y - f.Height / 2, f.Width + 2, f.Height));
        dc.DrawText(f, new Point(c.X - f.Width / 2, c.Y - f.Height / 2));
        dc.Pop();
    }
}
