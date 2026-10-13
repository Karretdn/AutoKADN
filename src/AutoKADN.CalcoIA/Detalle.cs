using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AutoKADN.CalcoIA;

// Modo «detallar»: el anillo ya está dibujado (por el usuario o por RECORTEANILLOS) y la IA solo lee del croquis los
// números que van en él. El plugin deja en lectura.json lo que hay dibujado, con ids (T1… tramos nuevos, E1… existentes,
// A1… accesorios); la IA devuelve detalle.json con a qué id va cada número, en el formato que lee CalcoDetalleJson del
// plugin. Antes de detallar comprueba que la hoja corresponda al dibujo; si no, la rechaza con sus motivos.

public sealed class LecturaDibujo
{
    public string Layout { get; set; } = string.Empty;
    public string Anillo { get; set; } = string.Empty;     // número del indicador del anillo (o del layout)
    public string Diametro { get; set; } = string.Empty;   // del indicador: 1/2, 3/4
    public string Ot { get; set; } = string.Empty;          // la del cajetín
    public List<TramoLeido> Tramos { get; set; } = [];
    public List<AccesorioLeido> Accesorios { get; set; } = [];
    public List<double[][]> Caras { get; set; } = [];      // [[x0, y0], [x1, y1]] en unidades del dibujo
    public List<TextoLeido> Textos { get; set; } = [];

    // Lo mismo que muestra ver_dibujo, en texto.
    public string Describir()
    {
        var t = new StringBuilder();
        t.AppendLine($"Layout {Layout}: anillo {Vacio(Anillo)}, diámetro {Vacio(Diametro)}, O.T. {Vacio(Ot)}.");
        t.AppendLine("Tramos (en unidades del dibujo; el dibujo NO va a escala: compare la forma y el orden, no los largos):");
        foreach (TramoLeido tramo in Tramos)
            t.AppendLine($"- {tramo.Id} {(tramo.Nuevo ? "nuevo" : "existente")}{(tramo.Diametro.Length > 0 ? " Ø" + tramo.Diametro + "\"" : string.Empty)}: de {P(tramo.Inicio)} a {P(tramo.Fin)}, {Sentido(tramo)}, mide {tramo.Largo.ToString("0.#", CultureInfo.InvariantCulture)} u.");
        t.AppendLine("Accesorios dibujados:");
        foreach (AccesorioLeido a in Accesorios) t.AppendLine($"- {a.Id} {a.Nombre}{(a.Diametro.Length > 0 ? " " + a.Diametro + "\"" : string.Empty)} en {P([a.X, a.Y])}{(a.Tramo.Length > 0 ? " sobre " + a.Tramo : string.Empty)}.");
        if (Accesorios.Count == 0) t.AppendLine("- (ninguno)");
        if (Textos.Count > 0) t.AppendLine("Textos: " + string.Join(", ", Textos.Select(x => "«" + x.Texto + "»")) + ".");
        return t.ToString();
    }

    public static string Sentido(TramoLeido tramo)
    {
        double dx = tramo.Fin[0] - tramo.Inicio[0], dy = tramo.Fin[1] - tramo.Inicio[1];
        if (Math.Abs(dx) >= Math.Abs(dy) * 3) return dx > 0 ? "va hacia la derecha" : "va hacia la izquierda";
        if (Math.Abs(dy) >= Math.Abs(dx) * 3) return dy > 0 ? "sube" : "baja";
        return "inclinado";
    }

    private static string P(double[] p) => "(" + p[0].ToString("0.#", CultureInfo.InvariantCulture) + ", " + p[1].ToString("0.#", CultureInfo.InvariantCulture) + ")";
    private static string Vacio(string s) => s.Length > 0 ? s : "?";
}

public sealed class TramoLeido
{
    public string Id { get; set; } = string.Empty;
    public bool Nuevo { get; set; }
    public double[] Inicio { get; set; } = [0, 0];
    public double[] Fin { get; set; } = [0, 0];
    public string Diametro { get; set; } = string.Empty;   // del cruce o sus tramitos (según las reducciones); vacío = el del anillo
    [JsonIgnore] public double Largo => Math.Sqrt(Math.Pow(Fin[0] - Inicio[0], 2) + Math.Pow(Fin[1] - Inicio[1], 2));
}

public sealed class AccesorioLeido
{
    public string Id { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Diametro { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public string Tramo { get; set; } = string.Empty;
}

public sealed class TextoLeido
{
    public string Texto { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Giro { get; set; }
}

// Lo que entrega la IA (y lo que va a detalle.json, sin los campos de revisión).
public sealed class DetalleIa
{
    public bool Corresponde { get; set; } = true;
    public List<string> Motivos { get; set; } = [];
    public string Diametro { get; set; } = string.Empty;
    public List<LargoIa> Largos { get; set; } = [];
    public List<UbicacionIa> Ubicaciones { get; set; } = [];
    public List<CotaAccesorioIa> Accesorios { get; set; } = [];
    public string? Total { get; set; }                                  // total de tubería del cuadro del croquis
    public Dictionary<string, int>? CuadroAccesorios { get; set; }
    public List<string> Dudas { get; set; } = [];
}

public sealed class LargoIa
{
    public string Tramo { get; set; } = string.Empty;
    public string Texto { get; set; } = string.Empty;
    public string Diametro { get; set; } = string.Empty;   // solo si el tramo es de otro diámetro que el anillo
}

public sealed class UbicacionIa
{
    public string Tramo { get; set; } = string.Empty;
    public double En { get; set; } = 0.5;
    public string Texto { get; set; } = string.Empty;
    public string Hacia { get; set; } = string.Empty;   // lado de la cara (en el dibujo); vacío = la más cercana
}

public sealed class CotaAccesorioIa
{
    public string Accesorio { get; set; } = string.Empty;
    public string Hacia { get; set; } = string.Empty;
    public string Texto { get; set; } = string.Empty;
}

// Revisión de lo que entrega la IA contra lo dibujado: errores (ids que no existen, valores inválidos) que debe corregir
// antes de entregar, y avisos (totales o accesorios que no cuadran) que quedan para el usuario.
public static class RevisionDetalle
{
    private static readonly string[] Direcciones = ["arriba", "abajo", "izquierda", "derecha"];

    public static (List<string> Errores, List<string> Avisos, List<string> Resumen) Revisar(DetalleIa d, LecturaDibujo l)
    {
        var errores = new List<string>();
        var avisos = new List<string>();
        var resumen = new List<string>();
        if (!d.Corresponde)
        {
            if (d.Motivos.Count == 0) errores.Add("si la hoja no corresponde al dibujo, diga por qué en «motivos»");
            return (errores, avisos, resumen);
        }

        var ids = l.Tramos.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var accesorios = l.Accesorios.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (LargoIa largo in d.Largos)
        {
            if (!ids.TryGetValue(largo.Tramo, out TramoLeido? t)) errores.Add($"cota de largo «{largo.Texto}»: no existe el tramo {largo.Tramo}");
            else if (!t.Nuevo) errores.Add($"cota de largo «{largo.Texto}»: {t.Id} es existente; las cotas de largo van solo en tramos nuevos");
            if (Numero(largo.Texto) == null) errores.Add($"cota de largo «{largo.Texto}» en {largo.Tramo}: no es un número");
        }
        foreach (var repetido in d.Largos.GroupBy(x => x.Tramo, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errores.Add($"el tramo {repetido.Key} tiene {repetido.Count()} cotas de largo; va una por tramo");
        foreach (TramoLeido t in l.Tramos.Where(t => t.Nuevo && !d.Largos.Any(x => string.Equals(x.Tramo, t.Id, StringComparison.OrdinalIgnoreCase))))
            avisos.Add($"el tramo nuevo {t.Id} quedó sin cota de largo");
        foreach (UbicacionIa u in d.Ubicaciones)
        {
            if (!ids.ContainsKey(u.Tramo)) errores.Add($"ubicación «{u.Texto}»: no existe el tramo {u.Tramo}");
            if (u.En < 0 || u.En > 1) errores.Add($"ubicación «{u.Texto}» en {u.Tramo}: «en» va de 0 (inicio del tramo) a 1 (fin)");
            if (Numero(u.Texto) == null) errores.Add($"ubicación «{u.Texto}» en {u.Tramo}: no es un número");
            if (u.Hacia.Length > 0 && !Direcciones.Contains(u.Hacia.Trim().ToLowerInvariant())) errores.Add($"ubicación «{u.Texto}» en {u.Tramo}: «hacia» debe ser arriba, abajo, izquierda o derecha");
        }
        foreach (CotaAccesorioIa c in d.Accesorios)
        {
            if (!accesorios.ContainsKey(c.Accesorio)) errores.Add($"cota «{c.Texto}»: no existe el accesorio {c.Accesorio}");
            bool cero = Numero(c.Texto) is double v && Math.Abs(v) < 1e-9;
            if (!cero && !Direcciones.Contains(c.Hacia.Trim().ToLowerInvariant())) errores.Add($"cota «{c.Texto}» de {c.Accesorio}: «hacia» debe ser arriba, abajo, izquierda o derecha (en el dibujo)");
            if (Numero(c.Texto) == null) errores.Add($"cota «{c.Texto}» de {c.Accesorio}: no es un número");
        }

        // Por diámetro: el del detalle, si no el del tramo, si no el del anillo (como lo pone AutoKADN).
        string anillo = d.Diametro.Length > 0 ? Normalizar(d.Diametro) : Normalizar(l.Diametro);
        string DiametroDe(LargoIa x) => x.Diametro.Length > 0 ? Normalizar(x.Diametro) : ids.TryGetValue(x.Tramo, out TramoLeido? t) && t.Diametro.Length > 0 ? Normalizar(t.Diametro) : anillo;
        foreach (var grupo in d.Largos.GroupBy(DiametroDe))
            resumen.Add($"Ø{grupo.Key}\": {string.Join(" + ", grupo.Select(x => x.Texto))} = {grupo.Sum(x => Numero(x.Texto) ?? 0).ToString("0.0#", CultureInfo.InvariantCulture)}");
        double suma = d.Largos.Where(x => DiametroDe(x) == anillo).Sum(x => Numero(x.Texto) ?? 0);
        resumen.Add($"{d.Ubicaciones.Count} cota(s) de ubicación de la tubería (se ponen las que quepan)");
        if (d.Accesorios.Count > 0) resumen.Add($"{d.Accesorios.Count} cota(s) de accesorio: {string.Join(", ", d.Accesorios.Select(c => c.Accesorio + " " + c.Texto))}");
        if (Numero(d.Total ?? string.Empty) is double total && Math.Abs(total - suma) > 0.05)
            avisos.Add($"la suma de las cotas de largo de Ø{anillo}\" ({suma.ToString("0.0#", CultureInfo.InvariantCulture)}) no da el total del cuadro ({d.Total})");
        if (d.CuadroAccesorios != null)
            foreach (var par in d.CuadroAccesorios)
            {
                int dibujados = l.Accesorios.Count(a => string.Equals(a.Nombre, par.Key, StringComparison.OrdinalIgnoreCase));
                if (dibujados != par.Value) avisos.Add($"el cuadro dice {par.Value} {par.Key} y en el dibujo hay {dibujados}");
            }
        if (d.Diametro.Length > 0 && l.Diametro.Length > 0 && Normalizar(d.Diametro) != Normalizar(l.Diametro))
            avisos.Add($"el croquis dice Ø{d.Diametro}\" y el indicador del dibujo Ø{l.Diametro}\"");
        return (errores, avisos, resumen);
    }

    public static double? Numero(string texto)
    {
        string t = texto.Trim().ToUpperInvariant().Replace("LC", string.Empty).Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    private static string Normalizar(string diametro) => diametro.Replace("\"", string.Empty).Replace("Ø", string.Empty).Replace("∅", string.Empty).Trim();
}
