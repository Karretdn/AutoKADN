using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using AutoKADN.Core;
using AutoKADN.Tools.Excel;
using static AutoKADN.Core.Naming;

namespace AutoKADN.Tools.Resumen;

// Modelo del resumen del proyecto: lo que el plano ya procesa (cotas UC, actividades anotadas, material, espiral y
// pruebas), ordenado por anillo y consolidado. Nada de esto calcula cosas nuevas: son los mismos números del Excel de
// legalización (GENERAREXCEL) y de los informes (RESUMENOBRA), puestos en una sola vista para compararlos.

internal enum ResumenSeveridad { Error = 0, Aviso = 1, Info = 2, Ok = 3 }

internal sealed class ResumenHallazgo
{
    public ResumenSeveridad Severidad;
    public string Anillo = string.Empty;          // "" = todo el proyecto
    public string Verificacion = string.Empty;
    public string Detalle = string.Empty;
    public string Correccion = string.Empty;
}

// Una unidad constructiva (diámetro + terreno) de un anillo, o del proyecto completo en el consolidado.
internal sealed class ResumenUc
{
    public string Anillo = string.Empty;
    public string Diametro = string.Empty;        // normalizado: "1/2", "3/4", "2"...
    public string Terreno = string.Empty;         // normalizado: "ZONA VERDE"...

    public double Cotas;                          // cotas UC del plano, sin descontar nada
    public double Arroyo;                         // CRUCE DE ARROYO: negativo en la UC de donde sale, positivo en la UC especial
    public double CotasNetas;                     // lo que queda en la UC después del cruce de arroyo (nunca negativo)
    public double Camisa;
    public double CruceTopo;
    public double Pantalla;
    public double VigaConcreto;
    public double Empedrado;
    public double Espiral;                        // tubería de espiral de válvula
    public double Pruebas;                        // tubería de material de prueba
    public double Accesorios;                     // piezas (sin tubería) de la UC: proyecto + pruebas
    public double Tuberia;                        // total de tubería de la UC (el que va al Excel de legalización)
    public double Canalizacion;                   // cantidad de la actividad de canalización de esta UC
    public double Excavacion;                     // ML excavados: cotas netas - camisa - cruce con topo
    public double Volumen;                        // m3 de zanja de esa excavación
    public Dictionary<string, double> Actividades = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public bool EsCruceArroyo => string.Equals(Terreno, CruceArroyoSurface, StringComparison.OrdinalIgnoreCase);
    public UcKey Clave => new UcKey(Diametro, Terreno);
}

internal sealed class ResumenMaterial
{
    public string Anillo = string.Empty;
    public string Descripcion = string.Empty;     // normalizada: "TUBERIA", "VALVULA"...
    public string Diametro = string.Empty;
    public string Unidad = "UND";
    public string Codigo = string.Empty;
    public double Proyecto;
    public double Pruebas;
    public double Total => Proyecto + Pruebas;
}

internal sealed class ResumenAnillo
{
    public string Clave = string.Empty;           // "ANILLO 3", "TRONCAL" o "PROYECTO" (consolidado)
    public int Numero;                            // 0 para el troncal
    public bool EsTroncal;
    public bool TieneDetalle;
    public bool TieneUc;
    public string LayoutDetalle = string.Empty;
    public string LayoutUc = string.Empty;
    public readonly List<ResumenUc> Ucs = new List<ResumenUc>();
    public readonly List<ResumenMaterial> Materiales = new List<ResumenMaterial>();

    public double Suma(Func<ResumenUc, double> campo) => Ucs.Sum(campo);

    public double TuberiaDiametro(string diametro) =>
        Ucs.Where(u => string.Equals(u.Diametro, diametro, StringComparison.OrdinalIgnoreCase)).Sum(u => u.Tuberia);

    public double Material(string descripcion, string diametro = null, bool pruebas = false)
    {
        double total = 0.0;
        foreach (ResumenMaterial m in Materiales)
        {
            if (!string.Equals(m.Descripcion, descripcion, StringComparison.OrdinalIgnoreCase)) continue;
            if (diametro != null && !string.Equals(m.Diametro, diametro, StringComparison.OrdinalIgnoreCase)) continue;
            total += pruebas ? m.Pruebas : m.Proyecto;
        }
        return total;
    }
}

// Algo que los escaneos del Excel de legalización dejan fuera sin avisar: una cota sin terreno, un bloque que no está
// en el catálogo... Se muestra para que el dibujante lo corrija.
internal sealed class ResumenOmitido
{
    public string Anillo = string.Empty;
    public string Layout = string.Empty;
    public string Tipo = string.Empty;            // "Cota" | "Bloque"
    public string Elemento = string.Empty;        // texto de la cota o nombre del bloque
    public string Diametro = string.Empty;
    public string Motivo = string.Empty;
}

internal sealed class ResumenProyecto
{
    public string Dibujo = string.Empty;          // nombre del DWG
    public string Carpeta = string.Empty;
    public DateTime Generado = DateTime.Now;
    public string Version = string.Empty;
    public readonly Dictionary<string, string> Cajetin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Contratista = string.Empty;
    public string ContratistaProblema = string.Empty;
    public readonly List<ResumenAnillo> Anillos = new List<ResumenAnillo>();
    public ResumenAnillo Consolidado = new ResumenAnillo { Clave = "PROYECTO" };
    public readonly List<ResumenOmitido> Omitidos = new List<ResumenOmitido>();
    public readonly List<string> LayoutsNoReconocidos = new List<string>();
    // Etiqueta del cajetín -> valor -> layouts donde está (para ver si es el mismo en todos).
    public readonly Dictionary<string, Dictionary<string, List<string>>> CajetinPorLayout =
        new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
    // CRUCE DE ARROYO anotado en cada layout UC (anillo -> UC -> ML), para cruzarlo con el del DETALLE.
    public readonly Dictionary<string, Dictionary<UcKey, double>> ArroyoEnUc =
        new Dictionary<string, Dictionary<UcKey, double>>(StringComparer.OrdinalIgnoreCase);
    // Cruce de arroyo anotado en el DETALLE (el que usa el cálculo), por anillo y UC.
    public readonly Dictionary<string, Dictionary<UcKey, double>> ArroyoEnDetalle =
        new Dictionary<string, Dictionary<UcKey, double>>(StringComparer.OrdinalIgnoreCase);
    // Diámetros que dicen los títulos de cada plano de anillo (ANILLO 1 - 3/4", %%C3/4"), para cruzarlos con las cotas.
    public readonly Dictionary<string, HashSet<string>> DiametrosTitulo =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    public ProjectSummary Informes;               // lo que RESUMENOBRA entrega a los formatos (FT-T-127, FT-O-108...)
    public string InformesProblema = string.Empty;
    public readonly List<ResumenHallazgo> Hallazgos = new List<ResumenHallazgo>();
    public readonly List<string> NotasProceso = new List<string>();

    public ResumenAnillo Anillo(string clave) =>
        Anillos.FirstOrDefault(a => string.Equals(a.Clave, clave, StringComparison.OrdinalIgnoreCase));
}

// Nombres para mostrar y códigos de actividad, a partir de las mismas tablas del Excel de legalización.
internal static class ResumenCatalogo
{
    private static readonly Dictionary<string, string> NombreMaterial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["TUBERIA"] = "Tubería", ["UNION"] = "Unión", ["TEE"] = "Tee", ["TAPON"] = "Tapón", ["SILLETA"] = "Silleta",
        ["VALVULA"] = "Válvula", ["REDUCCION"] = "Reducción", ["CODO"] = "Codo",
    };

    private static readonly Dictionary<string, string> NombrePlural = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["TUBERIA"] = "Tuberías", ["UNION"] = "Uniones", ["TEE"] = "Tees", ["TAPON"] = "Tapones", ["SILLETA"] = "Silletas",
        ["VALVULA"] = "Válvulas", ["REDUCCION"] = "Reducciones", ["CODO"] = "Codos",
    };

    private static readonly Dictionary<string, string> NombreTerreno = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ZONA VERDE"] = "Zona verde", ["ANDEN CONCRETO"] = "Andén concreto", ["CALZADA CONCRETO"] = "Calzada concreto",
        ["ANDEN TABLETA"] = "Andén tableta", ["ADOQUIN"] = "Adoquín", ["ASFALTO"] = "Asfalto", ["CUNETA"] = "Cuneta",
        ["DESTAPADO"] = "Destapado", [CruceArroyoSurface] = "Cruce de arroyo",
    };

    public static string Material(string descripcion) =>
        NombreMaterial.TryGetValue(descripcion ?? string.Empty, out string nombre) ? nombre : (descripcion ?? string.Empty);

    public static string MaterialPlural(string descripcion) =>
        NombrePlural.TryGetValue(descripcion ?? string.Empty, out string nombre) ? nombre : Material(descripcion);

    public static string Terreno(string terreno) =>
        NombreTerreno.TryGetValue(terreno ?? string.Empty, out string nombre) ? nombre : (terreno ?? string.Empty);

    // 3/4 -> 3/4"; 4X2 -> 4" x 2"
    public static string Diametro(string diametro)
    {
        if (string.IsNullOrWhiteSpace(diametro)) return string.Empty;
        string[] partes = diametro.Split('x', 'X');
        return string.Join(" x ", partes.Select(p => p.Trim() + "\""));
    }

    // m3 de zanja por ML: 0.28 en 1/2" y 3/4"; 0.30 en troncal (el mismo factor del formato FT-O-108).
    public static double FactorZanja(string diametro) => (diametro == "1/2" || diametro == "3/4") ? 0.28 : 0.3;

    public static readonly string[] OrdenDiametros = { "1/2", "3/4", "2", "3", "4", "6" };

    public static int OrdenDiametro(string diametro)
    {
        string primero = (diametro ?? string.Empty).Split('x', 'X')[0].Trim();
        int indice = Array.IndexOf(OrdenDiametros, primero);
        return indice < 0 ? int.MaxValue : indice;
    }

    // Código de actividad -> descripción, armado con las mismas tablas de GenerarExcelTool.
    private static Dictionary<string, string> _actividades;

    public static string Actividad(string codigo)
    {
        if (_actividades == null) _actividades = ConstruirActividades();
        return _actividades.TryGetValue(codigo ?? string.Empty, out string nombre) ? nombre : "Actividad " + codigo;
    }

    public static string UnidadActividad(string codigo) => codigo == GenerarExcelTool.EmpedradoCode ? "M2" : "ML";

    private static Dictionary<string, string> ConstruirActividades()
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Poner(string codigo, string nombre) { if (!mapa.ContainsKey(codigo)) mapa[codigo] = nombre; }

        foreach (KeyValuePair<string, string> item in GenerarExcelTool.CanalizacionCodeBySurface)
            Poner(item.Value, "Canalización anillo · " + Terreno(item.Key));
        foreach (KeyValuePair<string, string> item in GenerarExcelTool.CanalizacionTroncalCodeBySurface)
            Poner(item.Value, "Canalización troncal P80 · " + Terreno(item.Key));
        foreach (KeyValuePair<string, string> item in GenerarExcelTool.TendidoTermofusionCodeByDiameter)
            Poner(item.Value, "Tendido y termofusión troncal " + item.Key + "\"");
        foreach (KeyValuePair<string, string> item in GenerarExcelTool.CruceTopoCodeByDiameter)
            Poner(item.Value, item.Key == "2" ? "Cruce con topo (anillo 1/2\", 3/4\" y troncal 2\")" : "Cruce con topo troncal " + item.Key + "\"");
        Poner(GenerarExcelTool.CruceTopoCode, "Cruce con topo");
        Poner(GenerarExcelTool.CruceArroyoRingCode, "Cruce de arroyo a cielo abierto anillo (1/2\" y 3/4\")");
        Poner(GenerarExcelTool.CruceArroyoTroncalCode, "Cruce de arroyo a cielo abierto troncal (2\", 3\", 4\" y 6\")");
        Poner(GenerarExcelTool.PlanosAsBuiltCode, "Planos as-built");
        Poner(GenerarExcelTool.PantallaCode, "Pantalla");
        Poner(GenerarExcelTool.VigaConcretoCode, "Viga en concreto");
        Poner(GenerarExcelTool.EmpedradoCode, "Empedrado");
        return mapa;
    }

    // Orden estable para las tablas: primero los códigos conocidos por su código, y al final los demás.
    public static int OrdenActividad(string codigo) =>
        long.TryParse(codigo, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numero) ? (int)Math.Min(numero % 1000000000L, int.MaxValue) : int.MaxValue;
}
