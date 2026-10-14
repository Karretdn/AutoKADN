namespace AutoKADN.CalcoIA;

// La cara de la manzana vista desde un lado: los tramos del contorno paralelos a ese lado, en orden a lo largo, con C la
// coordenada de la cara (y en arriba/abajo, x en izquierda/derecha). Si la manzana tiene un escalón (un volado, una
// saliente), la cara también. Las puntas siguen de largo: hasta la esquina con el otro lado o hasta el borde del plano.
//  · Paralela: lo que va a la misma distancia de la cara, también de los escalones (tuberías, LB).
//  · Corrida: la misma figura corrida hacia afuera (la manzana de enfrente, al otro lado de la vía).
internal sealed class Frente
{
    private readonly List<(double A0, double A1, double C)> _tramos;   // sin solaparse y seguidos; el primero desde -∞, el último hasta +∞

    public double Signo { get; }      // hacia afuera, en C
    public double Inicio { get; }     // a lo largo: la esquina de la punta menor
    public double Fin { get; }        // y la de la punta mayor

    private Frente(List<(double A0, double A1, double C)> tramos, double signo, double inicio, double fin)
    {
        _tramos = tramos;
        Signo = signo;
        Inicio = inicio;
        Fin = fin;
    }

    public Frente Paralela(double distancia) =>
        new(Afuera(_tramos.Select(t => (t.A0 - distancia, t.A1 + distancia, t.C + Signo * distancia)).ToList(), Signo), Signo, Inicio, Fin);

    public Frente Corrida(double distancia) => new(_tramos.Select(t => (t.A0, t.A1, t.C + Signo * distancia)).ToList(), Signo, Inicio, Fin);

    // C en ese punto a lo largo (en un escalón, lo más afuera).
    public double En(double a)
    {
        double mejor = double.NaN;
        foreach (var t in _tramos)
            if (t.A0 - 1e-9 <= a && a <= t.A1 + 1e-9 && (double.IsNaN(mejor) || Signo * t.C > Signo * mejor)) mejor = t.C;
        return mejor;
    }

    // C en la punta menor o en la mayor (lo que llega a la esquina).
    public double Punta(bool mayor) => mayor ? _tramos[^1].C : _tramos[0].C;

    // Lo más afuera entre a y b.
    public double Extremo(double a, double b) => Entre(a, b).Select(q => q.C).MaxBy(c => Signo * c);

    // Los vértices de la línea de a a b (en ese orden), con los escalones que haya en medio.
    public List<(double A, double C)> Entre(double a, double b)
    {
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        var puntos = new List<(double A, double C)> { (lo, En(lo)) };
        for (int i = 0; i + 1 < _tramos.Count; i++)
        {
            double x = _tramos[i].A1;
            if (x <= lo + 1e-9 || x >= hi - 1e-9 || Math.Abs(_tramos[i].C - _tramos[i + 1].C) < 1e-9) continue;
            puntos.Add((x, _tramos[i].C));
            puntos.Add((x, _tramos[i + 1].C));
        }
        puntos.Add((hi, En(hi)));
        if (a > b) puntos.Reverse();
        return puntos;
    }

    // En cada punto a lo largo, el tramo más afuera de los que lo cubren; seguidos, sin solaparse, con las puntas de largo.
    private static List<(double A0, double A1, double C)> Afuera(List<(double A0, double A1, double C)> tramos, double signo)
    {
        var limites = new List<double> { double.NegativeInfinity };
        limites.AddRange(tramos.SelectMany(t => new[] { t.A0, t.A1 }).Where(double.IsFinite).Distinct().OrderBy(x => x));
        limites.Add(double.PositiveInfinity);
        var r = new List<(double A0, double A1, double C)>();
        for (int i = 0; i + 1 < limites.Count; i++)
        {
            double lo = limites[i], hi = limites[i + 1];
            double m = double.IsInfinity(lo) ? (double.IsInfinity(hi) ? 0 : hi - 1) : double.IsInfinity(hi) ? lo + 1 : (lo + hi) / 2;
            var cubren = tramos.Where(t => t.A0 <= m && m <= t.A1).ToList();
            if (cubren.Count == 0) continue;
            double c = cubren.MaxBy(t => signo * t.C).C;
            if (r.Count > 0 && Math.Abs(r[^1].C - c) < 1e-9) r[^1] = (r[^1].A0, hi, c);
            else
            {
                if (r.Count > 0) r[^1] = (r[^1].A0, lo, r[^1].C);   // sin huecos
                r.Add((lo, hi, c));
            }
        }
        if (r.Count == 0) return [(double.NegativeInfinity, double.PositiveInfinity, 0)];
        r[0] = (double.NegativeInfinity, r[0].A1, r[0].C);
        r[^1] = (r[^1].A0, double.PositiveInfinity, r[^1].C);
        return r;
    }

    // Las cuatro caras («arriba», «abajo», «izquierda», «derecha») de un contorno a 90° (vértices en orden), con los
    // vértices de cada una. yArriba: la y crece hacia arriba (el dibujo); si no, hacia abajo (la hoja). Cada cara va de
    // esquina a esquina: la esquina entre dos lados es el vértice que está más hacia afuera de los dos a la vez.
    public static Dictionary<string, (Frente Cara, HashSet<int> Vertices)> DeContorno(IReadOnlyList<(double X, double Y)> v, bool yArriba)
    {
        int n = v.Count;
        var lados = new Dictionary<string, (bool H, double Signo)>
        {
            ["arriba"] = (true, yArriba ? 1 : -1), ["abajo"] = (true, yArriba ? -1 : 1),
            ["izquierda"] = (false, -1), ["derecha"] = (false, 1),
        };
        double Fuera(int i, string lado) => lados[lado].Signo * (lados[lado].H ? v[i].Y : v[i].X);
        int Esquina(string a, string b) => Enumerable.Range(0, n).MaxBy(i => Fuera(i, a) + Fuera(i, b));
        var esquinas = new Dictionary<string, (string, string)>
        {
            ["arriba"] = ("izquierda", "derecha"), ["abajo"] = ("izquierda", "derecha"),
            ["izquierda"] = ("arriba", "abajo"), ["derecha"] = ("arriba", "abajo"),
        };
        var todas = new[] { Esquina("arriba", "izquierda"), Esquina("arriba", "derecha"), Esquina("abajo", "izquierda"), Esquina("abajo", "derecha") };
        var r = new Dictionary<string, (Frente, HashSet<int>)>();
        foreach (var (lado, (h, signo)) in lados)
        {
            double ALoLargo(int i) => h ? v[i].X : v[i].Y;
            double Cara(int i) => h ? v[i].Y : v[i].X;
            var (o1, o2) = esquinas[lado];
            int e1 = Esquina(lado, o1), e2 = Esquina(lado, o2);
            // El camino del contorno entre sus dos esquinas que no pasa por las otras.
            List<int> Camino(int paso)
            {
                var c = new List<int> { e1 };
                for (int k = e1; k != e2 && c.Count <= n;) { k = (k + paso + n) % n; c.Add(k); }
                return c;
            }
            var otras = todas.Where(i => i != e1 && i != e2).ToHashSet();
            List<int> camino = Camino(1);
            if (n < 4 || e1 == e2 || camino.Skip(1).SkipLast(1).Any(otras.Contains)) camino = Camino(-1);
            var tramos = new List<(double A0, double A1, double C)>();
            for (int i = 0; i + 1 < camino.Count; i++)
            {
                int a = camino[i], b = camino[i + 1];
                if (Math.Abs(Cara(a) - Cara(b)) > 1e-6) continue;   // perpendicular al lado: un escalón
                tramos.Add((Math.Min(ALoLargo(a), ALoLargo(b)), Math.Max(ALoLargo(a), ALoLargo(b)), Cara(a)));
            }
            // Contorno raro (sin tramos de ese lado): la cara del rectángulo que lo encierra.
            if (tramos.Count == 0) tramos.Add((Enumerable.Range(0, n).Min(ALoLargo), Enumerable.Range(0, n).Max(ALoLargo), Enumerable.Range(0, n).Select(Cara).MaxBy(c => signo * c)));
            double inicio = Math.Min(ALoLargo(e1), ALoLargo(e2)), fin = Math.Max(ALoLargo(e1), ALoLargo(e2));
            if (fin - inicio < 1e-9) { inicio = tramos.Min(t => t.A0); fin = tramos.Max(t => t.A1); }
            r[lado] = (new Frente(Afuera(tramos, signo), signo, inicio, fin), camino.ToHashSet());
        }
        return r;
    }
}
