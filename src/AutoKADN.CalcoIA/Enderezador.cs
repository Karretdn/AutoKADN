using System.Globalization;

namespace AutoKADN.CalcoIA;

public sealed class ResultadoEnderezado
{
    public Plano Plano { get; set; } = new();
    public List<string> Avisos { get; } = [];
    public List<string> Resumen { get; } = [];
    public AnilloRegistrado? Registro { get; set; }   // lo que este anillo deja para completar las hojas vecinas
}

// Un anillo ya calcado, para completar su red en las hojas de los anillos vecinos (donde se ve como red existente y el
// croquis solo muestra un pedazo): qué lados de su manzana llevan tubería nueva y qué vía da a cada lado (para saber
// cómo está girado en la otra hoja). Lados: norte, este, sur, oeste, en la orientación de su propia hoja. Desde/hasta:
// qué parte del lado cubre, de 0 a 1 en sentido horario (norte de oeste a este, este de norte a sur…).
public sealed class AnilloRegistrado
{
    public string Numero { get; set; } = string.Empty;
    public string Diametro { get; set; } = string.Empty;
    public List<LadoConTuberia> Lados { get; set; } = [];
    public Dictionary<string, string> Vias { get; set; } = [];
}

public sealed class LadoConTuberia
{
    public string Lado { get; set; } = string.Empty;
    public double Desde { get; set; }
    public double Hasta { get; set; } = 1;
}

// Croquis (píxeles de la hoja, como se ve) → Plano (unidades del dibujo, como lo dibuja AutoKADN). Aquí viven las reglas
// de composición de CALCO-IA.md: 90°, líneas de predio alineadas, vías al doble (los vecinos se alejan lo que mide la vía),
// todo el alto del marco con estirado en X hasta estirarMaximo, tubería a `separacion` de la cara de manzana más cercana,
// cruce de vía de 3/4" con sus reducciones (regla 10), accesorios orientados según la tubería y textos sin montarse.
public static class Enderezador
{
    private const double Alinear = 12;   // px: líneas de predio casi alineadas a ambos lados de una división se unen
    private const double Empalmar = 40;  // px: el extremo de una línea de predio se lleva a la línea perpendicular cercana

    // recorte: la zona de la hoja que eligió el usuario (px); lo que el croquis corta en su borde sigue hasta el marco.
    // anillo: el número del layout (ANILLO n DETALLE), para el indicador si el croquis no lo trae.
    // diametroLayout: el del título del layout; manda para el indicador que se agrega y se avisa si la tubería no coincide.
    // registro: anillos ya calcados del mismo proyecto (su red completa la de las manzanas vecinas que se ven en esta hoja).
    public static ResultadoEnderezado Enderezar(Croquis croquis, Parametros p, TamanosPedido tamanos, Rect? recorte = null, string? anillo = null, string? diametroLayout = null,
        IReadOnlyDictionary<string, AnilloRegistrado>? registro = null)
    {
        var r = new ResultadoEnderezado();
        if (croquis.Manzana.Length < 3) throw new ArgumentException("falta el contorno de la manzana (manzana: al menos 3 puntos)");

        // ── 1. Manzana a 90° ──
        List<Borde> manzana = Ortogonal(Puntos(croquis.Manzana), cerrada: true, fusionar: true, duenio: -1);
        List<P2> verticesManzana = Vertices(manzana, cerrada: true, null);
        double mx0 = verticesManzana.Min(v => v.X), mx1 = verticesManzana.Max(v => v.X);
        double my0 = verticesManzana.Min(v => v.Y), my1 = verticesManzana.Max(v => v.Y);
        double tamano = Math.Min(mx1 - mx0, my1 - my0);
        double acercar = 0.22 * tamano;  // px: hasta aquí una tubería se considera "a lo largo" de una cara

        // Lo que el croquis corta en el borde de la hoja sigue más allá: el lado de la manzana que llega al borde del
        // recorte (o que la IA marca en «abierta») no se cierra y llega hasta el borde del marco.
        double tolBorde = recorte is Rect rb ? 0.03 * Math.Min(rb.Ancho, rb.Alto) : 0;
        bool AlBorde(double v, double borde) => recorte != null && Math.Abs(v - borde) <= tolBorde;
        var abiertos = new HashSet<Lado>();
        foreach (string a in croquis.Abierta ?? [])
            switch (a.Trim().ToLowerInvariant())
            {
                case "arriba" or "norte": abiertos.Add(Lado.Norte); break;
                case "abajo" or "sur": abiertos.Add(Lado.Sur); break;
                case "izquierda" or "oeste": abiertos.Add(Lado.Oeste); break;
                case "derecha" or "este": abiertos.Add(Lado.Este); break;
            }
        // Punta de una red existente que sigue más allá de lo que muestra la hoja (hasta el marco): la que sale por el borde
        // de la zona elegida, o la que queda suelta (no toca otra tubería ni un accesorio) alejándose de la manzana, como la
        // troncal que el croquis deja de dibujar antes del borde. Esas puntas no cuentan para la escala en su dirección.
        var siguen = new Dictionary<TuberiaCroquis, (bool inicio, bool fin)>();
        bool Sigue(TuberiaCroquis t, bool inicio)
        {
            var pts = t.Puntos.Where(x => x.Length >= 2).Select(x => new P2(x[0], x[1])).ToList();
            if (pts.Count < 2) return false;
            P2 punta = inicio ? pts[0] : pts[^1], previo = inicio ? pts[1] : pts[^2];
            if (recorte is Rect rq && LlegaAlBorde(punta, previo, rq, tolBorde)) return true;
            double cerca = 0.04 * tamano;
            foreach (TuberiaCroquis otra in croquis.Tuberias.Where(o => o != t))
            {
                var op = otra.Puntos.Where(x => x.Length >= 2).Select(x => new P2(x[0], x[1])).ToList();
                for (int i = 0; i + 1 < op.Count; i++) if (DistanciaSegmento(punta, op[i], op[i + 1]) <= cerca) return false;
                if (op.Count == 1 && Math.Abs(op[0].X - punta.X) + Math.Abs(op[0].Y - punta.Y) <= cerca) return false;
            }
            if (croquis.Accesorios.Any(a => Math.Abs(a.X - punta.X) + Math.Abs(a.Y - punta.Y) <= cerca)) return false;
            double dx = punta.X - previo.X, dy = punta.Y - previo.Y;
            if (Math.Abs(dy) >= Math.Abs(dx)) return dy > 0 ? punta.Y > my1 : punta.Y < my0;
            return dx > 0 ? punta.X > mx1 : punta.X < mx0;
        }

        // La red existente no pasa del borde de la zona elegida: lo que el croquis muestra más allá se corta ahí (y luego
        // sigue hasta el marco).
        if (recorte is Rect rz)
            foreach (TuberiaCroquis t in croquis.Tuberias.Where(t => string.Equals(t.Tipo, "existente", StringComparison.OrdinalIgnoreCase)))
                t.Puntos = t.Puntos.Where(q => q.Length >= 2).Select(q => new[] { Math.Clamp(q[0], rz.X, rz.X1), Math.Clamp(q[1], rz.Y, rz.Y1) }).ToArray();
        foreach (TuberiaCroquis t in croquis.Tuberias.Where(t => string.Equals(t.Tipo, "existente", StringComparison.OrdinalIgnoreCase) && !t.Cerrada))
            siguen[t] = (Sigue(t, true), Sigue(t, false));
        if (recorte is Rect rc)
        {
            if (AlBorde(my0, rc.Y)) abiertos.Add(Lado.Norte);
            if (AlBorde(my1, rc.Y1)) abiertos.Add(Lado.Sur);
            if (AlBorde(mx0, rc.X)) abiertos.Add(Lado.Oeste);
            if (AlBorde(mx1, rc.X1)) abiertos.Add(Lado.Este);
        }

        // ── 2. Vecinos: lado, borde que da a la manzana y cuánto se alejan (vía al doble) ──
        var vecinos = new List<Vecino>();
        foreach (VecinoCroquis v in croquis.Vecinos)
        {
            if (v.Borde.Length < 2) continue;
            var vecino = new Vecino { Original = Puntos(v.Borde), Indice = vecinos.Count };
            vecino.Bordes = Ortogonal(vecino.Original, cerrada: false, fusionar: true, duenio: vecino.Indice);
            P2 centro = new(vecino.Original.Average(q => q.X), vecino.Original.Average(q => q.Y));
            vecino.Lado = LadoDe(centro, mx0, my0, mx1, my1);
            bool horizontal = vecino.Lado is Lado.Norte or Lado.Sur;
            Borde? frente = vecino.Bordes.Where(b => b.H == horizontal).OrderByDescending(b => b.Largo).FirstOrDefault();
            Borde? cara = manzana.Where(b => b.H == horizontal && EnLado(b, vecino.Lado, mx0, my0, mx1, my1)).OrderByDescending(b => b.Largo).FirstOrDefault();
            if (frente == null || cara == null) { r.Avisos.Add($"no se entendió de qué lado está el vecino {vecino.Indice + 1}; se omitió"); continue; }
            // Simetría con la vía: un lado de la vecina que cae cerca del borde de la manzana del anillo (al otro lado de la
            // calle) se alinea con él; casi siempre las manzanas están parejas a lado y lado de la vía.
            double alinearVecino = Math.Max(40, p.SimetriaVecinos * tamano);
            foreach (Borde lado in vecino.Bordes.Where(b => b.H != horizontal))
            {
                double b0 = horizontal ? mx0 : my0, b1 = horizontal ? mx1 : my1;
                if (Math.Abs(lado.C - b0) <= alinearVecino && Math.Abs(lado.C - b0) <= Math.Abs(lado.C - b1)) lado.C = b0;
                else if (Math.Abs(lado.C - b1) <= alinearVecino) lado.C = b1;
            }
            vecino.Frente = frente.C;
            vecino.Via = frente.C - cara.C;        // lo que mide la vía en el croquis (con signo: hacia afuera)
            vecino.Corrimiento = vecino.Via;       // para empezar, al doble; se ajusta con la escala (paso 3)
            vecinos.Add(vecino);
        }

        // Dueño de un punto: la manzana (-1) o el vecino más cercano (se corre con él).
        int Duenio(P2 q)
        {
            if (DentroDe(q, verticesManzana)) return -1;
            double mejor = DistanciaPoligono(q, verticesManzana, cerrada: true);
            int duenio = -1;
            foreach (Vecino v in vecinos)
            {
                double d = DistanciaPoligono(q, v.Original, cerrada: false);
                if (d < mejor) { mejor = d; duenio = v.Indice; }
            }
            return duenio;
        }
        P2 Correr(P2 q, int duenio)
        {
            Vecino? v = vecinos.FirstOrDefault(x => x.Indice == duenio);
            if (v == null) return q;
            return EsHorizontalLado(v.Lado) ? q with { Y = q.Y + v.Corrimiento } : q with { X = q.X + v.Corrimiento };
        }

        // ── 3. Escala: todo el alto del marco, estirado en X hasta estirarMaximo. Cada vía queda del doble de lo que mide
        //       en el croquis, pero entre viaMinima y viaMaxima unidades del dibujo (una calle que en el croquis ya es
        //       ancha no debe quedar como la manzana); como la escala depende de las vías y al revés, se ajusta en vueltas ──
        Vecino? norte = vecinos.FirstOrDefault(v => v.Lado == Lado.Norte), sur = vecinos.FirstOrDefault(v => v.Lado == Lado.Sur);
        Vecino? oeste = vecinos.FirstOrDefault(v => v.Lado == Lado.Oeste), este = vecinos.FirstOrDefault(v => v.Lado == Lado.Este);
        double espacioVecino = 2 * p.RadioIndicador + 2.5;
        double extraVertical = p.Separacion + p.OffsetCota + p.NombreViaSobreCota + 1.5;
        double extraLateral = p.Separacion + p.OffsetCota + p.NombreViaLateral + 1.5;
        double extraArriba = norte != null ? espacioVecino : extraVertical, extraAbajo = sur != null ? espacioVecino : extraVertical;
        double extraIzq = oeste != null ? espacioVecino : extraLateral, extraDer = este != null ? espacioVecino : extraLateral;
        // Un lado abierto llega justo al borde del marco: sin margen ni espacio para vía o cotas.
        double margenArriba = p.Margen, margenAbajo = p.Margen, margenIzq = p.Margen, margenDer = p.Margen;
        if (abiertos.Contains(Lado.Norte) && norte == null) { extraArriba = 0; margenArriba = 0; }
        if (abiertos.Contains(Lado.Sur) && sur == null) { extraAbajo = 0; margenAbajo = 0; }
        if (abiertos.Contains(Lado.Oeste) && oeste == null) { extraIzq = 0; margenIzq = 0; }
        if (abiertos.Contains(Lado.Este) && este == null) { extraDer = 0; margenDer = 0; }
        Marco marco = p.Marco;
        double altoUtil = marco.Alto - margenArriba - margenAbajo - extraArriba - extraAbajo, anchoUtil = marco.Ancho - margenIzq - margenDer - extraIzq - extraDer;
        double arriba = 0, abajo = 0, izquierda = 0, derecha = 0, sx = 1, sy = 1;
        bool escalado = false;
        void Escalar()
        {
            arriba = norte != null ? norte.Frente + norte.Corrimiento : my0;
            abajo = sur != null ? sur.Frente + sur.Corrimiento : my1;
            izquierda = oeste != null ? oeste.Frente + oeste.Corrimiento : mx0;
            derecha = este != null ? este.Frente + este.Corrimiento : mx1;
            // Lo que es de la manzana principal y queda afuera de ella (redes, cruces, indicadores de anillos sin manzana
            // vecina en el croquis, accesorios) también tiene que caber en el marco. Lo que es de un vecino no: se
            // recorta al marco como su borde.
            var sueltos = new List<(P2 q, double margen, bool usaX, bool usaY)>();
            bool Propio(P2 q) => vecinos.Count == 0 || Duenio(q) == -1;
            // La tubería pegada a la manzana va a `separacion` de su cara y ya tiene su espacio en los márgenes: solo
            // cuenta la que está lejos (una red al otro lado de la calle sin la manzana vecina en el croquis).
            // Una red existente que sale por el borde de la zona elegida sigue hasta el marco: esa punta no cuenta para la
            // escala en la dirección en que sale.
            foreach (TuberiaCroquis t in croquis.Tuberias)
            {
                var pts = t.Puntos.Where(x => x.Length >= 2).Select(x => new P2(x[0], x[1])).ToList();
                siguen.TryGetValue(t, out var sigue);
                for (int i = 0; i < pts.Count; i++)
                {
                    P2 q = pts[i];
                    if (!Propio(q) || DistanciaPoligono(q, verticesManzana, cerrada: true) <= acercar) continue;
                    bool punta = (i == 0 && sigue.inicio) || (i == pts.Count - 1 && sigue.fin);
                    P2 previo = i == 0 ? (pts.Count > 1 ? pts[1] : q) : pts[i - 1];
                    bool vertical = Math.Abs(q.Y - previo.Y) >= Math.Abs(q.X - previo.X);
                    sueltos.Add((q, 0, !(punta && !vertical), !(punta && vertical)));
                }
            }
            foreach (IndicadorCroquis i in croquis.Indicadores)
            {
                var q = new P2(i.X, i.Y);
                if (!DentroDe(q, verticesManzana) && Propio(q)) sueltos.Add((q, p.RadioIndicador + 1, true, true));
            }
            foreach (AccesorioCroquis a in croquis.Accesorios)
            {
                var q = new P2(a.X, a.Y);
                if (Propio(q) && DistanciaPoligono(q, verticesManzana, cerrada: true) > acercar) sueltos.Add((q, 0, true, true));
            }
            foreach ((P2 q, double margen, bool usaX, bool usaY) in sueltos)
            {
                double mx = escalado ? margen / sx : 0, my = escalado ? margen / sy : 0;
                if (usaY) { arriba = Math.Min(arriba, q.Y - my); abajo = Math.Max(abajo, q.Y + my); }
                if (usaX) { izquierda = Math.Min(izquierda, q.X - mx); derecha = Math.Max(derecha, q.X + mx); }
            }
            // Primero cabe entero sin deformar; luego se estira la dirección que sobra (a lo ancho o a lo alto) hasta
            // llenar el marco, sin pasar de estirarMaximo. Una manzana larga y angosta se estira a lo alto.
            double llenaAlto = altoUtil / (abajo - arriba), llenaAncho = anchoUtil / (derecha - izquierda);
            double justo = Math.Min(llenaAlto, llenaAncho);
            sy = Math.Min(llenaAlto, p.EstirarMaximo * justo);
            sx = Math.Min(llenaAncho, p.EstirarMaximo * justo);
            // Estirar no puede alargar la manzana más de lo que ya es en el croquis ni más que el propio marco: una
            // manzana ya larga y angosta no se estira a lo largo (quedaría aplastada).
            double anchoM = Math.Max(1, mx1 - mx0), altoM = Math.Max(1, my1 - my0);
            double limite = Math.Max(Math.Max(anchoM / altoM, altoM / anchoM), anchoUtil / altoUtil);
            double forma = anchoM * sx / (altoM * sy);
            if (forma > limite && sx > justo) sx = Math.Max(justo, limite * altoM * sy / anchoM);
            else if (forma < 1 / limite && sy > justo) sy = Math.Max(justo, anchoM * sx / (limite * altoM));
            escalado = true;
        }
        for (int vuelta = 0; vuelta < 12; vuelta++)
        {
            Escalar();
            bool cambio = false;
            foreach (Vecino v in vecinos)
            {
                double s = EsHorizontalLado(v.Lado) ? sy : sx, via = Math.Abs(v.Via);
                double ancho = Math.Max(p.ViaMinima, Math.Min(p.ViaMaxima, 2 * via * s));   // unidades del dibujo
                double corrimiento = Math.Sign(v.Via) * (ancho / s - via);
                if (Math.Abs(corrimiento - v.Corrimiento) > 0.5) cambio = true;
                v.Corrimiento = corrimiento;
            }
            if (!cambio) break;
        }
        Escalar();
        Escalar(); // con los márgenes de los indicadores sueltos ya en la escala final
        foreach (Vecino v in vecinos)
            foreach (Borde b in v.Bordes) { if (EsHorizontalLado(v.Lado) == b.H) b.C += v.Corrimiento; else { b.A0 += v.Corrimiento; b.A1 += v.Corrimiento; } }
        double holguraX = anchoUtil - (derecha - izquierda) * sx, holguraY = altoUtil - (abajo - arriba) * sy;
        // Lo que sobra se reparte a los dos lados, salvo que un lado sea abierto: ese queda pegado al marco.
        double sobraIzq = abiertos.Contains(Lado.Oeste) ? 0 : abiertos.Contains(Lado.Este) ? holguraX : holguraX / 2;
        double sobraArriba = abiertos.Contains(Lado.Norte) ? 0 : abiertos.Contains(Lado.Sur) ? holguraY : holguraY / 2;
        double ox = marco.X0 + margenIzq + extraIzq + sobraIzq, oy = marco.Y1 - margenArriba - extraArriba - sobraArriba;
        double X(double x) => Math.Round(ox + (x - izquierda) * sx, 4);
        double Y(double y) => Math.Round(oy - (y - arriba) * sy, 4);
        double[] D(P2 q) => [X(q.X), Y(q.Y)];
        r.Resumen.Add(FormattableString.Invariant($"Escala {sx:0.####} (X) × {sy:0.####} (Y) u/px; estirado ×{sx / sy:0.00}"));

        Plano plano = r.Plano;
        plano.CentroManzana = [X((mx0 + mx1) / 2), Y((my0 + my1) / 2)];
        if (abiertos.Count == 0) plano.Lineas.Add(new LineaPlano { Tipo = "manzana", Puntos = verticesManzana.Select(D).ToList(), Cerrada = true });
        else
        {
            foreach (List<double[]> tramo in ManzanaAbierta(verticesManzana, abiertos, mx0, my0, mx1, my1, D, marco))
                plano.Lineas.Add(new LineaPlano { Tipo = "manzana", Puntos = tramo });
            r.Resumen.Add("Manzana abierta hacia " + string.Join(", ", abiertos.Select(l => l.ToString().ToLowerInvariant())) + " (el croquis la corta en el borde de la hoja): sigue hasta el marco");
        }

        // ── 4. Predios: a 90°, alineados y empalmados ──
        double alinear = Math.Max(Alinear, 0.015 * tamano);
        List<Borde> predios = croquis.Predios.Where(s => s.Length >= 2).Select(s => Ortogonal(Puntos(s).Take(2).ToList(), false, false, -1)[0])
            // Una «línea de predio» encima del borde de la manzana es el mismo borde: sobra.
            .Where(a => !manzana.Any(m => m.H == a.H && Math.Abs(m.C - a.C) < alinear && a.Max > m.Min && a.Min < m.Max))
            .ToList();
        foreach (Borde a in predios)
            foreach (Borde b in predios)
                if (a != b && a.H == b.H && Math.Abs(a.C - b.C) < alinear && (a.Max < b.Min + 5 || b.Max < a.Min + 5)) { double m = (a.C + b.C) / 2; a.C = m; b.C = m; }
        List<Borde> perpendiculares = manzana.Concat(predios).ToList();
        double empalmar = Math.Max(Empalmar, 0.05 * tamano);
        foreach (Borde a in predios)
        {
            // Los extremos van a la línea perpendicular cercana y nunca afuera de la manzana.
            double minimo = a.H ? mx0 : my0, maximo = a.H ? mx1 : my1;
            a.A0 = Math.Max(minimo, Math.Min(maximo, Empalme(a, a.A0, perpendiculares, empalmar)));
            a.A1 = Math.Max(minimo, Math.Min(maximo, Empalme(a, a.A1, perpendiculares, empalmar)));
            P2 q0 = a.H ? new P2(a.A0, a.C) : new P2(a.C, a.A0), q1 = a.H ? new P2(a.A1, a.C) : new P2(a.C, a.A1);
            plano.Lineas.Add(new LineaPlano { Tipo = "predio", Puntos = [D(q0), D(q1)] });
        }
        foreach (double[][] c in croquis.Construcciones.Where(c => c.Length >= 3))
            plano.Lineas.Add(new LineaPlano { Tipo = "construccion", Puntos = Vertices(Ortogonal(Puntos(c), true, true, -1), true, null).Select(D).ToList(), Cerrada = true });

        // ── 5. Vecinos: su borde corrido, recortado al marco ──
        foreach (Vecino v in vecinos)
        {
            List<P2> borde = Vertices(v.Bordes, cerrada: false, null);
            foreach (List<double[]> tramo in RecortarAlMarco(borde.Select(D).ToList(), marco, 0.5))
                plano.Lineas.Add(new LineaPlano { Tipo = "vecino", Puntos = tramo });
        }

        // ── 6. Tuberías: cada tramo a `separacion` de la cara que acompaña ──
        List<Borde> caras = manzana.Concat(vecinos.SelectMany(v => v.Bordes)).ToList();
        var tubos = new List<Tubo>();
        foreach (TuberiaCroquis t in croquis.Tuberias)
        {
            if (t.Puntos.Length < 2) continue;
            bool cruce = string.Equals(t.Tipo, "cruce", StringComparison.OrdinalIgnoreCase);
            bool nueva = cruce || string.Equals(t.Tipo, "nueva", StringComparison.OrdinalIgnoreCase);
            List<P2> original = Puntos(t.Puntos);
            List<P2> corridos = original.Select(q => nueva && !cruce ? q : Correr(q, Duenio(q))).ToList();
            if (cruce) corridos = [original[0], Correr(original[^1], Duenio(original[^1]))];
            var tubo = new Tubo
            {
                Nueva = nueva, Cruce = cruce, Cerrada = t.Cerrada && !cruce && original.Count > 2,
                Diametro = Normalizar(t.Diametro), Cotas = t.Cotas, Original = original,
                Diametros = t.Diametros?.Select(Normalizar).ToArray(), Fuente = t,
            };
            tubo.Tramos = Ortogonal(corridos, tubo.Cerrada, fusionar: false, duenio: -1);
            if (cruce && tubo.Tramos.Count > 1) tubo.Tramos = [tubo.Tramos.OrderByDescending(b => b.Largo).First()];
            foreach (Borde tramo in tubo.Tramos)
            {
                tramo.CDib = tramo.H ? Y(tramo.C) : X(tramo.C);
                if (cruce) continue;
                // La cara que más acompaña al tramo a lo largo (no un escalón corto que solo se le acerca por una punta).
                Borde? cara = caras.Where(f => f.H == tramo.H && Math.Abs(f.C - tramo.C) < acercar && Solapa(f, tramo))
                                   .OrderBy(f => Math.Min(f.Max, tramo.Max) - Math.Max(f.Min, tramo.Min) >= 0.3 * tramo.Largo ? 0 : 1)
                                   .ThenBy(f => Math.Abs(f.C - tramo.C)).FirstOrDefault();
                if (cara == null) continue;
                tramo.Cara = cara;
                double afuera = tramo.C < cara.C ? -1 : 1;           // en píxeles (y hacia abajo)
                tramo.CDib = tramo.H ? Y(cara.C) - afuera * p.Separacion : X(cara.C) + afuera * p.Separacion;
            }
            tubo.X = X; tubo.Y = Y;
            tubos.Add(tubo);
        }

        // Cruces: cuelgan del vértice de la tubería nueva más cercano y llegan al vértice de la otra red, que se pone en su línea.
        foreach (Tubo cruce in tubos.Where(t => t.Cruce))
        {
            Borde eje = cruce.Tramos[0];
            Vertice? inicio = VerticeCercano(tubos.Where(t => !t.Cruce), cruce.Original[0], null);
            if (inicio != null)
            {
                P2 v = inicio.Tubo.Punto(inicio);
                eje.CDib = eje.H ? v.Y : v.X;
                cruce.InicioA = eje.H ? v.X : v.Y;
            }
            Vertice? fin = VerticeCercano(tubos.Where(t => !t.Cruce), cruce.Original[^1], inicio);
            if (fin != null)
            {
                foreach (Borde b in fin.Tubo.Incidentes(fin)) if (b.H == eje.H) { b.CDib = eje.CDib; b.Cara = null; }
                if (fin.Extremo != 0) fin.Tubo.FijarExtremo(fin.Extremo, eje.CDib, perpendicularA: eje.H);
                P2 v = fin.Tubo.Punto(fin);
                cruce.FinA = eje.H ? v.X : v.Y;
            }
            else r.Avisos.Add("un cruce no llega a otra red: revise que su punto final esté sobre la red existente");
        }

        // Extremos sueltos de tuberías que llegan a otra: se apoyan en ella.
        foreach (Tubo t in tubos.Where(t => !t.Cruce && !t.Cerrada))
            foreach (int extremo in new[] { -1, 1 })
            {
                if (t.ExtremoFijo(extremo)) continue;
                P2 q = t.PuntoExtremo(extremo);
                Borde propio = extremo < 0 ? t.Tramos[0] : t.Tramos[^1];
                foreach (Tubo otro in tubos.Where(o => o != t))
                    foreach ((P2 a, P2 b, Borde tramo) in otro.Segmentos())
                    {
                        if (tramo.H == propio.H) continue;
                        double perpendicular = tramo.H ? Math.Abs(q.Y - a.Y) : Math.Abs(q.X - a.X);
                        double a0 = tramo.H ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y), a1 = tramo.H ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
                        double sobre = tramo.H ? q.X : q.Y;
                        if (perpendicular > 6 || sobre < a0 - 1 || sobre > a1 + 1) continue;
                        t.FijarExtremo(extremo, tramo.CDib, perpendicularA: tramo.H);
                        goto siguiente;
                    }
                siguiente:;
            }

        // ── 6b. Red de anillos vecinos ya calcados: alrededor de su manzana, como existente ──
        if (registro != null && registro.Count > 0)
            CompletarVecinos(croquis, vecinos, registro, anillo, verticesManzana, mx0, my0, mx1, my1, tubos, p, X, Y, marco, plano, r);

        // Accesorio en la punta de la tubería nueva con su cota de ubicación (la unión que es la transición a la red
        // existente, a 1.4 m de la cara de la torre, o en 0.0 = alineada con ella): esa punta, y la existente que sigue
        // desde ahí, se llevan a ese punto. La cota va en metros y se pasa al dibujo con la escala de ese mismo tramo.
        AjustarPuntas(croquis, tubos, manzana.Concat(predios).ToList(), tamano, X, Y, r);

        // ── 7. Líneas de tubería, cotas, cruces (regla 10) y accesorios ──
        var accesorios = new List<AccesorioPlano>();
        string superficie = string.IsNullOrWhiteSpace(croquis.Superficie) ? p.Superficie : croquis.Superficie!.Trim().ToUpperInvariant();
        foreach (Tubo t in tubos.Where(t => !t.Cruce && !t.Reemplazada))
        {
            List<P2> puntos = t.Polilinea();
            if (!t.Nueva)
            {
                // La red existente se muestra hasta el borde del marco, como los vecinos.
                List<double[]> existente = puntos.Select(q => new[] { q.X, q.Y }).ToList();
                if (t.Cerrada) existente.Add(existente[0]);
                // La que sigue más allá de la hoja (sale por el borde o queda suelta alejándose de la manzana), hasta el marco.
                else if (existente.Count >= 2 && t.Fuente != null && siguen.TryGetValue(t.Fuente, out var sigue))
                {
                    if (sigue.inicio) Extender(existente, 0, 1, marco);
                    if (sigue.fin) Extender(existente, existente.Count - 1, existente.Count - 2, marco);
                }
                foreach (List<double[]> tramo in RecortarAlMarco(existente, marco, 0.5))
                    plano.Lineas.Add(new LineaPlano { Tipo = "tuberia_existente", Puntos = tramo });
                continue;
            }
            plano.Lineas.Add(new LineaPlano { Tipo = "tuberia_nueva", Puntos = puntos.Select(q => new[] { q.X, q.Y }).ToList(), Cerrada = t.Cerrada });
            for (int i = 0; i < t.Tramos.Count; i++)
            {
                string? texto = i < t.Cotas.Count ? t.Cotas[i] : null;
                (P2 a, P2 b) = t.Extremos(i);
                if (string.IsNullOrWhiteSpace(texto)) { r.Avisos.Add($"tramo nuevo sin cota (tubería de {t.Diametro}\", tramo {i + 1}): ¿es existente?"); continue; }
                plano.Cotas.Add(new CotaPlano { A = [a.X, a.Y], B = [b.X, b.Y], Texto = Cota(texto), Diametro = t.Diametro });
            }
        }
        foreach (Tubo c in tubos.Where(t => t.Cruce))
            ArmarCruce(c, p, plano, accesorios, superficie);

        foreach (AccesorioCroquis a in croquis.Accesorios)
        {
            string nombre = a.Nombre.Trim().ToUpperInvariant();
            Ubicacion? u = Ubicar(tubos, new P2(a.X, a.Y), 0.06 * tamano);
            if (u == null) { r.Avisos.Add($"el accesorio {nombre} no está cerca de ninguna tubería; se omitió"); continue; }
            if (accesorios.Any(x => x.Nombre == nombre && Math.Abs(x.X - u.Punto.X) < 2 && Math.Abs(x.Y - u.Punto.Y) < 2)) continue; // ya lo puso el cruce
            accesorios.Add(new AccesorioPlano
            {
                Nombre = nombre, X = u.Punto.X, Y = u.Punto.Y, Diametro = string.IsNullOrWhiteSpace(a.Diametro) ? u.Tubo.Diametro : Normalizar(a.Diametro),
                Superficie = superficie, Direccion = 0,
            });
        }
        foreach (AccesorioPlano a in accesorios) a.Direccion = Direccion(a, tubos, r);
        plano.Accesorios.AddRange(accesorios);

        // ── 8. Textos de predios ──
        foreach (TextoCroquis t in croquis.Textos.Where(t => !string.IsNullOrWhiteSpace(t.Texto)))
            plano.Textos.Add(new TextoPlano { Texto = t.Texto.Trim().ToUpperInvariant(), X = X(t.X), Y = Y(t.Y), Giro = Math.Abs(t.Giro - 90) < 30 ? 90 : 0, Tipo = "predial" });
        CentrarEnPredios(plano, marco, tamanos, r);

        // ── 9. Indicadores: el del anillo donde lo puso el croquis, sin que sus viviendas crucen una línea de predio;
        //       los de vecinos, justo detrás del borde que da a la manzana ──
        List<(P2 a, P2 b)> lineasDentro = plano.Lineas.Where(l => l.Tipo is "predio" or "manzana" or "construccion").SelectMany(l => Segmentos(l)).ToList();
        foreach (IndicadorCroquis ind in croquis.Indicadores)
        {
            P2 q = new(ind.X, ind.Y);
            int duenio = DentroDe(q, verticesManzana) || vecinos.Count == 0 ? -1 : Duenio(q);
            var salida = new IndicadorPlano { Numero = ind.Numero.Trim(), Diametro = Normalizar(ind.Diametro), Viviendas = string.IsNullOrWhiteSpace(ind.Viviendas) ? null : ind.Viviendas!.Trim().ToUpperInvariant() };
            Vecino? v = vecinos.FirstOrDefault(x => x.Indice == duenio);
            if (v == null)
            {
                var cajas = plano.Textos.Select(t => Caja(t, tamanos)).ToList();
                (salida.X, salida.Y) = UbicarIndicador(X(q.X), Y(q.Y), salida.Viviendas, p, tamanos, lineasDentro, cajas);
            }
            else
            {
                double frente = v.Frente + v.Corrimiento, dentro = 2 + p.RadioIndicador;
                switch (v.Lado)
                {
                    case Lado.Norte: salida.X = X(q.X); salida.Y = Y(frente) + dentro; break;
                    case Lado.Sur: salida.X = X(q.X); salida.Y = Y(frente) - dentro; break;
                    case Lado.Oeste: salida.X = X(frente) - dentro; salida.Y = Y(q.Y); break;
                    default: salida.X = X(frente) + dentro; salida.Y = Y(q.Y); break;
                }
            }
            plano.Indicadores.Add(salida);
        }

        // El indicador del anillo va siempre: si el croquis no lo trae, con el número del layout y el diámetro de la
        // tubería nueva, en medio de la manzana (corrido para no tapar líneas ni textos).
        if (!croquis.Indicadores.Any(i => DentroDe(new P2(i.X, i.Y), verticesManzana)) && !string.IsNullOrWhiteSpace(anillo))
        {
            string diametro = !string.IsNullOrWhiteSpace(diametroLayout) ? Normalizar(diametroLayout) : tubos.FirstOrDefault(t => t.Nueva && !t.Cruce)?.Diametro ?? "3/4";
            var propio = new IndicadorPlano { Numero = anillo!.Trim(), Diametro = diametro };
            var cajas = plano.Textos.Select(t => Caja(t, tamanos)).ToList();
            (propio.X, propio.Y) = UbicarIndicador(X((mx0 + mx1) / 2), Y((my0 + my1) / 2), null, p, tamanos, lineasDentro, cajas);
            plano.Indicadores.Add(propio);
            r.Resumen.Add($"Indicador del anillo {propio.Numero} Ø{diametro}\" agregado en la manzana (el croquis no lo trae)");
        }

        // ── 10. Nombres de vías: en la vía, fuera de las cotas. Una vía junto a la tubería nueva sin nombre en el croquis
        //        se rotula «VIA INTERNA - T.N.» ──
        var vias = croquis.Vias.Where(v => !string.IsNullOrWhiteSpace(v.Nombre)).ToList();
        foreach (Lado lado in new[] { Lado.Norte, Lado.Sur, Lado.Oeste, Lado.Este })
        {
            if (abiertos.Contains(lado) || vias.Any(v => LadoDe(new P2(v.X, v.Y), mx0, my0, mx1, my1) == lado)) continue;
            bool conTubo = tubos.Any(t => t.Nueva && t.Tramos.Any(b => b.Cara != null && b.Cara.Duenio == -1 && EnLado(b.Cara, lado, mx0, my0, mx1, my1)));
            if (!conTubo) continue;
            double fuera = 0.05 * tamano;
            P2 punto = lado switch
            {
                Lado.Norte => new((mx0 + mx1) / 2, my0 - fuera),
                Lado.Sur => new((mx0 + mx1) / 2, my1 + fuera),
                Lado.Oeste => new(mx0 - fuera, (my0 + my1) / 2),
                _ => new(mx1 + fuera, (my0 + my1) / 2),
            };
            vias.Add(new ViaCroquis { Nombre = p.ViaSinNombre, X = punto.X, Y = punto.Y });
        }
        foreach (ViaCroquis via in vias)
        {
            Lado lado = LadoDe(new P2(via.X, via.Y), mx0, my0, mx1, my1);
            Borde? cara = manzana.Where(b => b.H == EsHorizontalLado(lado) && EnLado(b, lado, mx0, my0, mx1, my1)).OrderByDescending(b => b.Largo).FirstOrDefault();
            if (cara == null) continue;
            bool conTubo = tubos.Any(t => t.Tramos.Any(b => b.Cara == cara));
            bool conCota = tubos.Any(t => t.Nueva && t.Tramos.Select((b, i) => (b, i)).Any(x => x.b.Cara == cara && x.i < t.Cotas.Count && !string.IsNullOrWhiteSpace(t.Cotas[x.i])));
            double limite = (conCota ? p.Separacion + p.OffsetCota : conTubo ? p.Separacion : 0);
            Vecino? v = vecinos.FirstOrDefault(x => x.Lado == lado);
            var texto = new TextoPlano { Texto = via.Nombre.Trim().ToUpperInvariant(), Tipo = "vial" };
            switch (lado)
            {
                case Lado.Norte:
                    texto.X = X((mx0 + mx1) / 2);
                    texto.Y = v != null ? (Y(cara.C) + limite + Y(v.Frente + v.Corrimiento) - LimiteVecino(v, tubos, p)) / 2 : Y(cara.C) + limite + p.NombreViaSobreCota;
                    break;
                case Lado.Sur:
                    texto.X = X((mx0 + mx1) / 2);
                    texto.Y = v != null ? (Y(cara.C) - limite + Y(v.Frente + v.Corrimiento) + LimiteVecino(v, tubos, p)) / 2 : Y(cara.C) - limite - p.NombreViaSobreCota;
                    break;
                case Lado.Oeste:
                    texto.Giro = 90; texto.Y = Y((my0 + my1) / 2);
                    texto.X = v != null ? (X(cara.C) - limite + X(v.Frente + v.Corrimiento) + LimiteVecino(v, tubos, p)) / 2 : X(cara.C) - limite - p.NombreViaLateral;
                    break;
                default:
                    texto.Giro = 90; texto.Y = Y((my0 + my1) / 2);
                    texto.X = v != null ? (X(cara.C) + limite + X(v.Frente + v.Corrimiento) - LimiteVecino(v, tubos, p)) / 2 : X(cara.C) + limite + p.NombreViaLateral;
                    break;
            }
            plano.Textos.Add(texto);
        }

        // ── 11. Revisión ──
        // Lo que suele faltar en el croquis (para que la IA lo complete antes de entregar).
        foreach (IndicadorCroquis ind in croquis.Indicadores)
        {
            var q = new P2(ind.X, ind.Y);
            if (!DentroDe(q, verticesManzana) && (vecinos.Count == 0 || Duenio(q) == -1))
                r.Avisos.Add($"el indicador del anillo {ind.Numero} está fuera de la manzana pero no hay manzana vecina: agregue en «vecinos» el borde de su manzana (al menos el lado que da a la vía)");
            else if (DentroDe(q, verticesManzana) && (string.IsNullOrWhiteSpace(ind.Viviendas) || !ind.Viviendas!.Any(char.IsDigit)))
                r.Avisos.Add($"el indicador del anillo {ind.Numero} no tiene el número de viviendas («10 VIVIENDAS»): agréguelo si la hoja lo trae; si no, no lo invente");
        }
        if (!string.IsNullOrWhiteSpace(diametroLayout))
            foreach (string d in tubos.Where(t => t.Nueva && !t.Cruce).Select(t => t.Diametro).Distinct())
                if (d != Normalizar(diametroLayout))
                    r.Avisos.Add($"el layout es de Ø{Normalizar(diametroLayout)}\" y la tubería nueva quedó de Ø{d}\": revise el diámetro en el cuadro de la hoja (tubería y accesorios); si la hoja no lo dice claramente, use el del layout");
        if (croquis.Vias.Count == 0) r.Avisos.Add($"no hay nombres de vías: agregue los que la hoja traiga (KR…, CL…); si no trae ninguno, no los invente: quedan como «{p.ViaSinNombre}»");
        Revisar(croquis, plano, p, tamanos, r);
        r.Registro = Registrar(croquis, anillo, manzana, mx0, my0, mx1, my1, tubos);
        return r;
    }

    private static void AjustarPuntas(Croquis croquis, List<Tubo> tubos, List<Borde> caras, double tamano, Func<double, double> X, Func<double, double> Y, ResultadoEnderezado r)
    {
        double cerca = 0.06 * tamano;
        static double Dist(P2 a, P2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        foreach (AccesorioCroquis acc in croquis.Accesorios.Where(x => x.Ubicacion != null && !string.IsNullOrWhiteSpace(x.Ubicacion.Texto)))
        {
            var q = new P2(acc.X, acc.Y);
            Tubo? t = tubos.FirstOrDefault(x => x.Nueva && !x.Cruce && !x.Cerrada && x.Original.Count >= 2 && (Dist(x.Original[0], q) <= cerca || Dist(x.Original[^1], q) <= cerca));
            if (t == null) continue; // los del medio de un tramo los acota el detallado; aquí solo se mueven puntas
            if (!double.TryParse(Cota(acc.Ubicacion!.Texto), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double valor)) continue;
            int extremo = Dist(t.Original[0], q) <= Dist(t.Original[^1], q) ? -1 : 1;
            int indice = extremo < 0 ? 0 : t.Tramos.Count - 1;
            Borde tramo = t.Tramos[indice];
            double sobre = tramo.H ? q.X : q.Y;
            int signo = (acc.Ubicacion.Hacia ?? string.Empty).Trim().ToLowerInvariant() switch { "abajo" or "derecha" => 1, "arriba" or "izquierda" => -1, _ => 0 };
            // Caras perpendiculares al tramo, al costado de la tubería (la torre o el predio que acompaña).
            var candidatas = caras.Where(f => f.H != tramo.H && tramo.C >= f.Min - 0.2 * tamano && tramo.C <= f.Max + 0.2 * tamano)
                                  .Where(f => valor == 0 || signo == 0 || (f.C - sobre) * signo > 0)
                                  .OrderBy(f => Math.Abs(f.C - sobre)).ToList();
            if (candidatas.Count == 0) { r.Avisos.Add($"no se encontró la cara de la cota {acc.Ubicacion.Texto} del accesorio {acc.Nombre}"); continue; }
            Borde cara = candidatas[0];
            if (signo == 0 && valor != 0) signo = Math.Sign(cara.C - sobre);
            double caraDib = tramo.H ? X(cara.C) : Y(cara.C);
            // Escala del tramo: su cota de largo frente a lo que mide dibujado.
            (P2 a, P2 b) = t.Extremos(indice);
            double largoDib = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            double largoM = indice < t.Cotas.Count && double.TryParse(Cota(t.Cotas[indice] ?? string.Empty), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m) ? m : 0;
            double unidades = largoM > 0 ? valor * largoDib / largoM : 0;
            double signoDib = tramo.H ? signo : -signo; // en el dibujo la y crece hacia arriba
            double objetivo = Math.Round(caraDib - signoDib * unidades, 4);
            t.FijarExtremo(extremo, objetivo, perpendicularA: !tramo.H);
            foreach (Tubo e in tubos.Where(x => !x.Nueva && !x.Cruce && x.Original.Count >= 2))
                foreach (int punta in new[] { -1, 1 })
                {
                    P2 p = punta < 0 ? e.Original[0] : e.Original[^1];
                    Borde et = punta < 0 ? e.Tramos[0] : e.Tramos[^1];
                    if (Dist(p, q) <= cerca && et.H == tramo.H) e.FijarExtremo(punta, objetivo, perpendicularA: !tramo.H);
                }
            r.Resumen.Add($"{acc.Nombre} a {Cota(acc.Ubicacion.Texto)} de la cara: la punta de la tubería nueva queda ahí");
        }
    }

    // ───────────────────────────── anillos vecinos (proyecto de varios anillos) ─────────────────────────────
    private static readonly Lado[] Horario = [Lado.Norte, Lado.Este, Lado.Sur, Lado.Oeste];
    private static string Nombre(Lado l) => l.ToString().ToLowerInvariant();
    private static Lado? DeNombre(string s) => Horario.Cast<Lado?>().FirstOrDefault(l => Nombre(l!.Value) == s.Trim().ToLowerInvariant());
    private static Lado Opuesto(Lado l) => Horario[(Array.IndexOf(Horario, l) + 2) % 4];
    private static string Via(string nombre) => System.Text.RegularExpressions.Regex.Replace(nombre.ToUpperInvariant().Split('-')[0], @"\s+", string.Empty);

    // Qué lados de la manzana del anillo llevan tubería nueva (y qué parte), y la vía de cada lado.
    private static AnilloRegistrado? Registrar(Croquis croquis, string? anillo, List<Borde> manzana, double x0, double y0, double x1, double y1, List<Tubo> tubos)
    {
        string numero = !string.IsNullOrWhiteSpace(anillo) ? anillo!.Trim()
            : croquis.Indicadores.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Viviendas))?.Numero.Trim() ?? string.Empty;
        if (numero.Length == 0) return null;
        var registro = new AnilloRegistrado { Numero = numero, Diametro = tubos.FirstOrDefault(t => t.Nueva && !t.Cruce)?.Diametro ?? string.Empty };
        double w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);
        var lados = new Dictionary<Lado, (double desde, double hasta)>();
        foreach (Tubo t in tubos.Where(t => t.Nueva && !t.Cruce))
            foreach (Borde tramo in t.Tramos.Where(b => b.Cara != null && b.Cara.Duenio == -1))
            {
                Lado lado = tramo.H ? (tramo.Cara!.C < (y0 + y1) / 2 ? Lado.Norte : Lado.Sur) : (tramo.Cara!.C < (x0 + x1) / 2 ? Lado.Oeste : Lado.Este);
                // En sentido horario: norte de oeste a este, este de norte a sur, sur de este a oeste, oeste de sur a norte.
                (double a, double b) = lado switch
                {
                    Lado.Norte => ((tramo.Min - x0) / w, (tramo.Max - x0) / w),
                    Lado.Este => ((tramo.Min - y0) / h, (tramo.Max - y0) / h),
                    Lado.Sur => ((x1 - tramo.Max) / w, (x1 - tramo.Min) / w),
                    _ => ((y1 - tramo.Max) / h, (y1 - tramo.Min) / h),
                };
                a = Math.Clamp(a, 0, 1); b = Math.Clamp(b, 0, 1);
                if (b - a < 0.05) continue;
                lados[lado] = lados.TryGetValue(lado, out var hay) ? (Math.Min(hay.desde, a), Math.Max(hay.hasta, b)) : (a, b);
            }
        foreach (var (lado, (desde, hasta)) in lados)
            registro.Lados.Add(new LadoConTuberia { Lado = Nombre(lado), Desde = Math.Round(desde < 0.12 ? 0 : desde, 3), Hasta = Math.Round(hasta > 0.88 ? 1 : hasta, 3) });
        foreach (ViaCroquis via in croquis.Vias.Where(v => !string.IsNullOrWhiteSpace(v.Nombre)))
            registro.Vias[Nombre(LadoDe(new P2(via.X, via.Y), x0, y0, x1, y1))] = via.Nombre.Trim().ToUpperInvariant();
        return registro;
    }

    // La red de un anillo vecino ya calcado se dibuja completa alrededor de su manzana (lo que se ve en esta hoja), como
    // existente, a la separación de sus caras. Su giro respecto a esta hoja sale de la vía que comparten; si no se sabe, se
    // supone sin giro. La red existente del croquis que va a lo largo de esos lados se reemplaza.
    private static void CompletarVecinos(Croquis croquis, List<Vecino> vecinos, IReadOnlyDictionary<string, AnilloRegistrado> registro, string? anillo,
        List<P2> manzana, double mx0, double my0, double mx1, double my1, List<Tubo> tubos, Parametros p, Func<double, double> X, Func<double, double> Y,
        Marco marco, Plano plano, ResultadoEnderezado r)
    {
        foreach (Vecino v in vecinos)
        {
            IndicadorCroquis? indicador = croquis.Indicadores.FirstOrDefault(i =>
            {
                var q = new P2(i.X, i.Y);
                if (DentroDe(q, manzana) || i.Numero.Trim() == anillo?.Trim()) return false;
                double mejor = DistanciaPoligono(q, manzana, cerrada: true);
                return vecinos.OrderBy(o => DistanciaPoligono(q, o.Original, cerrada: false)).First() == v && DistanciaPoligono(q, v.Original, cerrada: false) < mejor;
            });
            if (indicador == null || !registro.TryGetValue(indicador.Numero.Trim(), out AnilloRegistrado? otro) || otro.Lados.Count == 0) continue;

            // Giro: el lado del otro anillo que da a la vía que comparten debe quedar mirando a esta manzana.
            Lado frente = Opuesto(v.Lado);
            string? nuestraVia = croquis.Vias.Where(x => !string.IsNullOrWhiteSpace(x.Nombre) && LadoDe(new P2(x.X, x.Y), mx0, my0, mx1, my1) == v.Lado).Select(x => Via(x.Nombre)).FirstOrDefault();
            Lado suyo = otro.Vias.Where(kv => nuestraVia != null && Via(kv.Value) == nuestraVia).Select(kv => DeNombre(kv.Key)).FirstOrDefault(l => l != null) ?? frente;
            int giro = (Array.IndexOf(Horario, frente) - Array.IndexOf(Horario, suyo) + 4) % 4;

            // Caja de la vecina en el dibujo; el lado lejano (fuera de la hoja) queda afuera del marco.
            List<P2> puntos = Vertices(v.Bordes, cerrada: false, null);
            double vx0 = X(puntos.Min(q => q.X)), vx1 = X(puntos.Max(q => q.X)), vyN = Y(puntos.Min(q => q.Y)), vyS = Y(puntos.Max(q => q.Y));
            double lejos = 1000;
            switch (v.Lado)
            {
                case Lado.Sur: vyS = marco.Y0 - lejos; break;
                case Lado.Norte: vyN = marco.Y1 + lejos; break;
                case Lado.Este: vx1 = marco.X1 + lejos; break;
                default: vx0 = marco.X0 - lejos; break;
            }
            double s = p.Separacion;
            double xO = vx0 - s, xE = vx1 + s, yN = vyN + s, yS = vyS - s;
            (double[] a, double[] b) Lado_(Lado l) => l switch
            {
                Lado.Norte => ([xO, yN], [xE, yN]),
                Lado.Este => ([xE, yN], [xE, yS]),
                Lado.Sur => ([xE, yS], [xO, yS]),
                _ => ([xO, yS], [xO, yN]),
            };

            var cubiertos = new Dictionary<Lado, (double desde, double hasta)>();
            foreach (LadoConTuberia l in otro.Lados)
            {
                if (DeNombre(l.Lado) is not Lado propio) continue;
                Lado aqui = Horario[(Array.IndexOf(Horario, propio) + giro) % 4];
                if (aqui == v.Lado) continue; // el lado del otro anillo que da lejos de esta manzana no se ve
                cubiertos[aqui] = aqui == frente ? (l.Desde, l.Hasta) : (0, 1); // los lados que salen de la hoja, enteros
            }
            if (cubiertos.Count == 0) continue;

            // En sentido horario, uniendo los lados seguidos que se tocan en la esquina.
            var lineas = new List<List<double[]>>();
            List<double[]>? actual = null;
            int inicio = Enumerable.Range(0, 4).FirstOrDefault(i => !cubiertos.ContainsKey(Horario[(i + 3) % 4]) || cubiertos[Horario[(i + 3) % 4]].hasta < 0.999, 0);
            for (int k = 0; k < 4; k++)
            {
                Lado l = Horario[(inicio + k) % 4];
                if (!cubiertos.TryGetValue(l, out var parte)) { actual = null; continue; }
                (double[] a, double[] b) = Lado_(l);
                double[] desde = [a[0] + (b[0] - a[0]) * parte.desde, a[1] + (b[1] - a[1]) * parte.desde];
                double[] hasta = [a[0] + (b[0] - a[0]) * parte.hasta, a[1] + (b[1] - a[1]) * parte.hasta];
                if (actual == null || parte.desde > 0.001) { actual = [desde]; lineas.Add(actual); }
                actual.Add(hasta);
                if (parte.hasta < 0.999) actual = null;
            }
            foreach (List<double[]> linea in lineas)
                foreach (List<double[]> tramo in RecortarAlMarco(linea, marco, 0.5))
                    plano.Lineas.Add(new LineaPlano { Tipo = "tuberia_existente", Puntos = tramo });

            // La red existente del croquis que va a lo largo de esos lados sobra (era un pedazo de la misma).
            foreach (Tubo t in tubos.Where(t => !t.Nueva && !t.Cruce))
            {
                // Va por la línea de un lado cubierto (por su cara o por su posición en el dibujo, si el cruce la alineó).
                bool PorLado(Borde b) => cubiertos.Keys.Any(l => EsHorizontalLado(l) == b.H
                    && ((b.Cara != null && b.Cara.Duenio == v.Indice) || Math.Abs(b.CDib - (l switch { Lado.Norte => yN, Lado.Sur => yS, Lado.Oeste => xO, _ => xE })) < 0.5));
                if (t.Tramos.All(PorLado)) t.Reemplazada = true;
            }
            r.Resumen.Add($"Red del anillo {otro.Numero} (ya calcado) completada alrededor de su manzana: {string.Join(", ", cubiertos.Keys.Select(Nombre))}");
        }
    }

    // ───────────────────────────── cruce de vía (regla 10) ─────────────────────────────
    // Siempre de 3/4". En cada extremo que llega a una red de otro diámetro: tee de ese diámetro, tramito de 0.1 y
    // reducción 3/4x<diámetro> con la punta hacia la red. Cotas apiladas (0.1 · largo · 0.1) y el texto 3/4" al lado.
    private static void ArmarCruce(Tubo c, Parametros p, Plano plano, List<AccesorioPlano> accesorios, string superficie)
    {
        (P2 s, P2 e) = c.Extremos(0);
        double largo = Math.Sqrt((e.X - s.X) * (e.X - s.X) + (e.Y - s.Y) * (e.Y - s.Y));
        if (largo < 1e-6) return;
        double ux = (e.X - s.X) / largo, uy = (e.Y - s.Y) / largo;
        string dS = c.Diametros is { Length: > 0 } ? c.Diametros[0] : "3/4", dE = c.Diametros is { Length: > 1 } ? c.Diametros[1] : dS;
        bool redS = dS != "3/4", redE = dE != "3/4";
        double t = Math.Min(p.TramitoMaximo * largo, Math.Max(p.TramitoMinimo, p.TramitoFraccion * largo));
        P2 r1 = redS ? new P2(s.X + ux * t, s.Y + uy * t) : s, r2 = redE ? new P2(e.X - ux * t, e.Y - uy * t) : e;

        plano.Lineas.Add(new LineaPlano { Tipo = "tuberia_nueva", Puntos = [[s.X, s.Y], [e.X, e.Y]] });
        string largoTexto = Cota(c.Cotas.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty);
        if (redS) plano.Cotas.Add(new CotaPlano { A = [s.X, s.Y], B = [r1.X, r1.Y], Texto = "0.1", Diametro = dS });
        if (largoTexto.Length > 0) plano.Cotas.Add(new CotaPlano { A = [r1.X, r1.Y], B = [r2.X, r2.Y], Texto = largoTexto, Diametro = "3/4" });
        if (redE) plano.Cotas.Add(new CotaPlano { A = [r2.X, r2.Y], B = [e.X, e.Y], Texto = "0.1", Diametro = dE });

        // Del lado de las cotas (afuera de la manzana), el diámetro del cruce.
        double nx = -uy, ny = ux, mxc = (r1.X + r2.X) / 2, myc = (r1.Y + r2.Y) / 2;
        if ((plano.CentroManzana[0] - mxc) * nx + (plano.CentroManzana[1] - myc) * ny > 0) { nx = -nx; ny = -ny; }
        double distancia = p.OffsetCota + 6;
        plano.Textos.Add(new TextoPlano { Texto = "3/4\"", X = Math.Round(mxc + nx * distancia, 4), Y = Math.Round(myc + ny * distancia, 4), Giro = Math.Abs(ux) < 0.5 ? 90 : 0, Tipo = "libre" });

        accesorios.Add(new AccesorioPlano { Nombre = "TEE", X = s.X, Y = s.Y, Diametro = dS, Superficie = superficie });
        accesorios.Add(new AccesorioPlano { Nombre = "TEE", X = e.X, Y = e.Y, Diametro = dE, Superficie = superficie });
        double haciaS = Grados(-ux, -uy), haciaE = Grados(ux, uy);
        if (redS) accesorios.Add(new AccesorioPlano { Nombre = "REDUCCION", X = r1.X, Y = r1.Y, Diametro = "3/4x" + dS, Superficie = superficie, Direccion = haciaS, Fija = true });
        if (redE) accesorios.Add(new AccesorioPlano { Nombre = "REDUCCION", X = r2.X, Y = r2.Y, Diametro = "3/4x" + dE, Superficie = superficie, Direccion = haciaE, Fija = true });
    }

    // ───────────────────────────── accesorios ─────────────────────────────
    // Dirección según el bloque (ver CalcoAccesorio en el plugin): TAPON hacia donde sigue la tubería; TEE hacia el ramal
    // (el brazo que no está alineado con otro); el resto, a lo largo de la tubería.
    private static double Direccion(AccesorioPlano a, List<Tubo> tubos, ResultadoEnderezado r)
    {
        if (a.Fija) return a.Direccion;
        var direcciones = new List<double>();
        foreach (Tubo t in tubos)
            foreach ((P2 p0, P2 p1, _) in t.Segmentos())
            {
                if (Cerca(p0, a)) direcciones.Add(Grados(p1.X - p0.X, p1.Y - p0.Y));
                else if (Cerca(p1, a)) direcciones.Add(Grados(p0.X - p1.X, p0.Y - p1.Y));
                else if (SobreSegmento(new P2(a.X, a.Y), p0, p1)) { direcciones.Add(Grados(p1.X - p0.X, p1.Y - p0.Y)); direcciones.Add(Grados(p0.X - p1.X, p0.Y - p1.Y)); }
            }
        direcciones = direcciones.Aggregate(new List<double>(), (lista, d) => { if (!lista.Any(x => DiferenciaAngulo(x, d) < 2)) lista.Add(d); return lista; });
        if (direcciones.Count == 0) return 0;
        if (a.Nombre == "UNION")
            foreach (Tubo t in tubos.Where(t => t.Nueva && !t.Cruce))
            {
                List<P2> pts = t.Polilinea();
                if (pts.Count < 2 || t.Cerrada) continue;
                if (Cerca(pts[0], a)) return Grados(pts[1].X - pts[0].X, pts[1].Y - pts[0].Y);       // hacia dentro de la nueva
                if (Cerca(pts[^1], a)) return Grados(pts[^2].X - pts[^1].X, pts[^2].Y - pts[^1].Y);
            }
        switch (a.Nombre)
        {
            case "TAPON":
                if (direcciones.Count > 1) r.Avisos.Add(FormattableString.Invariant($"el tapón en ({a.X:0.0}, {a.Y:0.0}) no está en un extremo de tubería"));
                return direcciones[0];
            case "TEE":
                foreach (double d in direcciones)
                    if (!direcciones.Any(o => DiferenciaAngulo(o, d + 180) < 2)) return d;   // el ramal no tiene opuesto
                r.Avisos.Add(FormattableString.Invariant($"la tee en ({a.X:0.0}, {a.Y:0.0}) no tiene un ramal claro"));
                return direcciones[0];
            default:
                return direcciones[0];
        }
    }

    private static bool Cerca(P2 q, AccesorioPlano a) => Math.Abs(q.X - a.X) < 0.05 && Math.Abs(q.Y - a.Y) < 0.05;

    private sealed record Ubicacion(Tubo Tubo, P2 Punto);

    // El accesorio va sobre la tubería: en el vértice o empalme más cercano (esquina, extremo, donde arranca otra
    // tubería) si está cerca; si no, en el punto del tramo más cercano. Se busca en píxeles del croquis y se lleva al dibujo.
    private static Ubicacion? Ubicar(List<Tubo> tubos, P2 q, double cercaVertice)
    {
        Vertice? vertice = VerticeCercano(tubos, q, null);
        if (vertice != null)
        {
            P2 o = vertice.Tubo.Original[vertice.Indice];
            if (Math.Sqrt((o.X - q.X) * (o.X - q.X) + (o.Y - q.Y) * (o.Y - q.Y)) <= cercaVertice)
            {
                P2 v = vertice.Tubo.Punto(vertice);
                return new Ubicacion(vertice.Tubo, new P2(Math.Round(v.X, 4), Math.Round(v.Y, 4)));
            }
        }

        Ubicacion? mejor = null;
        double distancia = double.MaxValue;
        foreach (Tubo t in tubos)
        {
            List<P2> original = t.Original;
            int n = original.Count, m = t.Cerrada ? n : n - 1;
            for (int i = 0; i < m && i < t.Tramos.Count; i++)
            {
                P2 a = original[i], b = original[(i + 1) % n];
                double u = Proyeccion(q, a, b);
                P2 en = new(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);
                double d = Math.Sqrt((q.X - en.X) * (q.X - en.X) + (q.Y - en.Y) * (q.Y - en.Y));
                if (d >= distancia) continue;
                (P2 da, P2 db) = t.Extremos(i);
                double largo = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                double cerca = Math.Min(largo / 3, 80);
                P2 punto = u * largo < cerca ? da : (1 - u) * largo < cerca ? db : new P2(Math.Round(da.X + (db.X - da.X) * u, 4), Math.Round(da.Y + (db.Y - da.Y) * u, 4));
                distancia = d;
                mejor = new Ubicacion(t, punto);
            }
        }
        return distancia < 250 ? mejor : null;
    }

    // ───────────────────────────── indicador y vías ─────────────────────────────
    // El indicador del anillo se queda donde lo puso el croquis si no estorba; si no, se busca el lugar más cercano
    // (hasta 12 unidades) que cumpla, en este orden: ni el círculo ni el texto de viviendas cruzan líneas ni tapan los
    // números de predio; el círculo libre; el círculo al menos sin tapar números (puede cruzar una línea).
    private static (double x, double y) UbicarIndicador(double cx, double cy, string? viviendas, Parametros p, TamanosPedido tamanos,
        List<(P2 a, P2 b)> lineas, List<(double x0, double y0, double x1, double y1)> textos)
    {
        double alto = tamanos.Predial, r = p.RadioIndicador;
        bool SinLineas(double x, double y) => lineas.All(l => DistanciaSegmento(new P2(x, y), l.a, l.b) >= r + 0.4);
        bool SinTextos(double x, double y) => textos.All(t => t.x1 < x - r || t.x0 > x + r || t.y1 < y - r || t.y0 > y + r);
        (double x0, double y0, double x1, double y1) CajaViviendas(double x, double y)
        {
            double ancho = 0.9 * alto * viviendas!.Length, ty = y - r - p.SeparacionViviendas;
            return (x - ancho / 2, ty - alto / 2 - 0.3, x + ancho / 2, ty + alto / 2 + 0.3);
        }
        bool ViviendasSinTextos(double x, double y)
        {
            if (viviendas == null) return true;
            var (x0, y0, x1, y1) = CajaViviendas(x, y);
            return textos.All(t => t.x1 < x0 || t.x0 > x1 || t.y1 < y0 || t.y0 > y1);
        }
        bool ViviendasLibres(double x, double y)
        {
            if (viviendas == null) return true;
            var (x0, y0, x1, y1) = CajaViviendas(x, y);
            return !lineas.Any(l => CruzaCaja(l.a, l.b, x0, y0, x1, y1)) && ViviendasSinTextos(x, y);
        }
        var candidatos = new List<(double x, double y, double d)>();
        for (double dx = -12; dx <= 12; dx += 0.5)
            for (double dy = -12; dy <= 12; dy += 0.5)
            {
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= 12) candidatos.Add((cx + dx, cy + dy, d));
            }
        candidatos.Sort((a, b) => a.d.CompareTo(b.d));
        foreach (Func<double, double, bool> cumple in new Func<double, double, bool>[]
                 {
                     (x, y) => SinLineas(x, y) && SinTextos(x, y) && ViviendasLibres(x, y),
                     (x, y) => SinLineas(x, y) && SinTextos(x, y) && ViviendasSinTextos(x, y),
                     // Entre predios angostos no hay sitio libre de líneas: al menos que no tape ningún texto.
                     (x, y) => SinTextos(x, y) && ViviendasSinTextos(x, y),
                     (x, y) => SinLineas(x, y) && SinTextos(x, y),
                     SinTextos,
                 })
            foreach (var c in candidatos)
                if (cumple(c.x, c.y)) return (Math.Round(c.x, 4), Math.Round(c.y, 4));
        return (cx, cy);
    }

    // Como NOMENK (nomenclatura predial): el texto va en el centroide del predio donde cae, la región que cierran la
    // manzana, las líneas de predio, las construcciones y el marco. La región se busca rellenando una rejilla fina desde
    // el punto del croquis. El texto se queda donde estaba si la región no cierra (se escapa de la manzana) o si otro
    // texto cae en la misma. Si no cabe acostado y de pie sí (o al revés), se gira.
    private static void CentrarEnPredios(Plano plano, Marco marco, TamanosPedido tamanos, ResultadoEnderezado r)
    {
        const double paso = 0.25, grosor = 0.35;
        List<TextoPlano> textos = plano.Textos.Where(t => t.Tipo == "predial").ToList();
        List<double[]> deManzana = plano.Lineas.Where(l => l.Tipo == "manzana").SelectMany(l => l.Puntos).ToList();
        if (textos.Count == 0 || deManzana.Count == 0) return;

        // Rejilla sobre la manzana (sin pasar del marco), con una celda de sobra alrededor para notar si la región se escapa.
        double x0 = Math.Max(marco.X0, deManzana.Min(q => q[0])) - paso, y0 = Math.Max(marco.Y0, deManzana.Min(q => q[1])) - paso;
        double x1 = Math.Min(marco.X1, deManzana.Max(q => q[0])) + paso, y1 = Math.Min(marco.Y1, deManzana.Max(q => q[1])) + paso;
        int nx = (int)Math.Ceiling((x1 - x0) / paso), ny = (int)Math.Ceiling((y1 - y0) / paso);
        if (nx < 3 || ny < 3 || (long)nx * ny > 4_000_000) return;
        double Cx(int i) => x0 + (i + 0.5) * paso;
        double Cy(int j) => y0 + (j + 0.5) * paso;
        var pared = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                pared[i, j] = Cx(i) < marco.X0 || Cx(i) > marco.X1 || Cy(j) < marco.Y0 || Cy(j) > marco.Y1;
        List<(P2 a, P2 b)> paredes = plano.Lineas.Where(l => l.Tipo is "manzana" or "predio" or "construccion").SelectMany(Segmentos).ToList();
        foreach ((P2 a, P2 b) in paredes)
        {
            int i0 = Math.Max(0, (int)Math.Floor((Math.Min(a.X, b.X) - grosor - x0) / paso)), i1 = Math.Min(nx - 1, (int)Math.Ceiling((Math.Max(a.X, b.X) + grosor - x0) / paso));
            int j0 = Math.Max(0, (int)Math.Floor((Math.Min(a.Y, b.Y) - grosor - y0) / paso)), j1 = Math.Min(ny - 1, (int)Math.Ceiling((Math.Max(a.Y, b.Y) + grosor - y0) / paso));
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    if (!pared[i, j] && DistanciaSegmento(new P2(Cx(i), Cy(j)), a, b) <= grosor) pared[i, j] = true;
        }

        // Cada texto, a su región (la celda libre más cercana a su punto, por si cayó encima de una línea).
        var region = new int[nx, ny];
        var regiones = new List<(List<(int i, int j)> celdas, bool escapa)>();
        var deTexto = new Dictionary<TextoPlano, int>();
        foreach (TextoPlano t in textos)
        {
            int ti = (int)Math.Floor((t.X - x0) / paso), tj = (int)Math.Floor((t.Y - y0) / paso);
            (int i, int j)? inicio = null;
            for (int radio = 0; radio <= 6 && inicio == null; radio++)
                for (int i = ti - radio; i <= ti + radio && inicio == null; i++)
                    for (int j = tj - radio; j <= tj + radio && inicio == null; j++)
                        if (i >= 0 && j >= 0 && i < nx && j < ny && !pared[i, j]) inicio = (i, j);
            if (inicio == null) continue;
            (int si, int sj) = inicio.Value;
            if (region[si, sj] == 0)
            {
                var celdas = new List<(int i, int j)>();
                bool escapa = false;
                int id = regiones.Count + 1;
                var pendientes = new Stack<(int i, int j)>();
                pendientes.Push((si, sj));
                region[si, sj] = id;
                while (pendientes.Count > 0)
                {
                    (int i, int j) = pendientes.Pop();
                    celdas.Add((i, j));
                    if (i == 0 || j == 0 || i == nx - 1 || j == ny - 1) escapa = true;
                    foreach ((int vi, int vj) in new[] { (i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1) })
                        if (vi >= 0 && vj >= 0 && vi < nx && vj < ny && !pared[vi, vj] && region[vi, vj] == 0) { region[vi, vj] = id; pendientes.Push((vi, vj)); }
                }
                regiones.Add((celdas, escapa));
            }
            deTexto[t] = region[si, sj];
        }

        var centrados = new List<string>();
        foreach (var grupo in deTexto.GroupBy(kv => kv.Value))
        {
            var (celdas, escapa) = regiones[grupo.Key - 1];
            if (escapa || grupo.Count() > 1) continue;
            TextoPlano t = grupo.First().Key;
            double mx = celdas.Average(c => Cx(c.i)), my = celdas.Average(c => Cy(c.j));
            // En un predio en L el centroide puede caer afuera: se usa la celda del predio más cercana.
            int ci = (int)Math.Floor((mx - x0) / paso), cj = (int)Math.Floor((my - y0) / paso);
            if (ci < 0 || cj < 0 || ci >= nx || cj >= ny || region[ci, cj] != grupo.Key)
            {
                var c = celdas.MinBy(c => (Cx(c.i) - mx) * (Cx(c.i) - mx) + (Cy(c.j) - my) * (Cy(c.j) - my));
                (mx, my) = (Cx(c.i), Cy(c.j));
            }
            // Predio rectangular (lo normal): el centro exacto, a medio camino entre sus líneas (la rejilla se corre hasta
            // media celda).
            int bi0 = celdas.Min(c => c.i), bi1 = celdas.Max(c => c.i), bj0 = celdas.Min(c => c.j), bj1 = celdas.Max(c => c.j);
            if (celdas.Count >= 0.98 * (bi1 - bi0 + 1) * (bj1 - bj0 + 1))
            {
                double izquierda = marco.X0, derecha = marco.X1, abajo = marco.Y0, arriba = marco.Y1;
                foreach ((P2 a, P2 b) in paredes)
                {
                    if (Math.Abs(b.Y - a.Y) > 1e-9 && Math.Min(a.Y, b.Y) <= my && my <= Math.Max(a.Y, b.Y))
                    {
                        double x = a.X + (my - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                        if (x < mx) izquierda = Math.Max(izquierda, x); else derecha = Math.Min(derecha, x);
                    }
                    if (Math.Abs(b.X - a.X) > 1e-9 && Math.Min(a.X, b.X) <= mx && mx <= Math.Max(a.X, b.X))
                    {
                        double y = a.Y + (mx - a.X) * (b.Y - a.Y) / (b.X - a.X);
                        if (y < my) abajo = Math.Max(abajo, y); else arriba = Math.Min(arriba, y);
                    }
                }
                (mx, my) = ((izquierda + derecha) / 2, (abajo + arriba) / 2);
            }
            // Que la caja del texto quede toda dentro del predio. Si en el centro no cabe, se gira; si tampoco, va al punto
            // más cercano al centro donde quepa (predio en L), primero con su giro y luego girado.
            double alto = tamanos.Predial, largo = 0.9 * alto * t.Texto.Length;
            bool Cabe(double x, double y, double giro)
            {
                (double w, double h) = giro == 90 ? (alto, largo) : (largo, alto);
                for (int i = Math.Max(0, (int)Math.Floor((x - w / 2 - x0) / paso)); i <= Math.Min(nx - 1, (int)Math.Floor((x + w / 2 - x0) / paso)); i++)
                    for (int j = Math.Max(0, (int)Math.Floor((y - h / 2 - y0) / paso)); j <= Math.Min(ny - 1, (int)Math.Floor((y + h / 2 - y0) / paso)); j++)
                        if (region[i, j] != grupo.Key) return false;
                return true;
            }
            (double x, double y)? Cercano(double giro)
            {
                foreach (var c in celdas.OrderBy(c => (Cx(c.i) - mx) * (Cx(c.i) - mx) + (Cy(c.j) - my) * (Cy(c.j) - my)))
                    if (Cabe(Cx(c.i), Cy(c.j), giro)) return (Cx(c.i), Cy(c.j));
                return null;
            }
            double otro = t.Giro == 90 ? 0 : 90;
            if (!Cabe(mx, my, t.Giro))
            {
                if (Cabe(mx, my, otro)) t.Giro = otro;
                else if (Cercano(t.Giro) is { } q) (mx, my) = q;
                else if (Cercano(otro) is { } q2) { (mx, my) = q2; t.Giro = otro; }
            }
            t.X = Math.Round(mx, 4);
            t.Y = Math.Round(my, 4);
            centrados.Add(t.Texto);
        }
        if (centrados.Count > 0) r.Resumen.Add("Textos de predio centrados en su predio (como NOMENK): " + string.Join(", ", centrados));
    }

    private static (double x0, double y0, double x1, double y1) Caja(TextoPlano t, TamanosPedido tamanos)
    {
        double alto = t.Tipo == "vial" ? tamanos.Vial : tamanos.Predial, ancho = 0.9 * alto * t.Texto.Length;
        (double w, double h) = t.Giro == 90 ? (alto, ancho) : (ancho, alto);
        return (t.X - w / 2, t.Y - h / 2, t.X + w / 2, t.Y + h / 2);
    }

    private static double LimiteVecino(Vecino v, List<Tubo> tubos, Parametros p) =>
        tubos.Any(t => t.Tramos.Any(b => b.Cara != null && b.Cara.Duenio == v.Indice)) ? p.Separacion : 0;

    // ───────────────────────────── revisión ─────────────────────────────
    private static void Revisar(Croquis croquis, Plano plano, Parametros p, TamanosPedido tamanos, ResultadoEnderezado r)
    {
        Marco m = p.Marco;
        int fuera = plano.Lineas.Where(l => l.Tipo != "vecino").SelectMany(l => l.Puntos).Concat(plano.Textos.Select(t => new[] { t.X, t.Y }))
            .Count(q => q[0] < m.X0 || q[0] > m.X1 || q[1] < m.Y0 || q[1] > m.Y1);
        if (fuera > 0) r.Avisos.Add($"{fuera} punto(s) quedan fuera del marco");

        // Totales por diámetro (lo que leerá el Excel) frente al cuadro de UC de la hoja.
        var totales = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (CotaPlano c in plano.Cotas)
            if (double.TryParse(c.Texto, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                totales[c.Diametro] = totales.TryGetValue(c.Diametro, out double s) ? s + v : v;
        foreach (var kv in totales) r.Resumen.Add(FormattableString.Invariant($"Tubería {kv.Key}\": {kv.Value:0.0#} ML"));
        if (croquis.Cuadro != null)
            foreach (var kv in croquis.Cuadro)
            {
                string d = Normalizar(kv.Key);
                if (!double.TryParse(kv.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double cuadro)) continue;
                double dibujo = totales.TryGetValue(d, out double s) ? s : 0;
                if (Math.Abs(cuadro - dibujo) > 0.05) r.Avisos.Add(FormattableString.Invariant($"el cuadro dice {cuadro:0.0#} ML de {d}\" y las cotas suman {dibujo:0.0#}"));
            }
        foreach (var g in plano.Accesorios.GroupBy(a => (a.Nombre, a.Diametro)).OrderBy(g => g.Key.Nombre))
            r.Resumen.Add($"{g.Key.Nombre} {g.Key.Diametro}\": {g.Count()}");
        if (croquis.CuadroAccesorios != null)
            foreach (var kv in croquis.CuadroAccesorios)
            {
                string nombre = kv.Key.Trim().ToUpperInvariant().Replace("Ó", "O");
                int dibujo = plano.Accesorios.Count(a => a.Nombre == nombre);
                if (dibujo != kv.Value) r.Avisos.Add($"el cuadro dice {kv.Value} {nombre} y el plano tiene {dibujo}");
            }

        // Textos que se montan (aproximado: caja de 0.9 × alto por letra).
        var cajas = new List<(string texto, double x0, double y0, double x1, double y1)>();
        foreach (TextoPlano t in plano.Textos)
        {
            double alto = t.Tipo == "vial" ? tamanos.Vial : tamanos.Predial, ancho = 0.9 * alto * t.Texto.Length;
            (double w, double h) = t.Giro == 90 ? (alto, ancho) : (ancho, alto);
            cajas.Add((t.Texto, t.X - w / 2, t.Y - h / 2, t.X + w / 2, t.Y + h / 2));
        }
        foreach (IndicadorPlano i in plano.Indicadores)
            cajas.Add(($"indicador {i.Numero}", i.X - p.RadioIndicador, i.Y - p.RadioIndicador, i.X + p.RadioIndicador, i.Y + p.RadioIndicador));
        for (int a = 0; a < cajas.Count; a++)
            for (int b = a + 1; b < cajas.Count; b++)
                if (cajas[a].x0 < cajas[b].x1 && cajas[b].x0 < cajas[a].x1 && cajas[a].y0 < cajas[b].y1 && cajas[b].y0 < cajas[a].y1)
                    r.Avisos.Add($"se montan «{cajas[a].texto}» y «{cajas[b].texto}»");
    }

    // ───────────────────────────── geometría ─────────────────────────────
    private enum Lado { Norte, Sur, Este, Oeste }

    private static bool EsHorizontalLado(Lado lado) => lado is Lado.Norte or Lado.Sur;

    // Lado de la manzana hacia el que queda un punto (px, y hacia abajo).
    private static Lado LadoDe(P2 q, double x0, double y0, double x1, double y1)
    {
        double w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);
        var opciones = new (Lado lado, double d)[] { (Lado.Norte, (y0 - q.Y) / h), (Lado.Sur, (q.Y - y1) / h), (Lado.Oeste, (x0 - q.X) / w), (Lado.Este, (q.X - x1) / w) };
        if (opciones.Max(o => o.d) > 0) return opciones.OrderByDescending(o => o.d).First().lado;
        // Adentro de la caja de la manzana: el borde más cercano.
        var cerca = new (Lado lado, double d)[] { (Lado.Norte, q.Y - y0), (Lado.Sur, y1 - q.Y), (Lado.Oeste, q.X - x0), (Lado.Este, x1 - q.X) };
        return cerca.OrderBy(o => o.d).First().lado;
    }

    private static bool EnLado(Borde b, Lado lado, double x0, double y0, double x1, double y1) => lado switch
    {
        Lado.Norte => b.C < (y0 + y1) / 2,
        Lado.Sur => b.C > (y0 + y1) / 2,
        Lado.Oeste => b.C < (x0 + x1) / 2,
        _ => b.C > (x0 + x1) / 2,
    };

    private static List<P2> Puntos(double[][] puntos) => puntos.Where(q => q.Length >= 2).Select(q => new P2(q[0], q[1])).ToList();

    // Cada tramo pasa a horizontal o vertical en su coordenada promedio; con fusionar se juntan los seguidos iguales.
    private static List<Borde> Ortogonal(IReadOnlyList<P2> puntos, bool cerrada, bool fusionar, int duenio)
    {
        var tramos = new List<Borde>();
        int n = puntos.Count, m = cerrada ? n : n - 1;
        for (int i = 0; i < m; i++)
        {
            P2 a = puntos[i], b = puntos[(i + 1) % n];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) continue;
            bool h = Math.Abs(dx) >= Math.Abs(dy);
            tramos.Add(new Borde { H = h, C = h ? (a.Y + b.Y) / 2 : (a.X + b.X) / 2, A0 = h ? a.X : a.Y, A1 = h ? b.X : b.Y, Duenio = duenio });
        }
        if (!fusionar) return tramos;
        var juntos = new List<Borde>();
        foreach (Borde t in tramos)
        {
            Borde? ultimo = juntos.Count > 0 ? juntos[^1] : null;
            if (ultimo != null && ultimo.H == t.H) { Fusionar(ultimo, t); continue; }
            juntos.Add(t);
        }
        if (cerrada && juntos.Count > 1 && juntos[0].H == juntos[^1].H) { Fusionar(juntos[^1], juntos[0]); juntos[^1].A1 = juntos[0].A1; juntos.RemoveAt(0); }
        return juntos;
    }

    private static void Fusionar(Borde a, Borde b)
    {
        double la = a.Largo, lb = b.Largo;
        a.C = la + lb > 0 ? (a.C * la + b.C * lb) / (la + lb) : a.C;
        a.A1 = b.A1;
    }

    // Vértices de una polilínea de tramos a 90° (en píxeles).
    private static List<P2> Vertices(List<Borde> tramos, bool cerrada, Func<Borde, double>? coordenada)
    {
        coordenada ??= b => b.C;
        var v = new List<P2>();
        int n = tramos.Count;
        if (n == 0) return v;
        if (!cerrada) v.Add(tramos[0].H ? new P2(tramos[0].A0, coordenada(tramos[0])) : new P2(coordenada(tramos[0]), tramos[0].A0));
        for (int i = 0; i < (cerrada ? n : n - 1); i++)
        {
            Borde a = tramos[i], b = tramos[(i + 1) % n];
            if (a.H != b.H) v.Add(a.H ? new P2(coordenada(b), coordenada(a)) : new P2(coordenada(a), coordenada(b)));
            else v.Add(a.H ? new P2(a.A1, coordenada(a)) : new P2(coordenada(a), a.A1));
        }
        if (!cerrada) v.Add(tramos[^1].H ? new P2(tramos[^1].A1, coordenada(tramos[^1])) : new P2(coordenada(tramos[^1]), tramos[^1].A1));
        return v;
    }

    private static double Empalme(Borde linea, double extremo, List<Borde> perpendiculares, double tolerancia)
    {
        double mejor = extremo, distancia = tolerancia;
        foreach (Borde o in perpendiculares)
        {
            if (o == linea || o.H == linea.H) continue;
            if (linea.C < o.Min - tolerancia || linea.C > o.Max + tolerancia) continue;
            double d = Math.Abs(o.C - extremo);
            if (d < distancia) { distancia = d; mejor = o.C; }
        }
        return mejor;
    }

    private static bool Solapa(Borde cara, Borde tramo)
    {
        double margen = 0.15 * Math.Max(cara.Largo, tramo.Largo);
        return tramo.Max > cara.Min - margen && tramo.Min < cara.Max + margen;
    }

    private static bool DentroDe(P2 q, List<P2> poligono)
    {
        bool dentro = false;
        for (int i = 0, j = poligono.Count - 1; i < poligono.Count; j = i++)
            if ((poligono[i].Y > q.Y) != (poligono[j].Y > q.Y) && q.X < (poligono[j].X - poligono[i].X) * (q.Y - poligono[i].Y) / (poligono[j].Y - poligono[i].Y) + poligono[i].X)
                dentro = !dentro;
        return dentro;
    }

    private static double DistanciaPoligono(P2 q, List<P2> puntos, bool cerrada)
    {
        double d = double.MaxValue;
        int n = puntos.Count;
        for (int i = 0; i < (cerrada ? n : n - 1); i++) d = Math.Min(d, DistanciaSegmento(q, puntos[i], puntos[(i + 1) % n]));
        return d;
    }

    private static double Proyeccion(P2 q, P2 a, P2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        return l2 < 1e-12 ? 0 : Math.Clamp(((q.X - a.X) * dx + (q.Y - a.Y) * dy) / l2, 0, 1);
    }

    private static double DistanciaSegmento(P2 q, P2 a, P2 b)
    {
        double u = Proyeccion(q, a, b), x = a.X + (b.X - a.X) * u - q.X, y = a.Y + (b.Y - a.Y) * u - q.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static bool SobreSegmento(P2 q, P2 a, P2 b) => DistanciaSegmento(q, a, b) < 0.05;

    private static bool CruzaCaja(P2 a, P2 b, double x0, double y0, double x1, double y1)
    {
        // Segmentos a 90° (los de predios): basta con ver si el rectángulo del segmento toca la caja.
        double sx0 = Math.Min(a.X, b.X), sx1 = Math.Max(a.X, b.X), sy0 = Math.Min(a.Y, b.Y), sy1 = Math.Max(a.Y, b.Y);
        return sx0 <= x1 && sx1 >= x0 && sy0 <= y1 && sy1 >= y0;
    }

    private static IEnumerable<(P2 a, P2 b)> Segmentos(LineaPlano l)
    {
        int n = l.Puntos.Count;
        for (int i = 0; i < (l.Cerrada ? n : n - 1); i++)
            yield return (new P2(l.Puntos[i][0], l.Puntos[i][1]), new P2(l.Puntos[(i + 1) % n][0], l.Puntos[(i + 1) % n][1]));
    }

    // La manzana sin los lados abiertos: las caras que siguen, en orden, con sus puntas llevadas hasta el borde del marco.
    private static List<List<double[]>> ManzanaAbierta(List<P2> v, HashSet<Lado> abiertos, double x0, double y0, double x1, double y1, Func<P2, double[]> D, Marco marco)
    {
        int n = v.Count;
        bool Quitada(int i)
        {
            P2 a = v[i], b = v[(i + 1) % n];
            bool h = Math.Abs(a.Y - b.Y) < 0.5, ver = Math.Abs(a.X - b.X) < 0.5;
            return (h && abiertos.Contains(Lado.Norte) && Math.Abs(a.Y - y0) < 0.5) || (h && abiertos.Contains(Lado.Sur) && Math.Abs(a.Y - y1) < 0.5)
                || (ver && abiertos.Contains(Lado.Oeste) && Math.Abs(a.X - x0) < 0.5) || (ver && abiertos.Contains(Lado.Este) && Math.Abs(a.X - x1) < 0.5);
        }
        var tramos = new List<List<double[]>>();
        int inicio = Enumerable.Range(0, n).FirstOrDefault(i => !Quitada(i) && Quitada((i - 1 + n) % n), -1);
        if (inicio < 0) { tramos.Add(v.Select(D).ToList()); return tramos; }
        List<double[]>? actual = null;
        for (int k = 0; k < n; k++)
        {
            int i = (inicio + k) % n;
            if (Quitada(i)) { if (actual != null) { Extender(actual, actual.Count - 1, actual.Count - 2, marco); tramos.Add(actual); actual = null; } continue; }
            if (actual == null) actual = [D(v[i])];
            actual.Add(D(v[(i + 1) % n]));
        }
        if (actual != null) { Extender(actual, actual.Count - 1, actual.Count - 2, marco); tramos.Add(actual); }
        foreach (List<double[]> t in tramos) Extender(t, 0, 1, marco);
        return tramos;
    }

    // Lleva la punta i de una polilínea (en el dibujo) hasta el borde del marco, siguiendo la dirección desde su vecino.
    private static void Extender(List<double[]> puntos, int i, int vecino, Marco marco)
    {
        double dx = puntos[i][0] - puntos[vecino][0], dy = puntos[i][1] - puntos[vecino][1];
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
        if (Math.Abs(dy) >= Math.Abs(dx)) puntos[i] = [puntos[i][0], dy > 0 ? marco.Y1 : marco.Y0];
        else puntos[i] = [dx > 0 ? marco.X1 : marco.X0, puntos[i][1]];
    }

    // La punta de una línea del croquis (px) que sale por el borde del recorte: está cerca de él y va hacia él.
    private static bool LlegaAlBorde(P2 punta, P2 anterior, Rect r, double tolerancia)
    {
        double dx = punta.X - anterior.X, dy = punta.Y - anterior.Y;
        if (Math.Abs(dy) >= Math.Abs(dx)) return (dy < 0 && Math.Abs(punta.Y - r.Y) <= tolerancia) || (dy > 0 && Math.Abs(punta.Y - r.Y1) <= tolerancia);
        return (dx < 0 && Math.Abs(punta.X - r.X) <= tolerancia) || (dx > 0 && Math.Abs(punta.X - r.X1) <= tolerancia);
    }

    // Recorta una polilínea al marco (con un margen hacia adentro); puede quedar partida en varios tramos.
    private static List<List<double[]>> RecortarAlMarco(List<double[]> puntos, Marco m, double margen)
    {
        double x0 = m.X0 + margen, y0 = m.Y0 + margen, x1 = m.X1 - margen, y1 = m.Y1 - margen;
        var partes = new List<List<double[]>>();
        List<double[]>? actual = null;
        for (int i = 0; i + 1 < puntos.Count; i++)
        {
            double ax = puntos[i][0], ay = puntos[i][1], bx = puntos[i + 1][0], by = puntos[i + 1][1];
            double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
            bool visible = true;
            foreach ((double pq, double qq) in new[] { (-dx, ax - x0), (dx, x1 - ax), (-dy, ay - y0), (dy, y1 - ay) })
            {
                if (Math.Abs(pq) < 1e-12) { if (qq < 0) { visible = false; break; } continue; }
                double t = qq / pq;
                if (pq < 0) { if (t > t1) { visible = false; break; } t0 = Math.Max(t0, t); }
                else { if (t < t0) { visible = false; break; } t1 = Math.Min(t1, t); }
            }
            if (!visible) { actual = null; continue; }
            double[] desde = [Math.Round(ax + dx * t0, 4), Math.Round(ay + dy * t0, 4)], hasta = [Math.Round(ax + dx * t1, 4), Math.Round(ay + dy * t1, 4)];
            if (actual == null || t0 > 1e-9) { actual = [desde]; partes.Add(actual); }
            actual.Add(hasta);
            if (t1 < 1 - 1e-9) actual = null;
        }
        return partes.Where(x => x.Count >= 2).ToList();
    }

    private static double Grados(double dx, double dy)
    {
        double g = Math.Atan2(dy, dx) * 180 / Math.PI;
        return Math.Round(g < 0 ? g + 360 : g, 4);
    }

    private static double DiferenciaAngulo(double a, double b)
    {
        double d = Math.Abs(((a - b) % 360 + 360) % 360);
        return Math.Min(d, 360 - d);
    }

    // "3/4\"" / "3/4 pulg" / "1/2" → "3/4", "1/2"; "2x3/4" se deja.
    public static string Normalizar(string? diametro)
    {
        string d = (diametro ?? "3/4").Trim().Replace("\"", "").Replace("”", "").Replace("pulg", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "");
        return d.Length == 0 ? "3/4" : d.ToLowerInvariant();
    }

    // "39" → "39.0", "45,5" → "45.5", "2°" → "2.0" (en los croquis el cero de los decimales se escribe arriba, como °).
    public static string Cota(string texto)
    {
        string t = texto.Trim().Replace(',', '.').Replace("°", "").Replace("º", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v.ToString("0.0#", CultureInfo.InvariantCulture) : texto.Trim();
    }

    // ───────────────────────────── tipos internos ─────────────────────────────
    private readonly record struct P2(double X, double Y);

    private sealed class Borde
    {
        public bool H;            // horizontal: C es la y; vertical: C es la x
        public double C;          // coordenada en píxeles
        public double A0, A1;     // de dónde a dónde va (x si es horizontal, y si es vertical)
        public int Duenio;        // -1 manzana; si no, el vecino
        public double CDib;       // coordenada final en el dibujo
        public Borde? Cara;       // la cara de manzana que acompaña (tuberías)
        public double Largo => Math.Abs(A1 - A0);
        public double Min => Math.Min(A0, A1);
        public double Max => Math.Max(A0, A1);
    }

    private sealed class Vecino
    {
        public int Indice;
        public List<P2> Original = [];
        public List<Borde> Bordes = [];
        public Lado Lado;
        public double Frente;      // coordenada (px) del borde que da a la manzana
        public double Via;         // lo que mide la vía en el croquis (px, con signo hacia afuera)
        public double Corrimiento; // cuánto se aleja (px) para que la vía quede de su ancho final
    }

    private sealed record Vertice(Tubo Tubo, int Indice, int Extremo); // Indice: entre el tramo Indice-1 y el Indice; Extremo -1/1 en los extremos

    private static Vertice? VerticeCercano(IEnumerable<Tubo> tubos, P2 q, Vertice? excepto)
    {
        Vertice? mejor = null;
        double distancia = double.MaxValue;
        foreach (Tubo t in tubos)
        {
            int n = t.Original.Count;
            for (int i = 0; i < n; i++)
            {
                if (t.Cerrada && i == n) continue;
                int extremo = t.Cerrada ? 0 : i == 0 ? -1 : i == n - 1 ? 1 : 0;
                if (excepto != null && excepto.Tubo == t && excepto.Indice == i) continue;
                double d = Math.Sqrt((t.Original[i].X - q.X) * (t.Original[i].X - q.X) + (t.Original[i].Y - q.Y) * (t.Original[i].Y - q.Y));
                if (d < distancia) { distancia = d; mejor = new Vertice(t, i, extremo); }
            }
        }
        return distancia < 300 ? mejor : null;
    }

    private sealed class Tubo
    {
        public bool Nueva, Cruce, Cerrada;
        public string Diametro = "3/4";
        public string[]? Diametros;
        public List<string?> Cotas = [];
        public List<P2> Original = [];
        public List<Borde> Tramos = [];
        public Func<double, double> X = x => x, Y = y => y;
        public double? InicioA, FinA;   // coordenada a lo largo del primer / último tramo, fijada al empalmar
        public bool Reemplazada;         // existente que se dibuja completa desde el anillo vecino ya calcado
        public TuberiaCroquis? Fuente;   // lo que leyó la IA

        public bool ExtremoFijo(int extremo) => extremo < 0 ? InicioA.HasValue : FinA.HasValue;

        // El extremo se apoya en una tubería perpendicular cuya coordenada (en el dibujo) es c.
        public void FijarExtremo(int extremo, double c, bool perpendicularA)
        {
            Borde t = extremo < 0 ? Tramos[0] : Tramos[^1];
            if (t.H == perpendicularA) { t.CDib = c; return; } // colineal: sigue la misma línea
            if (extremo < 0) InicioA = c; else FinA = c;
        }

        public P2 PuntoExtremo(int extremo) => extremo < 0 ? Extremos(0).a : Extremos(Tramos.Count - 1).b;

        public P2 Punto(Vertice v)
        {
            if (v.Extremo < 0) return Extremos(0).a;
            if (v.Extremo > 0) return Extremos(Tramos.Count - 1).b;
            int i = v.Indice % Tramos.Count;
            return Extremos(i).a;
        }

        public IEnumerable<Borde> Incidentes(Vertice v)
        {
            int n = Tramos.Count;
            if (v.Extremo < 0) { yield return Tramos[0]; yield break; }
            if (v.Extremo > 0) { yield return Tramos[^1]; yield break; }
            yield return Tramos[(v.Indice - 1 + n) % n];
            yield return Tramos[v.Indice % n];
        }

        // Inicio y fin del tramo i en el dibujo.
        public (P2 a, P2 b) Extremos(int i)
        {
            int n = Tramos.Count;
            Borde t = Tramos[i];
            P2 a = i == 0 && !Cerrada ? Libre(t, true) : Union(Tramos[(i - 1 + n) % n], t, entrando: true);
            P2 b = i == n - 1 && !Cerrada ? Libre(t, false) : Union(t, Tramos[(i + 1) % n], entrando: false);
            return (a, b);
        }

        private P2 Libre(Borde t, bool inicio)
        {
            double? fijo = inicio ? InicioA : FinA;
            double a = fijo ?? (t.H ? X(inicio ? t.A0 : t.A1) : Y(inicio ? t.A0 : t.A1));
            return t.H ? new P2(a, t.CDib) : new P2(t.CDib, a);
        }

        // Punto donde se juntan dos tramos seguidos (a → b); si son paralelos, el del lado que se pide.
        private P2 Union(Borde a, Borde b, bool entrando)
        {
            if (a.H != b.H) return a.H ? new P2(b.CDib, a.CDib) : new P2(a.CDib, b.CDib);
            Borde propio = entrando ? b : a;
            double en = a.H ? X(a.A1) : Y(a.A1);
            return propio.H ? new P2(en, propio.CDib) : new P2(propio.CDib, en);
        }

        public List<P2> Polilinea()
        {
            var puntos = new List<P2>();
            for (int i = 0; i < Tramos.Count; i++)
            {
                (P2 a, P2 b) = Extremos(i);
                if (puntos.Count == 0 || Math.Abs(puntos[^1].X - a.X) > 1e-6 || Math.Abs(puntos[^1].Y - a.Y) > 1e-6) puntos.Add(a);
                puntos.Add(b);
            }
            if (Cerrada && puntos.Count > 1 && Math.Abs(puntos[0].X - puntos[^1].X) < 1e-6 && Math.Abs(puntos[0].Y - puntos[^1].Y) < 1e-6) puntos.RemoveAt(puntos.Count - 1);
            return puntos.Select(q => new P2(Math.Round(q.X, 4), Math.Round(q.Y, 4))).ToList();
        }

        public IEnumerable<(P2 a, P2 b, Borde tramo)> Segmentos()
        {
            for (int i = 0; i < Tramos.Count; i++)
            {
                (P2 a, P2 b) = Extremos(i);
                yield return (new P2(Math.Round(a.X, 4), Math.Round(a.Y, 4)), new P2(Math.Round(b.X, 4), Math.Round(b.Y, 4)), Tramos[i]);
            }
        }
    }
}
