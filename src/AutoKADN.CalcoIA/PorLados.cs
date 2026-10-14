using System.Globalization;
using System.Text.Json;

namespace AutoKADN.CalcoIA;

// Lectura por lados (CroquisLados → Plano). De la hoja solo se toman la forma de la manzana y sus predios (a escala, a
// 90°) y, a lo largo de cada lado, dónde empieza y termina cada tubería y dónde van los accesorios. Lo demás sale de los
// parámetros, siempre igual:
//  · cada lado, desde la cara de la manzana hacia afuera: carril 1, carril 2… a 1×, 2×… la separación; la línea de
//    bordillo (LB) a una separación más; la vía de anchoVia; la cara de la manzana de enfrente. Una vecina pegada (otro
//    lote) es una línea en la cara misma, sin calle.
//  · la tubería que da la vuelta a la esquina se junta en el cruce de sus carriles; la que sigue fuera de la hoja llega al
//    borde del plano.
//  · anillo azul, troncal roja; nueva continua, existente punteada. Cotas verdes: una por tramo nuevo, como está escrita.
public static partial class Enderezador
{
    private sealed class LadoDibujo
    {
        public Lado Lado;
        public LadoCroquis Datos = new();
        public double F;          // la cara de la manzana (y si el lado es horizontal, x si es vertical), en el dibujo
        public double Signo;      // hacia afuera: +1 / -1 en esa coordenada
        public double A0, A1;     // de dónde a dónde va la cara a lo largo (dibujo)
        public bool H => EsHorizontalLado(Lado);
        public int Carriles => Datos.Carriles.Count;
        public double Carril(int k) => F + Signo * k * sepGlobal;
        public double LB => F + Signo * (Math.Max(1, Carriles) + 1) * sepGlobal;
        public double Exterior => Datos.Bordillo ? LB : Carriles > 0 ? Carril(Carriles) : F;   // donde empieza la calle
        public bool Pegada => string.Equals(Datos.Enfrente, "pegada", StringComparison.OrdinalIgnoreCase);
        public bool ConManzana => string.Equals(Datos.Enfrente, "manzana", StringComparison.OrdinalIgnoreCase);
        public double Frente => Exterior + Signo * anchoViaGlobal;                              // cara de la manzana de enfrente
    }

    [ThreadStatic] private static double sepGlobal;
    [ThreadStatic] private static double anchoViaGlobal;

    private static Lado? LadoPorNombre(string? nombre) => (nombre ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "arriba" or "norte" => Lado.Norte,
        "abajo" or "sur" => Lado.Sur,
        "izquierda" or "oeste" => Lado.Oeste,
        "derecha" or "este" => Lado.Este,
        _ => null,
    };

    // recorte: la zona de la hoja que eligió el usuario (px); lo que la lectura muestra en ella cabe en el marco.
    public static ResultadoEnderezado EnderezarPorLados(CroquisLados c, Parametros p, TamanosPedido tamanos, string? anillo = null, string? diametroLayout = null, Rect? recorte = null)
    {
        var r = new ResultadoEnderezado();
        Plano plano = r.Plano;
        if (c.Manzana.Length < 3) throw new ArgumentException("falta el contorno de la manzana (manzana: al menos 3 puntos)");
        sepGlobal = p.Separacion;
        anchoViaGlobal = p.AnchoVia;

        // ── Manzana a 90° ──
        List<Borde> bordes = Ortogonal(Puntos(c.Manzana), cerrada: true, fusionar: true, duenio: -1);
        List<P2> vm = Vertices(bordes, cerrada: true, null);
        double mx0 = vm.Min(v => v.X), mx1 = vm.Max(v => v.X), my0 = vm.Min(v => v.Y), my1 = vm.Max(v => v.Y);

        var lados = new Dictionary<Lado, LadoDibujo>();
        foreach (Lado l in new[] { Lado.Norte, Lado.Sur, Lado.Oeste, Lado.Este })
            lados[l] = new LadoDibujo { Lado = l, Signo = l is Lado.Norte or Lado.Este ? 1 : -1 };
        foreach (var (nombre, datos) in c.Lados)
            if (LadoPorNombre(nombre) is Lado l && datos != null) lados[l].Datos = datos;

        // ── Escala: la manzana entera en el marco, con lo de cada lado afuera (carriles, LB, vía y su nombre) ──
        double Afuera(LadoDibujo d)
        {
            if (d.Datos.Abierta) return 0;
            double carriles = d.Datos.Bordillo ? (Math.Max(1, d.Carriles) + 1) * p.Separacion : d.Carriles * p.Separacion;
            if (d.Pegada) return Math.Max(carriles, 2 * tamanos.Predial + 1);
            if (d.ConManzana) return carriles + p.AnchoVia + 1;
            if (!string.IsNullOrWhiteSpace(d.Datos.Via)) return carriles + p.OffsetCota + (d.H ? p.NombreViaSobreCota : p.NombreViaLateral) + 2;
            return carriles + (d.Carriles > 0 ? p.OffsetCota + 2 : 0);
        }
        Marco m = p.Marco;
        double eN = Afuera(lados[Lado.Norte]), eS = Afuera(lados[Lado.Sur]), eO = Afuera(lados[Lado.Oeste]), eE = Afuera(lados[Lado.Este]);
        // Lo que se ve de la hoja cabe en el marco: la manzana y las tuberías que siguen más allá de ella a lo largo de su
        // lado (hasta donde terminan, o hasta el borde de la zona elegida si salen de la hoja).
        double bx0 = mx0, bx1 = mx1, by0 = my0, by1 = my1;
        if (recorte is Rect ra)
        {
            if (lados[Lado.Norte].Datos.Abierta) by0 = Math.Min(by0, ra.Y);
            if (lados[Lado.Sur].Datos.Abierta) by1 = Math.Max(by1, ra.Y1);
            if (lados[Lado.Oeste].Datos.Abierta) bx0 = Math.Min(bx0, ra.X);
            if (lados[Lado.Este].Datos.Abierta) bx1 = Math.Max(bx1, ra.X1);
        }
        foreach (var (nombre, l) in c.Lados)
        {
            if (LadoPorNombre(nombre) is not Lado lado || l == null) continue;
            bool horizontal = EsHorizontalLado(lado);
            double medio = horizontal ? (mx0 + mx1) / 2 : (my0 + my1) / 2;
            foreach (CarrilCroquis carril in l.Carriles)
                foreach (var (e, otra) in new[] { (carril.Desde, carril.Hasta), (carril.Hasta, carril.Desde) })
                {
                    double? x = Valor(e);
                    // «borde»: el borde de la zona elegida del lado por donde sale (el contrario a su otra punta).
                    if (x == null && EsBorde(e) && recorte is Rect rb)
                        x = (Valor(otra) ?? medio) > medio ? (horizontal ? rb.X : rb.Y) : (horizontal ? rb.X1 : rb.Y1);
                    if (x is not double v) continue;
                    if (horizontal) { bx0 = Math.Min(bx0, v); bx1 = Math.Max(bx1, v); } else { by0 = Math.Min(by0, v); by1 = Math.Max(by1, v); }
                }
        }
        // Escala: a cada lado de la manzana va lo que más pida, lo de afuera (carriles, LB, vía; en unidades) o lo que la
        // hoja muestra más allá de la cara (en px, a escala). La mayor escala con la que todo cabe, y centrado.
        double extIzq = mx0 - bx0, extDer = bx1 - mx1, extArr = my0 - by0, extAba = by1 - my1;
        double Ancho(double e) => Math.Max(extIzq * e, eO) + (mx1 - mx0) * e + Math.Max(extDer * e, eE);
        double Alto(double e) => Math.Max(extArr * e, eN) + (my1 - my0) * e + Math.Max(extAba * e, eS);
        double anchoTotal = m.Ancho - 2 * p.Margen, altoTotal = m.Alto - 2 * p.Margen;
        double bajo = 0, tope = Math.Min(anchoTotal / Math.Max(1, bx1 - bx0 + 1), altoTotal / Math.Max(1, by1 - by0 + 1)) * 4;
        for (int i = 0; i < 60; i++) { double medio = (bajo + tope) / 2; if (Ancho(medio) <= anchoTotal && Alto(medio) <= altoTotal) bajo = medio; else tope = medio; }
        double s = bajo;
        if (s <= 0) throw new ArgumentException("lo de los lados no deja espacio para la manzana en el marco");
        double ox = m.X0 + p.Margen + (anchoTotal - Ancho(s)) / 2 + Math.Max(extIzq * s, eO);
        double oy = m.Y1 - p.Margen - (altoTotal - Alto(s)) / 2 - Math.Max(extArr * s, eN);
        double X(double x) => Math.Round(ox + (x - mx0) * s, 4);
        double Y(double y) => Math.Round(oy - (y - my0) * s, 4);
        double[] D(P2 q) => [X(q.X), Y(q.Y)];
        r.Sx = r.Sy = s;
        (r.Ox, r.Oy, r.Izquierda, r.Arriba) = (ox, oy, mx0, my0);
        r.Resumen.Add(FormattableString.Invariant($"Escala {s:0.####} u/px (lectura por lados: tuberías, LB y vías a la separación de {p.Separacion})"));

        lados[Lado.Norte].F = Y(my0); lados[Lado.Sur].F = Y(my1); lados[Lado.Oeste].F = X(mx0); lados[Lado.Este].F = X(mx1);
        foreach (LadoDibujo d in lados.Values)
        {
            if (d.H) { d.A0 = X(mx0); d.A1 = X(mx1); } else { d.A0 = Y(my1); d.A1 = Y(my0); }
        }
        double ALoLargo(LadoDibujo d, double px) => d.H ? X(px) : Y(px);
        // Los lados que se tocan en cada punta de un lado: la punta de menor coordenada y la de mayor.
        (Lado menor, Lado mayor) Vecinos(LadoDibujo d) => d.H ? (Lado.Oeste, Lado.Este) : (Lado.Sur, Lado.Norte);

        plano.CentroManzana = [X((mx0 + mx1) / 2), Y((my0 + my1) / 2)];
        var abiertos = lados.Values.Where(d => d.Datos.Abierta).ToList();
        if (abiertos.Count == 0) plano.Lineas.Add(new LineaPlano { Tipo = "manzana", Puntos = vm.Select(D).ToList(), Cerrada = true });
        else
        {
            // Sin las aristas que van sobre un lado abierto; las que llegan a él siguen hasta el borde del plano.
            var esquinas = vm.Select(D).ToList();
            double Borde(LadoDibujo d) => d.Lado switch { Lado.Norte => m.Y1, Lado.Sur => m.Y0, Lado.Oeste => m.X0, _ => m.X1 };
            bool Sobre(double[] q, LadoDibujo d) => Math.Abs((d.H ? q[1] : q[0]) - d.F) < 1e-6;
            for (int i = 0; i < esquinas.Count; i++)
            {
                double[] a = [.. esquinas[i]], b = [.. esquinas[(i + 1) % esquinas.Count]];
                if (abiertos.Any(d => Sobre(a, d) && Sobre(b, d))) continue;
                foreach (LadoDibujo d in abiertos)
                {
                    if (Sobre(a, d)) a[d.H ? 1 : 0] = Borde(d);
                    if (Sobre(b, d)) b[d.H ? 1 : 0] = Borde(d);
                }
                plano.Lineas.Add(new LineaPlano { Tipo = "manzana", Puntos = [a, b] });
            }
            r.Resumen.Add("Manzana abierta hacia " + string.Join(", ", abiertos.Select(d => Nombre(d.Lado))) + " (la hoja la corta): sigue hasta el borde del plano");
        }

        // ── Predios: a 90°, de línea a línea (pegados a la manzana y a las otras divisiones) ──
        double tamano = Math.Min(mx1 - mx0, my1 - my0);
        List<Borde> predios = c.Predios.Where(x => x.Length >= 2).Select(x => Ortogonal(Puntos(x).Take(2).ToList(), false, false, -1)[0]).ToList();
        double empalme = Math.Max(Empalmar, 0.05 * tamano);
        foreach (Borde b in predios)
        {
            // Cada punta a la línea perpendicular más cercana (la manzana u otra división).
            var perpendiculares = bordes.Concat(predios).Where(o => o != b && o.H != b.H).ToList();
            double Pegar(double a) => perpendiculares.Where(o => b.C >= o.Min - empalme && b.C <= o.Max + empalme)
                .Select(o => o.C).OrderBy(cc => Math.Abs(cc - a)).Where(cc => Math.Abs(cc - a) <= empalme).DefaultIfEmpty(a).First();
            b.A0 = Pegar(b.A0); b.A1 = Pegar(b.A1);
            if (b.Largo < 1) continue;
            plano.Lineas.Add(new LineaPlano { Tipo = "predio", Puntos = b.H ? [[X(b.A0), Y(b.C)], [X(b.A1), Y(b.C)]] : [[X(b.C), Y(b.A0)], [X(b.C), Y(b.A1)]] });
        }

        // ── Tuberías por carril ──
        // Un carril es una línea de tubería; a lo largo puede cambiar (nueva en un tramo, existente en otro): cada tramo
        // lleva su estado y su cota. A y B son sus puntas en el dibujo, en el orden de «desde» a «hasta».
        double tolEsquina(LadoDibujo d) => Math.Max(40, 0.08 * (d.H ? mx1 - mx0 : my1 - my0));
        string Estado(CarrilCroquis carril, int tramo) => carril.Tramos.Count > tramo && !string.IsNullOrWhiteSpace(carril.Tramos[tramo].Estado) ? carril.Tramos[tramo].Estado! : carril.Estado;
        var segmentos = new List<(LadoDibujo D, int K, CarrilCroquis C, double A, double B)>();
        foreach (LadoDibujo d in lados.Values)
            for (int k = 1; k <= d.Carriles; k++)
            {
                CarrilCroquis carril = d.Datos.Carriles[k - 1];
                double? desde = Valor(carril.Desde), hasta = Valor(carril.Hasta);
                double a = EsBorde(carril.Desde) ? double.NaN : desde is double dv ? ALoLargo(d, dv) : double.NaN;
                double b = EsBorde(carril.Hasta) ? double.NaN : hasta is double hv ? ALoLargo(d, hv) : double.NaN;
                // Sin dar: de esquina a esquina; «borde»: hasta el borde del plano, del lado que corresponda.
                if (double.IsNaN(a) && double.IsNaN(b)) { a = EsBorde(carril.Desde) ? double.NegativeInfinity : d.A0; b = EsBorde(carril.Hasta) ? double.PositiveInfinity : d.A1; }
                else if (double.IsNaN(a)) a = EsBorde(carril.Desde) ? (b > (d.A0 + d.A1) / 2 ? double.NegativeInfinity : double.PositiveInfinity) : (b > (d.A0 + d.A1) / 2 ? d.A0 : d.A1);
                else if (double.IsNaN(b)) b = EsBorde(carril.Hasta) ? (a > (d.A0 + d.A1) / 2 ? double.NegativeInfinity : double.PositiveInfinity) : (a > (d.A0 + d.A1) / 2 ? d.A0 : d.A1);
                segmentos.Add((d, k, carril, a, b));
            }
        // Esquinas: dos carriles de la misma red, de lados que se tocan, que llegan los dos a esa esquina se juntan en el
        // cruce de sus líneas (si de un lado es nueva y del otro existente, ahí empalman).
        for (int i = 0; i < segmentos.Count; i++)
            for (int j = 0; j < segmentos.Count; j++)
            {
                var u = segmentos[i]; var v = segmentos[j];
                if (u.D.H == v.D.H || !Igual(u.C.Red, v.C.Red)) continue;
                double esquinaU = v.D.F, esquinaV = u.D.F;   // a lo largo de u, la cara de v; y al revés
                double tolU = tolEsquina(u.D) * s, tolV = tolEsquina(v.D) * s;
                bool Llega(double x, double esquina, double tol, double signoOtro) => !double.IsInfinity(x) && (Math.Abs(x - esquina) <= tol || (signoOtro > 0 ? x > esquina : x < esquina));
                bool uA = Llega(u.A, esquinaU, tolU, v.D.Signo), uB = Llega(u.B, esquinaU, tolU, v.D.Signo);
                bool vLlega = Llega(v.A, esquinaV, tolV, u.D.Signo) || Llega(v.B, esquinaV, tolV, u.D.Signo);
                if (!(uA || uB) || !vLlega) continue;
                double cruceU = v.D.Carril(v.K);
                if (uA && (!uB || Math.Abs(u.A - esquinaU) <= Math.Abs(u.B - esquinaU))) u.A = cruceU; else u.B = cruceU;
                segmentos[i] = u;
            }
        double BordeMarco(LadoDibujo d, double valor) => double.IsNegativeInfinity(valor) ? (d.H ? m.X0 : m.Y0) : double.IsPositiveInfinity(valor) ? (d.H ? m.X1 : m.Y1) : valor;
        P2 Punto(LadoDibujo d, int k, double aLoLargo) => d.H ? new P2(aLoLargo, d.Carril(k)) : new P2(d.Carril(k), aLoLargo);
        var lineasCarril = new Dictionary<(Lado, int), (P2 A, P2 B, CarrilCroquis C)>();
        var puntasNuevas = new List<(P2 Punto, string Diametro)>();     // puntas de tramos nuevos
        var puntasExistentes = new List<P2>();                          // puntas de tramos existentes
        var empalmes = new List<(P2 Punto, string Diametro)>();          // donde la nueva sigue como existente: unión
        foreach (var (d, k, carril, a0, b0) in segmentos)
        {
            double a = BordeMarco(d, a0), b = BordeMarco(d, b0);
            lineasCarril[(d.Lado, k)] = (Punto(d, k, a), Punto(d, k, b), carril);
            string diametro = Normalizar(string.IsNullOrWhiteSpace(carril.Diametro) ? (string.IsNullOrWhiteSpace(diametroLayout) ? "3/4" : diametroLayout) : carril.Diametro);
            // Tramos en orden de «desde» a «hasta»; el último termina en la punta del carril.
            var limites = new List<double> { a };
            int n = Math.Max(1, carril.Tramos.Count);
            for (int t = 0; t < n - 1; t++) limites.Add(Math.Clamp(ALoLargo(d, carril.Tramos[t].Hasta), Math.Min(a, b), Math.Max(a, b)));
            limites.Add(b);
            for (int t = 0; t < n; t++)
            {
                bool nueva = Igual(Estado(carril, t), "nueva"), troncal = Igual(carril.Red, "troncal");
                string tipo = troncal ? (nueva ? "troncal_nueva" : "troncal_existente") : (nueva ? "tuberia_nueva" : "tuberia_existente");
                P2 qa = Punto(d, k, limites[t]), qb = Punto(d, k, limites[t + 1]);
                if (Math.Abs(limites[t + 1] - limites[t]) < 1e-6) continue;
                plano.Lineas.Add(new LineaPlano { Tipo = tipo, Puntos = [[qa.X, qa.Y], [qb.X, qb.Y]] });
                if (nueva) { puntasNuevas.Add((qa, diametro)); puntasNuevas.Add((qb, diametro)); } else { puntasExistentes.Add(qa); puntasExistentes.Add(qb); }
                if (t > 0 && Igual(Estado(carril, t - 1), "nueva") != nueva) empalmes.Add((qa, diametro));
                if (!nueva) continue;
                string? cota = carril.Tramos.Count > t ? carril.Tramos[t].Cota : carril.Cota;
                if (string.IsNullOrWhiteSpace(cota)) { r.Avisos.Add($"tramo nuevo sin cota en el lado {Nombre(d.Lado)} (carril {k})"); continue; }
                double[] lado = d.H ? [(qa.X + qb.X) / 2, qa.Y + d.Signo * 10] : [qa.X + d.Signo * 10, (qa.Y + qb.Y) / 2];
                plano.Cotas.Add(new CotaPlano { A = [qa.X, qa.Y], B = [qb.X, qb.Y], Texto = Cota(cota!), Diametro = diametro, Lado = lado });
            }
        }
        // La punta de un tramo nuevo que toca una punta de tramo existente (en la esquina) también es un empalme.
        foreach (var (punta, diametro) in puntasNuevas)
            if (puntasExistentes.Any(q => Math.Abs(q.X - punta.X) + Math.Abs(q.Y - punta.Y) < 0.5) && !empalmes.Any(e => Math.Abs(e.Punto.X - punta.X) + Math.Abs(e.Punto.Y - punta.Y) < 0.5))
                empalmes.Add((punta, diametro));

        // ── Líneas de bordillo, vecinas pegadas, manzanas de enfrente y nombres de vías ──
        double Punta(LadoDibujo d, bool mayor)
        {
            // Hasta dónde llega lo de este lado en cada punta: la LB del lado que se toca, o su cara si allá hay una vecina
            // pegada, o lo de ese lado hacia afuera.
            var (menorL, mayorL) = Vecinos(d);
            LadoDibujo o = lados[mayor ? mayorL : menorL];
            if (o.Datos.Bordillo) return o.LB;
            if (o.Pegada) return o.F;
            return o.Exterior;
        }
        foreach (LadoDibujo d in lados.Values)
        {
            double a = Punta(d, false), b = Punta(d, true);
            if (d.Datos.Bordillo)
                plano.Lineas.Add(new LineaPlano { Tipo = "bordillo", Puntos = d.H ? [[a, d.LB], [b, d.LB]] : [[d.LB, a], [d.LB, b]] });
            if (d.Pegada)
            {
                double fa = d.H ? m.X0 : m.Y0, fb = d.H ? m.X1 : m.Y1;
                plano.Lineas.Add(new LineaPlano { Tipo = "vecino", Puntos = d.H ? [[fa, d.F], [fb, d.F]] : [[d.F, fa], [d.F, fb]] });
                if (!string.IsNullOrWhiteSpace(d.Datos.RotuloEnfrente))
                    plano.Textos.Add(new TextoPlano
                    {
                        Texto = d.Datos.RotuloEnfrente!.Trim().ToUpperInvariant(), Tipo = "libre", Fijo = true, Giro = d.H ? 0 : 90,
                        X = d.H ? (d.A0 + d.A1) / 2 : d.F + d.Signo * (tamanos.Predial + 0.5), Y = d.H ? d.F + d.Signo * (tamanos.Predial + 0.5) : (d.A0 + d.A1) / 2,
                    });
            }
            if (d.ConManzana)
                plano.Lineas.Add(new LineaPlano { Tipo = "vecino", Puntos = d.H ? [[a, d.Frente], [b, d.Frente]] : [[d.Frente, a], [d.Frente, b]] });
            if (!string.IsNullOrWhiteSpace(d.Datos.Via) && !d.Pegada)
            {
                double centro = d.ConManzana ? (d.Exterior + d.Frente) / 2 : d.Exterior + d.Signo * (p.OffsetCota + (d.H ? p.NombreViaSobreCota : p.NombreViaLateral));
                plano.Textos.Add(new TextoPlano
                {
                    Texto = d.Datos.Via!.Trim().ToUpperInvariant(), Tipo = "vial", Giro = d.H ? 0 : 90,
                    X = d.H ? (d.A0 + d.A1) / 2 : centro, Y = d.H ? centro : (d.A0 + d.A1) / 2,
                });
            }
        }

        // ── Accesorios sobre su carril ──
        var accesorios = new List<(AccesorioPlano A, Lado L, int K)>();
        foreach (AccesorioLado acc in c.Accesorios)
        {
            if (LadoPorNombre(acc.Lado) is not Lado l || !lineasCarril.TryGetValue((l, Math.Max(1, acc.Carril)), out var linea))
            {
                r.Avisos.Add($"el accesorio {acc.Nombre} no está en un carril que exista (lado «{acc.Lado}», carril {acc.Carril}); se omitió");
                continue;
            }
            LadoDibujo d = lados[l];
            double a = d.H ? linea.A.X : linea.A.Y, b = d.H ? linea.B.X : linea.B.Y;
            double en = Math.Clamp(ALoLargo(d, acc.En), Math.Min(a, b), Math.Max(a, b));
            // Cerca de una punta, en la punta (tapones).
            if (Math.Abs(en - a) < 2) en = a; else if (Math.Abs(en - b) < 2) en = b;
            P2 q = Punto(d, Math.Max(1, acc.Carril), en);
            string nombre = acc.Nombre.Trim().ToUpperInvariant();
            Vector2 dir = d.H ? new Vector2(1, 0) : new Vector2(0, 1);
            double direccion = nombre switch
            {
                "TAPON" => Math.Abs(en - a) <= Math.Abs(en - b) ? Angulo(dir) : Angulo(-dir),
                "TEE" => d.H ? (d.Signo > 0 ? 270 : 90) : (d.Signo > 0 ? 180 : 0),   // el ramal, hacia la manzana o hacia la calle (se ajusta abajo)
                _ => Angulo(dir),
            };
            string diametro = Normalizar(acc.Diametro ?? linea.C.Diametro ?? diametroLayout ?? "3/4");
            accesorios.Add((new AccesorioPlano { Nombre = nombre, X = q.X, Y = q.Y, Direccion = direccion, Diametro = diametro, Superficie = string.IsNullOrWhiteSpace(c.Superficie) ? p.Superficie : c.Superficie!.Trim().ToUpperInvariant() }, l, Math.Max(1, acc.Carril)));
        }
        // Empalme con silleta: la silleta frente a la tee (en la misma posición a lo largo del lado) y la tee mirando hacia ella.
        foreach (var (silleta, l, k) in accesorios.Where(x => x.A.Nombre == "SILLETA").ToList())
        {
            var tee = accesorios.Where(x => x.A.Nombre == "TEE" && x.L == l && x.K != k)
                .OrderBy(x => lados[l].H ? Math.Abs(x.A.X - silleta.X) : Math.Abs(x.A.Y - silleta.Y)).FirstOrDefault();
            if (tee.A == null || (lados[l].H ? Math.Abs(tee.A.X - silleta.X) : Math.Abs(tee.A.Y - silleta.Y)) > 0.15 * tamano * s) continue;
            if (lados[l].H) silleta.X = tee.A.X; else silleta.Y = tee.A.Y;
            tee.A.Direccion = Angulo(new Vector2(silleta.X - tee.A.X, silleta.Y - tee.A.Y));
        }
        // Donde la tubería nueva empalma con la existente va una unión, no un tapón: el tapón es solo para la punta libre.
        int cambiados = 0, agregadas = 0;
        foreach (var (punto, diametro) in empalmes)
        {
            var ahi = accesorios.Where(x => Math.Abs(x.A.X - punto.X) + Math.Abs(x.A.Y - punto.Y) < 2).ToList();
            foreach (var x in ahi.Where(x => x.A.Nombre == "TAPON")) { x.A.Nombre = "UNION"; cambiados++; }
            if (ahi.Count == 0)
            {
                var (ld, lk) = lineasCarril.Where(kv => DistanciaSegmento(punto, kv.Value.A, kv.Value.B) < 0.5).Select(kv => kv.Key).FirstOrDefault();
                Vector2 dir = lados[ld].H ? new Vector2(1, 0) : new Vector2(0, 1);
                accesorios.Add((new AccesorioPlano { Nombre = "UNION", X = punto.X, Y = punto.Y, Direccion = Angulo(dir), Diametro = diametro, Superficie = string.IsNullOrWhiteSpace(c.Superficie) ? p.Superficie : c.Superficie!.Trim().ToUpperInvariant() }, ld, lk));
                agregadas++;
            }
        }
        // Una unión en la punta de la tubería nueva es un empalme con la red existente: si ahí no sigue ninguna, falta leerla.
        foreach (var (union, l, k) in accesorios.Where(x => x.A.Nombre == "UNION"))
        {
            bool enPunta = puntasNuevas.Any(q => Math.Abs(q.Punto.X - union.X) + Math.Abs(q.Punto.Y - union.Y) < 0.5)
                && puntasNuevas.Count(q => Math.Abs(q.Punto.X - union.X) + Math.Abs(q.Punto.Y - union.Y) < 0.5) == 1;
            bool conExistente = puntasExistentes.Any(q => Math.Abs(q.X - union.X) + Math.Abs(q.Y - union.Y) < 0.5);
            if (enPunta && !conExistente) r.UnionesSueltas.Add($"lado {Nombre(l)}, carril {k}");
        }
        if (cambiados + agregadas > 0) r.Resumen.Add($"Empalmes de la tubería nueva con la existente: unión ({cambiados} tapón(es) cambiado(s) por unión, {agregadas} unión(es) agregada(s))");
        plano.Accesorios.AddRange(accesorios.Select(x => x.A));

        // ── Notas: pasado lo de su lado (la LB o el último carril), con la flecha al carril ──
        foreach (NotaLado n in c.Notas.Where(n => !string.IsNullOrWhiteSpace(n.Texto)))
        {
            if (LadoPorNombre(n.Lado) is not Lado l || !lineasCarril.TryGetValue((l, Math.Max(1, n.Carril)), out var linea)) continue;
            LadoDibujo d = lados[l];
            double a = d.H ? linea.A.X : linea.A.Y, b = d.H ? linea.B.X : linea.B.Y;
            double en = Math.Clamp(ALoLargo(d, n.En), Math.Min(a, b), Math.Max(a, b));
            P2 flecha = Punto(d, Math.Max(1, n.Carril), en);
            AccesorioPlano? ahi = plano.Accesorios.OrderBy(x => Math.Abs(x.X - flecha.X) + Math.Abs(x.Y - flecha.Y)).FirstOrDefault();
            if (ahi != null && Math.Abs(ahi.X - flecha.X) + Math.Abs(ahi.Y - flecha.Y) < 3) flecha = new P2(ahi.X, ahi.Y);
            string[] renglones = n.Texto.Replace("\r", string.Empty).Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            double alto = tamanos.Predial, ancho = renglones.Max(x => x.Length) * 0.62 * alto, largo = renglones.Length * 1.6 * alto;
            double fuera = d.Exterior + d.Signo * 2;
            double nx = d.H ? flecha.X - 2 : (d.Signo > 0 ? fuera : fuera - ancho), ny = d.H ? (d.Signo > 0 ? fuera + largo : fuera) : flecha.Y + largo / 2;
            nx = Math.Clamp(nx, m.X0 + 1, m.X1 - ancho - 1);
            ny = Math.Clamp(ny, m.Y0 + largo + 1, m.Y1 - 1);
            plano.Notas.Add(new NotaPlano { Texto = string.Join("\n", renglones), X = Math.Round(nx, 4), Y = Math.Round(ny, 4), Flecha = [flecha.X, flecha.Y] });
        }
        // El nombre de la vía no va encima de una nota: se corre a lo largo de la vía.
        foreach (TextoPlano via in plano.Textos.Where(t => t.Tipo == "vial"))
            foreach (NotaPlano n in plano.Notas)
            {
                string[] renglones = n.Texto.Split('\n');
                double nx0 = n.X - 1, nx1 = n.X + renglones.Max(x => x.Length) * 0.62 * tamanos.Predial + 1, ny0 = n.Y - renglones.Length * 1.6 * tamanos.Predial - 1, ny1 = n.Y + 1;
                double largoTexto = via.Texto.Length * 0.75 * tamanos.Vial;
                bool vertical = Math.Abs(via.Giro) > 45;
                double tx0 = vertical ? via.X - tamanos.Vial : via.X - largoTexto / 2, tx1 = vertical ? via.X + tamanos.Vial : via.X + largoTexto / 2;
                double ty0 = vertical ? via.Y - largoTexto / 2 : via.Y - tamanos.Vial, ty1 = vertical ? via.Y + largoTexto / 2 : via.Y + tamanos.Vial;
                if (tx1 < nx0 || tx0 > nx1 || ty1 < ny0 || ty0 > ny1) continue;
                if (vertical) via.Y = (ty0 + ty1) / 2 > (ny0 + ny1) / 2 ? ny1 + largoTexto / 2 + 1 : ny0 - largoTexto / 2 - 1;
                else via.X = (tx0 + tx1) / 2 > (nx0 + nx1) / 2 ? nx1 + largoTexto / 2 + 1 : nx0 - largoTexto / 2 - 1;
            }

        // ── Textos de predio (centrados en su predio, como NOMENK) e indicadores ──
        foreach (TextoCroquis t in c.Textos.Where(t => !string.IsNullOrWhiteSpace(t.Texto)))
            plano.Textos.Add(new TextoPlano { Texto = t.Texto.Trim().ToUpperInvariant(), X = X(t.X), Y = Y(t.Y), Giro = Math.Abs(t.Giro) > 45 ? 90 : 0, Tipo = "predial" });
        CentrarEnPredios(plano, m, tamanos, r);
        foreach (IndicadorCroquis ind in c.Indicadores)
        {
            var lineas = plano.Lineas.SelectMany(Segmentos).ToList();
            var cajas = plano.Textos.Select(t => Caja(t, tamanos)).ToList();
            var (ix, iy) = UbicarIndicador(X(ind.X), Y(ind.Y), ind.Viviendas, p, tamanos, lineas, cajas, (X(mx0), Y(my1), X(mx1), Y(my0)));
            plano.Indicadores.Add(new IndicadorPlano { Numero = ind.Numero, Diametro = Normalizar(string.IsNullOrWhiteSpace(diametroLayout) ? ind.Diametro : diametroLayout), Viviendas = ind.Viviendas, X = ix, Y = iy });
        }
        if (c.Indicadores.Count == 0 && anillo != null) r.Avisos.Add($"no se leyó el indicador del anillo {anillo}");

        // ── Resumen ──
        foreach (var g in plano.Cotas.GroupBy(x => x.Diametro))
        {
            double total = g.Sum(x => double.TryParse(x.Texto, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0);
            r.Resumen.Add(FormattableString.Invariant($"Tubería {g.Key}\": {total:0.0#} ML"));
        }
        foreach (var g in plano.Accesorios.GroupBy(x => (x.Nombre, x.Diametro)).OrderBy(g => g.Key.Nombre))
            r.Resumen.Add($"{g.Key.Nombre} {g.Key.Diametro}\": {g.Count()}");
        foreach (LadoDibujo d in lados.Values.Where(d => d.Carriles > 0 || d.Datos.Bordillo || d.Pegada || d.ConManzana))
            r.Resumen.Add($"Lado {Nombre(d.Lado)}: " + string.Join(" → ", new[] { "manzana" }
                .Concat(d.Datos.Carriles.Select(x => $"{x.Red} {x.Estado}"))
                .Concat(d.Datos.Bordillo ? ["LB"] : [])
                .Concat(d.Pegada ? ["pegada"] : string.IsNullOrWhiteSpace(d.Datos.Via) ? [] : [d.Datos.Via!])
                .Concat(d.ConManzana ? ["manzana de enfrente"] : [])));
        return r;
    }

    private readonly record struct Vector2(double X, double Y)
    {
        public static Vector2 operator -(Vector2 v) => new(-v.X, -v.Y);
    }

    private static double Angulo(Vector2 v) => Math.Round(Math.Atan2(v.Y, v.X) * 180 / Math.PI, 4);

    private static bool Igual(string? a, string? b) => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static double? Valor(JsonElement? e) => e is JsonElement v && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
        : e is JsonElement w && w.ValueKind == JsonValueKind.String && double.TryParse(w.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : null;

    private static bool EsBorde(JsonElement? e) => e is JsonElement v && v.ValueKind == JsonValueKind.String && Igual(v.GetString(), "borde");
}
