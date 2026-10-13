using System.Globalization;
using AutoKADN.Core;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Lo que dice el croquis para DETALLAR un anillo que ya está dibujado (recorte del plano general o dibujo a mano): solo
// números y a qué tramo o accesorio van. Los tramos y accesorios se nombran con los ids que da CalcoLectorLayout
// (T1, T2… y A1, A2…); la geometría la pone el dibujo, no el croquis. Formato del archivo: ver CalcoDetalleJson.
public sealed class CalcoDetalle
{
    public string Diametro { get; set; } = "3/4";
    // Cota de largo de cada tramo de tubería nueva.
    public List<CalcoLargo> Largos { get; } = new List<CalcoLargo>();
    // Cotas magenta de la tubería a la cara de enfrente, en el orden de prioridad del croquis (se ponen las que quepan).
    public List<CalcoUbicacion> Ubicaciones { get; } = new List<CalcoUbicacion>();
    // Cota magenta de un accesorio a la cara que tiene adelante o atrás en el sentido del tramo («0.0» = LC0.0).
    public List<CalcoCotaAccesorio> Accesorios { get; } = new List<CalcoCotaAccesorio>();
}

public sealed class CalcoLargo
{
    public string Tramo { get; set; } = "";
    public string Texto { get; set; } = "";
    // Si el tramo es de otro diámetro que el anillo (el cruce de 3/4", sus tramitos); vacío = el del tramo o el del anillo.
    public string Diametro { get; set; } = "";
}

public sealed class CalcoUbicacion
{
    public string Tramo { get; set; } = "";
    // Posición a lo largo del tramo, de 0 (su inicio) a 1 (su fin).
    public double En { get; set; } = 0.5;
    public string Texto { get; set; } = "";
    // Hacia qué lado (en el dibujo) está la cara que acota: arriba, abajo, izquierda, derecha; vacío = la más cercana.
    public string Hacia { get; set; } = "";
}

public sealed class CalcoCotaAccesorio
{
    public string Accesorio { get; set; } = "";
    // Dirección del dibujo hacia la cara: arriba, abajo, izquierda o derecha.
    public string Hacia { get; set; } = "";
    public string Texto { get; set; } = "";
}

// {"diametro": "3/4",
//  "largos": [{"tramo": "T1", "texto": "43.5"}, {"tramo": "T4", "texto": "2.0", "diametro": "3/4"}],
//  "ubicaciones": [{"tramo": "T1", "en": 0.35, "texto": "0.5"}],
//  "accesorios": [{"accesorio": "A2", "hacia": "arriba", "texto": "7.8"}]}
public static class CalcoDetalleJson
{
    public static CalcoDetalle Leer(string json)
    {
        if (MiniJson.Parse(json) is not Dictionary<string, object?> raiz) throw new FormatException("el detalle debe ser un objeto JSON");
        var detalle = new CalcoDetalle();
        if (Texto(raiz, "diametro") is string diametro && diametro.Length > 0) detalle.Diametro = diametro;
        foreach (Dictionary<string, object?> o in Lista(raiz, "largos"))
            detalle.Largos.Add(new CalcoLargo { Tramo = Texto(o, "tramo"), Texto = Texto(o, "texto"), Diametro = Texto(o, "diametro") });
        foreach (Dictionary<string, object?> o in Lista(raiz, "ubicaciones"))
            detalle.Ubicaciones.Add(new CalcoUbicacion { Tramo = Texto(o, "tramo"), En = o.TryGetValue("en", out object? en) && en is double d ? d : 0.5, Texto = Texto(o, "texto"), Hacia = Texto(o, "hacia").ToLowerInvariant() });
        foreach (Dictionary<string, object?> o in Lista(raiz, "accesorios"))
            detalle.Accesorios.Add(new CalcoCotaAccesorio { Accesorio = Texto(o, "accesorio"), Hacia = Texto(o, "hacia").ToLowerInvariant(), Texto = Texto(o, "texto") });
        return detalle;
    }

    private static IEnumerable<Dictionary<string, object?>> Lista(Dictionary<string, object?> objeto, string campo)
    {
        if (!objeto.TryGetValue(campo, out object? valor) || valor is null) yield break;
        if (valor is not List<object?> lista) throw new FormatException($"«{campo}» debe ser una lista");
        foreach (object? elemento in lista)
            if (elemento is Dictionary<string, object?> o) yield return o;
    }

    private static string Texto(Dictionary<string, object?> objeto, string campo)
    {
        if (!objeto.TryGetValue(campo, out object? valor) || valor is null) return "";
        return valor is double d ? d.ToString("0.0##", CultureInfo.InvariantCulture) : (valor.ToString() ?? "").Trim();
    }
}
