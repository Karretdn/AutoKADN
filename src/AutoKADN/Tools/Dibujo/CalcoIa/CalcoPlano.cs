using System.Globalization;
using AutoKADN.Core;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Lo que el dibujante del calco con IA pone en un layout ANILLO n DETALLE, ya en unidades del dibujo (dentro del marco).
// Solo dice QUÉ es cada cosa y dónde va; capa, color, tipo de línea, tamaños, bloques y XData los decide CalcoDibujante
// con las convenciones de las herramientas (COTAK, NOMENK, LISTABLOQUES). Los giros van en grados en el archivo y en
// radianes aquí. Formato del archivo: ver CalcoPlanoJson.
public sealed class CalcoPlano
{
    // Las cotas de longitud van hacia afuera de este punto (salvo que la cota diga su lado).
    public CalcoPunto CentroManzana { get; set; }
    public List<CalcoLinea> Lineas { get; } = new List<CalcoLinea>();
    public List<CalcoTexto> Textos { get; } = new List<CalcoTexto>();
    public List<CalcoCota> Cotas { get; } = new List<CalcoCota>();
    public List<CalcoAccesorio> Accesorios { get; } = new List<CalcoAccesorio>();
    public List<CalcoIndicador> Indicadores { get; } = new List<CalcoIndicador>();
}

public readonly struct CalcoPunto
{
    public CalcoPunto(double x, double y) { X = x; Y = y; }
    public double X { get; }
    public double Y { get; }
}

public enum CalcoTipoLinea { Manzana, Predio, Construccion, Vecino, TuberiaNueva, TuberiaExistente }

public enum CalcoTipoTexto { Predial, Vial, Libre }

public sealed class CalcoLinea
{
    public CalcoTipoLinea Tipo { get; set; }
    public List<CalcoPunto> Puntos { get; } = new List<CalcoPunto>();
    public bool Cerrada { get; set; }
}

public sealed class CalcoTexto
{
    public string Texto { get; set; } = string.Empty;
    public CalcoPunto Centro { get; set; }
    public double Giro { get; set; }
    public CalcoTipoTexto Tipo { get; set; }
}

// Cota de longitud de un tramo de tubería nueva (COTAK > Longitud).
public sealed class CalcoCota
{
    public CalcoPunto A { get; set; }
    public CalcoPunto B { get; set; }
    public string Texto { get; set; } = string.Empty;
    public string Diametro { get; set; } = "3/4";
    // Un punto del lado donde va la cota; sin él va hacia afuera de la manzana.
    public CalcoPunto? Lado { get; set; }
}

// Accesorio (bloque dinámico de material). Direccion, según el bloque:
//   TAPON: hacia donde sigue la tubería desde el tapón.  TEE: hacia donde sale el ramal.
//   REDUCCION: hacia el lado de diámetro menor (adonde apunta el triángulo).
//   UNION, VALVULA, SILLETA: a lo largo de la tubería.
public sealed class CalcoAccesorio
{
    public string Nombre { get; set; } = string.Empty;
    public CalcoPunto Posicion { get; set; }
    public double Direccion { get; set; }
    public string Diametro { get; set; } = "3/4";
    public string Superficie { get; set; } = "ZONA VERDE";
}

// Indicador de anillo: círculo con el número y el diámetro (y debajo las viviendas, si se dan).
public sealed class CalcoIndicador
{
    public string Numero { get; set; } = string.Empty;
    public string Diametro { get; set; } = "3/4";
    public string Viviendas { get; set; } = string.Empty;
    public CalcoPunto Centro { get; set; }
}

/// <summary>
/// Lee un plano de calco desde JSON. Formato (todo opcional salvo las coordenadas; giros en grados):
/// <code>
/// { "centroManzana": [x, y],
///   "lineas":      [ { "tipo": "manzana|predio|construccion|vecino|tuberia_nueva|tuberia_existente", "puntos": [[x, y], ...], "cerrada": false } ],
///   "textos":      [ { "texto": "34C-11", "x": 0, "y": 0, "giro": 0, "tipo": "predial|vial|libre" } ],
///   "cotas":       [ { "a": [x, y], "b": [x, y], "texto": "31.0", "diametro": "3/4", "lado": [x, y] } ],
///   "accesorios":  [ { "nombre": "TAPON", "x": 0, "y": 0, "direccion": 180, "diametro": "3/4", "superficie": "ZONA VERDE" } ],
///   "indicadores": [ { "numero": "5", "diametro": "3/4", "viviendas": "6 VIVIENDAS", "x": 0, "y": 0 } ] }
/// </code>
/// </summary>
public static class CalcoPlanoJson
{
    public static CalcoPlano Leer(string json)
    {
        if (!(MiniJson.Parse(json) is Dictionary<string, object?> raiz)) throw new FormatException("el archivo no es un objeto JSON");
        var plano = new CalcoPlano();
        if (raiz.TryGetValue("centroManzana", out object? centro) && centro != null) plano.CentroManzana = Punto(centro, "centroManzana");

        foreach (Dictionary<string, object?> item in Lista(raiz, "lineas"))
        {
            var linea = new CalcoLinea { Tipo = TipoLinea(Texto(item, "tipo", "manzana")), Cerrada = Booleano(item, "cerrada") };
            foreach (object? punto in Valores(item, "puntos")) linea.Puntos.Add(Punto(punto, "lineas.puntos"));
            plano.Lineas.Add(linea);
        }

        foreach (Dictionary<string, object?> item in Lista(raiz, "textos"))
            plano.Textos.Add(new CalcoTexto
            {
                Texto = Texto(item, "texto", string.Empty),
                Centro = new CalcoPunto(Numero(item, "x"), Numero(item, "y")),
                Giro = Grados(item, "giro"),
                Tipo = TipoTexto(Texto(item, "tipo", "libre")),
            });

        foreach (Dictionary<string, object?> item in Lista(raiz, "cotas"))
        {
            var cota = new CalcoCota
            {
                A = Punto(Valor(item, "a"), "cotas.a"),
                B = Punto(Valor(item, "b"), "cotas.b"),
                Texto = Texto(item, "texto", string.Empty),
                Diametro = Texto(item, "diametro", "3/4"),
            };
            if (item.TryGetValue("lado", out object? lado) && lado != null) cota.Lado = Punto(lado, "cotas.lado");
            plano.Cotas.Add(cota);
        }

        foreach (Dictionary<string, object?> item in Lista(raiz, "accesorios"))
            plano.Accesorios.Add(new CalcoAccesorio
            {
                Nombre = Texto(item, "nombre", string.Empty).Trim().ToUpperInvariant(),
                Posicion = new CalcoPunto(Numero(item, "x"), Numero(item, "y")),
                Direccion = Grados(item, "direccion"),
                Diametro = Texto(item, "diametro", "3/4"),
                Superficie = Texto(item, "superficie", "ZONA VERDE"),
            });

        foreach (Dictionary<string, object?> item in Lista(raiz, "indicadores"))
            plano.Indicadores.Add(new CalcoIndicador
            {
                Numero = Texto(item, "numero", string.Empty),
                Diametro = Texto(item, "diametro", "3/4"),
                Viviendas = Texto(item, "viviendas", string.Empty),
                Centro = new CalcoPunto(Numero(item, "x"), Numero(item, "y")),
            });

        return plano;
    }

    private static CalcoTipoLinea TipoLinea(string valor)
    {
        switch (valor.Trim().ToLowerInvariant())
        {
            case "manzana": return CalcoTipoLinea.Manzana;
            case "predio": return CalcoTipoLinea.Predio;
            case "construccion": return CalcoTipoLinea.Construccion;
            case "vecino": return CalcoTipoLinea.Vecino;
            case "tuberia_nueva": return CalcoTipoLinea.TuberiaNueva;
            case "tuberia_existente": return CalcoTipoLinea.TuberiaExistente;
            default: throw new FormatException($"tipo de línea desconocido «{valor}»");
        }
    }

    private static CalcoTipoTexto TipoTexto(string valor)
    {
        switch (valor.Trim().ToLowerInvariant())
        {
            case "predial": return CalcoTipoTexto.Predial;
            case "vial": return CalcoTipoTexto.Vial;
            case "libre": return CalcoTipoTexto.Libre;
            default: throw new FormatException($"tipo de texto desconocido «{valor}»");
        }
    }

    private static IEnumerable<Dictionary<string, object?>> Lista(Dictionary<string, object?> objeto, string campo)
    {
        foreach (object? valor in Valores(objeto, campo))
        {
            if (!(valor is Dictionary<string, object?> item)) throw new FormatException($"«{campo}» debe ser una lista de objetos");
            yield return item;
        }
    }

    private static List<object?> Valores(Dictionary<string, object?> objeto, string campo)
    {
        if (!objeto.TryGetValue(campo, out object? valor) || valor == null) return new List<object?>();
        if (valor is List<object?> lista) return lista;
        throw new FormatException($"«{campo}» debe ser una lista");
    }

    private static object? Valor(Dictionary<string, object?> objeto, string campo)
    {
        if (!objeto.TryGetValue(campo, out object? valor) || valor == null) throw new FormatException($"falta «{campo}»");
        return valor;
    }

    private static CalcoPunto Punto(object? valor, string campo)
    {
        if (valor is List<object?> lista && lista.Count >= 2 && lista[0] is double x && lista[1] is double y) return new CalcoPunto(x, y);
        throw new FormatException($"«{campo}» debe ser un punto [x, y]");
    }

    private static double Numero(Dictionary<string, object?> objeto, string campo)
    {
        object? valor = Valor(objeto, campo);
        if (valor is double numero) return numero;
        if (valor is string texto && double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out numero)) return numero;
        throw new FormatException($"«{campo}» debe ser un número");
    }

    private static double Grados(Dictionary<string, object?> objeto, string campo) =>
        objeto.TryGetValue(campo, out object? valor) && valor != null ? Numero(objeto, campo) * Math.PI / 180.0 : 0.0;

    private static string Texto(Dictionary<string, object?> objeto, string campo, string porDefecto)
    {
        if (!objeto.TryGetValue(campo, out object? valor) || valor == null) return porDefecto;
        if (valor is string texto) return texto;
        if (valor is double numero) return numero.ToString(CultureInfo.InvariantCulture);
        throw new FormatException($"«{campo}» debe ser un texto");
    }

    private static bool Booleano(Dictionary<string, object?> objeto, string campo) =>
        objeto.TryGetValue(campo, out object? valor) && valor is bool b && b;
}
