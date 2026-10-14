namespace AutoKADN.CalcoIA;

// La hoja girada en el dibujo (CALCAR > Girar o ROTATE): la IA la lee derecha, como fue escaneada (los números escritos de
// lado se leen mal), y aquí se gira lo que leyó para que el plano salga como el usuario colocó la hoja. Giro en grados
// antihorarios (0, 90, 180, 270); coordenadas en píxeles de la hoja (origen arriba a la izquierda, y hacia abajo).
public static class Giro
{
    public static int Normalizar(int giro) => ((giro % 360) + 360) % 360 / 90 * 90;

    // Un punto de la hoja derecha (ancho × alto) a la hoja vista con el giro.
    public static (double X, double Y) Punto(double x, double y, int giro, double ancho, double alto) => Normalizar(giro) switch
    {
        90 => (y, ancho - x),
        180 => (ancho - x, alto - y),
        270 => (alto - y, x),
        _ => (x, y),
    };

    public static Rect Rectangulo(Rect r, int giro, double ancho, double alto)
    {
        var a = Punto(r.X, r.Y, giro, ancho, alto);
        var b = Punto(r.X1, r.Y1, giro, ancho, alto);
        int x0 = (int)Math.Round(Math.Min(a.X, b.X)), y0 = (int)Math.Round(Math.Min(a.Y, b.Y));
        return new Rect(x0, y0, (int)Math.Round(Math.Max(a.X, b.X)) - x0, (int)Math.Round(Math.Max(a.Y, b.Y)) - y0);
    }

    // Todo lo que leyó la IA, girado. Los textos giran con el dibujo; los lados abiertos y el «hacia» de la ubicación de
    // un accesorio cambian de nombre.
    public static Croquis Croquis(Croquis c, int giro, double ancho, double alto)
    {
        giro = Normalizar(giro);
        if (giro == 0) return c;
        double[] P(double[] q) { if (q.Length < 2) return q; var g = Punto(q[0], q[1], giro, ancho, alto); return [g.X, g.Y]; }
        double[][] L(double[][] puntos) => puntos.Select(P).ToArray();
        double[] Caja(double[] k)
        {
            if (k.Length < 4) return k;
            var a = Punto(k[0], k[1], giro, ancho, alto);
            var b = Punto(k[2], k[3], giro, ancho, alto);
            return [Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)];
        }
        string[] lados = ["arriba", "izquierda", "abajo", "derecha"]; // en sentido antihorario
        string Lado(string l)
        {
            int i = Array.IndexOf(lados, l.Trim().ToLowerInvariant());
            return i < 0 ? l : lados[(i + giro / 90) % 4];
        }
        var r = new Croquis
        {
            Manzana = L(c.Manzana),
            Predios = c.Predios.Select(L).ToArray(),
            Construcciones = c.Construcciones.Select(L).ToArray(),
            Casas = c.Casas.Select(k => new CasaCroquis { Caja = Caja(k.Caja), Rotulo = k.Rotulo, Techo = k.Techo, EnConstruccion = k.EnConstruccion }).ToList(),
            Cercas = c.Cercas.Select(L).ToArray(),
            Textos = c.Textos.Select(t => { var g = Punto(t.X, t.Y, giro, ancho, alto); return new TextoCroquis { Texto = t.Texto, X = g.X, Y = g.Y, Giro = ((t.Giro + giro) % 180 + 180) % 180 }; }).ToList(),
            Vecinos = c.Vecinos.Select(v => new VecinoCroquis { Borde = L(v.Borde) }).ToList(),
            Indicadores = c.Indicadores.Select(i => { var g = Punto(i.X, i.Y, giro, ancho, alto); return new IndicadorCroquis { Numero = i.Numero, Diametro = i.Diametro, Viviendas = i.Viviendas, X = g.X, Y = g.Y }; }).ToList(),
            Vias = c.Vias.Select(v => { var g = Punto(v.X, v.Y, giro, ancho, alto); return new ViaCroquis { Nombre = v.Nombre, X = g.X, Y = g.Y, Giro = v.Giro == null ? null : ((v.Giro.Value + giro) % 180 + 180) % 180 }; }).ToList(),
            Tuberias = c.Tuberias.Select(t => new TuberiaCroquis { Tipo = t.Tipo, Diametro = t.Diametro, Puntos = L(t.Puntos), Cerrada = t.Cerrada, Cotas = t.Cotas, Diametros = t.Diametros }).ToList(),
            Accesorios = c.Accesorios.Select(a =>
            {
                var g = Punto(a.X, a.Y, giro, ancho, alto);
                UbicacionAccesorio? u = a.Ubicacion == null ? null
                    : new UbicacionAccesorio { Texto = a.Ubicacion.Texto, Hacia = a.Ubicacion.Hacia == null ? null : Lado(a.Ubicacion.Hacia) };
                return new AccesorioCroquis { Nombre = a.Nombre, X = g.X, Y = g.Y, Diametro = a.Diametro, Ubicacion = u };
            }).ToList(),
            Abierta = c.Abierta?.Select(Lado).ToArray(),
            Superficie = c.Superficie,
            Cuadro = c.Cuadro,
            CuadroAccesorios = c.CuadroAccesorios,
            Dudas = c.Dudas,
        };
        return r;
    }
}
