using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using AutoKADN.Core;
using AutoKADN.Tools.Excel;
using static AutoKADN.Core.Naming;
using E = AutoKADN.Tools.Resumen.ResumenEstilos;

namespace AutoKADN.Tools.Resumen;

// Una sección del libro es una hoja que se escribe a partir del resumen leído. Para sumar una nueva (por ejemplo
// rendimientos por día según los días en obra) basta una clase que implemente esta interfaz y registrarla en
// ResumenLibro.Secciones: el libro la agrega, el índice de la portada la enlaza y el LEEME la lista.
internal interface IResumenSeccion
{
    string Hoja { get; }
    string Descripcion { get; }
    void Escribir(XlsxHoja hoja, ResumenProyecto proyecto);
}

internal sealed class ResumenSeccion : IResumenSeccion
{
    private readonly Action<XlsxHoja, ResumenProyecto> _escribir;

    public ResumenSeccion(string hoja, string descripcion, Action<XlsxHoja, ResumenProyecto> escribir)
    {
        Hoja = hoja;
        Descripcion = descripcion;
        _escribir = escribir;
    }

    public string Hoja { get; private set; }
    public string Descripcion { get; private set; }
    public void Escribir(XlsxHoja hoja, ResumenProyecto proyecto) => _escribir(hoja, proyecto);
}

internal static class ResumenLibro
{
    private const string HojaResumen = "RESUMEN";
    private const string HojaComparar = "COMPARAR";
    private const string HojaAnillos = "ANILLOS";
    private const string HojaUcAnillo = "UC POR ANILLO";
    private const string HojaUcConsolidado = "UC CONSOLIDADO";
    private const string HojaActividades = "ACTIVIDADES";
    private const string HojaActividadesUc = "ACTIVIDADES POR UC";
    private const string HojaMateriales = "MATERIALES";
    private const string HojaVerificaciones = "VERIFICACIONES";
    private const string HojaLeeme = "LEEME";

    private const double Tolerancia = 0.01;

    // Hojas del libro después de RESUMEN y antes de LEEME, en este orden.
    public static readonly List<IResumenSeccion> Secciones = new List<IResumenSeccion>
    {
        new ResumenSeccion(HojaComparar, "Plano contra interventoría: digita lo que reporta interventoría y ves la diferencia y el estado de cada concepto.", Comparar),
        new ResumenSeccion(HojaAnillos, "Una fila por anillo: cotas, restas, tubería por diámetro, excavación y material, con el total y su cruce contra GENERAREXCEL.", Anillos),
        new ResumenSeccion(HojaUcAnillo, "Cada unidad constructiva (diámetro y terreno) de cada anillo, con todas sus restas y adiciones.", UcPorAnillo),
        new ResumenSeccion(HojaUcConsolidado, "Cada unidad constructiva de todo el proyecto: suma de los anillos contra el consolidado del Excel de legalización.", UcConsolidado),
        new ResumenSeccion(HojaActividades, "Códigos de actividad del Excel de legalización por anillo y en el consolidado.", Actividades),
        new ResumenSeccion(HojaActividadesUc, "Las actividades que lleva cada unidad constructiva en su formato de legalización.", ActividadesPorUc),
        new ResumenSeccion(HojaMateriales, "Material del proyecto y de pruebas por descripción y diámetro, y su distribución por anillo.", Materiales),
        new ResumenSeccion(HojaVerificaciones, "Revisión automática del plano: errores, avisos y cómo corregirlos.", Verificaciones),
    };

    public static XlsxLibro Construir(ResumenProyecto p)
    {
        string titulo = "Resumen del proyecto";
        string proyecto = Valor(p, "PROYECTO", "OBRA");
        var libro = new XlsxLibro
        {
            Titulo = proyecto.Length > 0 ? titulo + " · " + proyecto : titulo,
            Descripcion = "Resumen por anillo y consolidado generado por AutoKADN a partir del plano " + p.Dibujo,
        };

        XlsxHoja portada = libro.AgregarHoja(HojaResumen);
        foreach (IResumenSeccion seccion in Secciones)
        {
            XlsxHoja hoja = libro.AgregarHoja(seccion.Hoja);
            seccion.Escribir(hoja, p);
        }
        XlsxHoja leeme = libro.AgregarHoja(HojaLeeme);
        EscribirLeeme(leeme, p);
        EscribirPortada(portada, p);
        return libro;
    }

    // ---------- utilidades ----------

    private static string Valor(ResumenProyecto p, params string[] etiquetas)
    {
        foreach (string etiqueta in etiquetas)
        {
            string valor;
            if (p.Cajetin.TryGetValue(etiqueta, out valor) && !string.IsNullOrWhiteSpace(valor)) return valor;
        }
        return string.Empty;
    }

    private static double Redondear(double v) => Math.Round(v, 4);

    private static void Titulo(XlsxHoja h, string titulo, string subtitulo, int ultimaColumna)
    {
        h.Texto(2, 2, titulo, E.Titulo);
        h.Alto(2, 30);
        h.Combinar(2, 2, 2, ultimaColumna);
        if (!string.IsNullOrEmpty(subtitulo))
        {
            h.Texto(3, 2, subtitulo, E.Subtitulo);
            h.Combinar(3, 2, 3, ultimaColumna);
        }
        h.Ancho(1, 2.5);
    }

    private static void Seccion(XlsxHoja h, int fila, string texto, int c1, int c2)
    {
        h.Texto(fila, c1, texto, E.Seccion);
        h.Rellenar(fila, c1 + 1, fila, c2, E.Seccion);
        h.Alto(fila, 22);
    }

    // Altura de fila para texto con ajuste: aproxima las líneas que ocupa en una columna de n caracteres.
    private static double AltoTexto(string texto, double anchoCaracteres, double minimo = 18.0)
    {
        if (string.IsNullOrEmpty(texto)) return minimo;
        int lineas = 0;
        foreach (string parte in texto.Split('\n'))
            lineas += Math.Max(1, (int)Math.Ceiling(parte.Length / Math.Max(8.0, anchoCaracteres * 1.05)));
        return Math.Max(minimo, lineas * 13.5 + 5.0);
    }

    private static string Fecha(DateTime fecha) => fecha.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static List<string> DiametrosUsados(ResumenProyecto p)
    {
        var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1/2", "3/4" };
        foreach (ResumenUc uc in p.Consolidado.Ucs) if (uc.Tuberia > 0.0) usados.Add(uc.Diametro);
        foreach (ResumenAnillo a in p.Anillos) foreach (ResumenUc uc in a.Ucs) if (uc.Tuberia > 0.0) usados.Add(uc.Diametro);
        return usados.OrderBy(ResumenCatalogo.OrdenDiametro).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int Errores(ResumenProyecto p, string anillo = null) =>
        p.Hallazgos.Count(h => h.Severidad == ResumenSeveridad.Error && (anillo == null || string.Equals(h.Anillo, anillo, StringComparison.OrdinalIgnoreCase)));

    private static int Avisos(ResumenProyecto p, string anillo = null) =>
        p.Hallazgos.Count(h => h.Severidad == ResumenSeveridad.Aviso && (anillo == null || string.Equals(h.Anillo, anillo, StringComparison.OrdinalIgnoreCase)));

    // Excavación del consolidado como la calcula el formato FT-O-108: ML de cada UC sumados entre los anillos y el
    // volumen con la regla del formato (ML x factor, truncado a 2 decimales).
    private sealed class FilaUcProyecto
    {
        public string Diametro = string.Empty;
        public string Terreno = string.Empty;
        public double Excavacion;
        public double Volumen;
    }

    private static List<FilaUcProyecto> ExcavacionProyecto(ResumenProyecto p)
    {
        var mapa = new Dictionary<UcKey, FilaUcProyecto>();
        foreach (ResumenAnillo a in p.Anillos)
            foreach (ResumenUc uc in a.Ucs)
            {
                FilaUcProyecto fila;
                if (!mapa.TryGetValue(uc.Clave, out fila)) { fila = new FilaUcProyecto { Diametro = uc.Diametro, Terreno = uc.Terreno }; mapa[uc.Clave] = fila; }
                fila.Excavacion += uc.Excavacion;
            }
        foreach (FilaUcProyecto fila in mapa.Values) fila.Volumen = ControlExcavacionBuilder.Volume(fila.Diametro, fila.Excavacion);
        return mapa.Values
            .OrderBy(f => GetSurfaceOrder(f.Terreno)).ThenBy(f => ResumenCatalogo.OrdenDiametro(f.Diametro)).ToList();
    }

    // ---------- PORTADA ----------

    private static void EscribirPortada(XlsxHoja h, ResumenProyecto p)
    {
        const int ultima = 13;
        h.ColorPestana = E.Primario;
        h.Zoom = 90;
        for (int c = 2; c <= ultima; c++) h.Ancho(c, 12.5);
        string proyecto = Valor(p, "PROYECTO", "OBRA");
        Titulo(h, "RESUMEN DEL PROYECTO", proyecto.Length > 0 ? proyecto : "Resumen por anillo y consolidado", ultima);
        string linea = "Dibujo: " + p.Dibujo + "   ·   Generado: " + Fecha(p.Generado) + (p.Version.Length > 0 ? "   ·   AutoKADN " + p.Version : string.Empty);
        h.Texto(4, 2, linea, E.Nota);
        h.Combinar(4, 2, 4, ultima);
        h.Alto(4, 16);

        // Datos del proyecto (del cajetín del plano).
        int fila = 6;
        Seccion(h, fila, "Datos del proyecto", 2, ultima);
        fila++;
        var datos = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Municipio", Valor(p, "MUNICIPIO")),
            new KeyValuePair<string, string>("Proyecto", Valor(p, "PROYECTO", "OBRA")),
            new KeyValuePair<string, string>("Sector", Valor(p, "SECTOR")),
            new KeyValuePair<string, string>("Orden de trabajo", Valor(p, "O.T.#")),
            new KeyValuePair<string, string>("Interventor", Valor(p, "INTERVENTOR")),
            new KeyValuePair<string, string>("Supervisor", Valor(p, "PEGADOR")),
            new KeyValuePair<string, string>("Tubería", Valor(p, "TUBERIA")),
            new KeyValuePair<string, string>("Contratista", p.Contratista),
        };
        for (int i = 0; i < datos.Count; i += 2)
        {
            for (int lado = 0; lado < 2 && i + lado < datos.Count; lado++)
            {
                int columna = lado == 0 ? 2 : 8;
                h.Texto(fila, columna, datos[i + lado].Key, E.Banner);
                h.Combinar(fila, columna, fila, columna + 1);
                h.Rellenar(fila, columna + 1, fila, columna + 1, E.Banner);
                string valor = datos[i + lado].Value;
                h.Texto(fila, columna + 2, valor.Length > 0 ? valor : "—", E.Texto);
                h.Combinar(fila, columna + 2, fila, columna + 4);
                h.Rellenar(fila, columna + 3, fila, columna + 4, E.Texto);
            }
            h.Alto(fila, 20);
            fila++;
        }

        // Tarjetas con las cifras principales.
        fila++;
        Seccion(h, fila, "Cifras principales", 2, ultima);
        fila += 2;
        ResumenAnillo total = p.Consolidado;
        List<FilaUcProyecto> excavacion = ExcavacionProyecto(p);
        double tuberia = total.Ucs.Sum(u => u.Tuberia);
        double excavacionMl = excavacion.Sum(f => f.Excavacion);
        double volumen = excavacion.Sum(f => f.Volumen);
        double valvulas = total.Material("VALVULA") + total.Material("VALVULA", null, true);
        int anillosConDetalle = p.Anillos.Count(a => !a.EsTroncal && a.TieneDetalle);
        int errores = Errores(p), avisos = Avisos(p);

        Kpi(h, fila, 2, "ANILLOS", anillosConDetalle, "#,##0", p.Anillos.Any(a => a.EsTroncal) ? "más el troncal" : "layouts DETALLE");
        Kpi(h, fila, 4, "TUBERÍA TOTAL (ML)", tuberia, "#,##0.00", "proyecto + pruebas");
        Kpi(h, fila, 6, "EXCAVACIÓN (ML)", excavacionMl, "#,##0.00", "sin camisa ni topo");
        Kpi(h, fila, 8, "ZANJA (M³)", volumen, "#,##0.00", "según FT-O-108");
        Kpi(h, fila, 10, "VÁLVULAS", valvulas, "#,##0", "proyecto + pruebas");
        Kpi(h, fila, 12, "ALERTAS", errores + avisos, "#,##0", errores + " error(es) · " + avisos + " aviso(s)");
        fila += 4;

        // Tubería por terreno y diámetro (consolidado).
        Seccion(h, fila, "Tubería por terreno y diámetro (ML)", 2, ultima);
        fila++;
        List<string> diametros = DiametrosUsados(p);
        var terrenos = total.Ucs.Select(u => u.Terreno).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetSurfaceOrder).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        h.Texto(fila, 2, "Terreno", E.EncabezadoIzquierda);
        h.Combinar(fila, 2, fila, 3);
        h.Vacia(fila, 3, E.EncabezadoIzquierda);
        for (int d = 0; d < diametros.Count; d++) h.Texto(fila, 4 + d, ResumenCatalogo.Diametro(diametros[d]), E.Encabezado);
        int columnaTotal = 4 + diametros.Count;
        h.Texto(fila, columnaTotal, "Total", E.Encabezado);
        h.Alto(fila, 20);
        int encabezado = fila;
        fila++;
        int primera = fila;
        foreach (string terreno in terrenos)
        {
            h.Texto(fila, 2, ResumenCatalogo.Terreno(terreno), E.Texto);
            h.Combinar(fila, 2, fila, 3);
            h.Vacia(fila, 3, E.Texto);
            double sumaFila = 0.0;
            for (int d = 0; d < diametros.Count; d++)
            {
                double valor = total.Ucs.Where(u => string.Equals(u.Terreno, terreno, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(u.Diametro, diametros[d], StringComparison.OrdinalIgnoreCase)).Sum(u => u.Tuberia);
                h.Numero(fila, 4 + d, Redondear(valor), E.Ml);
                sumaFila += valor;
            }
            h.Formula(fila, columnaTotal, "SUM(" + XlsxHoja.Celda(fila, 4) + ":" + XlsxHoja.Celda(fila, columnaTotal - 1) + ")", Redondear(sumaFila), E.TotalMl);
            fila++;
        }
        if (terrenos.Count > 0)
        {
            h.Texto(fila, 2, "Total", E.TotalTexto);
            h.Combinar(fila, 2, fila, 3);
            h.Vacia(fila, 3, E.TotalTexto);
            for (int c = 4; c <= columnaTotal; c++)
            {
                double suma = 0.0;
                for (int r = primera; r < fila; r++)
                {
                    XlsxCelda celda;
                    if (h.Filas[r].TryGetValue(c, out celda)) suma += celda.Numero;
                }
                h.Formula(fila, c, "SUM(" + XlsxHoja.Celda(primera, c) + ":" + XlsxHoja.Celda(fila - 1, c) + ")", Redondear(suma), E.TotalMl);
            }
            fila++;
        }
        else
        {
            h.Texto(fila, 2, "No se encontraron cotas UC en el plano.", E.Nota);
            h.Combinar(fila, 2, fila, ultima);
            fila++;
        }
        fila++;

        // Estado de las verificaciones.
        Seccion(h, fila, "Verificaciones", 2, ultima);
        fila++;
        List<ResumenHallazgo> graves = p.Hallazgos
            .Where(x => x.Severidad == ResumenSeveridad.Error || x.Severidad == ResumenSeveridad.Aviso)
            .OrderBy(x => (int)x.Severidad).ToList();
        if (graves.Count == 0)
        {
            h.Texto(fila, 2, "Sin errores ni avisos: el plano pasó todas las verificaciones.", E.SevOk);
            h.Combinar(fila, 2, fila, ultima);
            h.Rellenar(fila, 3, fila, ultima, E.SevOk);
            fila++;
        }
        else
        {
            foreach (ResumenHallazgo x in graves.Take(8))
            {
                XlsxEstilo chip = x.Severidad == ResumenSeveridad.Error ? E.SevError : E.SevAviso;
                h.Texto(fila, 2, x.Severidad == ResumenSeveridad.Error ? "ERROR" : "AVISO", chip);
                h.Texto(fila, 3, x.Anillo.Length > 0 ? x.Anillo : "Proyecto", E.TextoCentro);
                h.Combinar(fila, 3, fila, 4);
                h.Vacia(fila, 4, E.TextoCentro);
                h.Texto(fila, 5, x.Detalle, E.TextoAjustado);
                h.Combinar(fila, 5, fila, ultima);
                h.Rellenar(fila, 6, fila, ultima, E.TextoAjustado);
                h.Alto(fila, AltoTexto(x.Detalle, 12.5 * 9));
                fila++;
            }
            if (graves.Count > 8)
            {
                h.Texto(fila, 2, "… y " + (graves.Count - 8) + " más en la hoja VERIFICACIONES.", E.Nota);
                h.Combinar(fila, 2, fila, ultima);
                fila++;
            }
            h.Texto(fila, 2, "Ver todas las verificaciones →", E.Enlace);
            h.Enlace(fila, 2, HojaVerificaciones, "A1", "Abre la hoja VERIFICACIONES");
            fila++;
        }
        fila++;

        // Índice del libro.
        Seccion(h, fila, "Contenido del libro", 2, ultima);
        fila++;
        var indice = new List<KeyValuePair<string, string>>();
        foreach (IResumenSeccion seccion in Secciones) indice.Add(new KeyValuePair<string, string>(seccion.Hoja, seccion.Descripcion));
        indice.Add(new KeyValuePair<string, string>(HojaLeeme, "Cómo leer el libro, de dónde sale cada número y cómo agregar secciones nuevas."));
        foreach (KeyValuePair<string, string> item in indice)
        {
            h.Texto(fila, 2, item.Key, E.EnlaceTabla);
            h.Combinar(fila, 2, fila, 4);
            h.Rellenar(fila, 3, fila, 4, E.EnlaceTabla);
            h.Enlace(fila, 2, item.Key, "A1", "Ir a " + item.Key);
            h.Texto(fila, 5, item.Value, E.TextoAjustado);
            h.Combinar(fila, 5, fila, ultima);
            h.Rellenar(fila, 6, fila, ultima, E.TextoAjustado);
            h.Alto(fila, AltoTexto(item.Value, 12.5 * 9));
            fila++;
        }
        h.Horizontal = false;
        h.FilasTitulo = 0;
    }

    private static void Kpi(XlsxHoja h, int fila, int columna, string etiqueta, double valor, string formato, string nota)
    {
        h.Texto(fila, columna, etiqueta, E.KpiEtiqueta);
        h.Combinar(fila, columna, fila, columna + 1);
        h.Rellenar(fila, columna + 1, fila, columna + 1, E.KpiEtiqueta);
        h.Numero(fila + 1, columna, Redondear(valor), E.KpiValor.Con(e => e.Formato = formato));
        h.Combinar(fila + 1, columna, fila + 1, columna + 1);
        h.Rellenar(fila + 1, columna + 1, fila + 1, columna + 1, E.KpiValor.Con(e => e.Formato = formato));
        h.Alto(fila + 1, 34);
        h.Texto(fila + 2, columna, nota, E.KpiNota);
        h.Combinar(fila + 2, columna, fila + 2, columna + 1);
        h.Rellenar(fila + 2, columna + 1, fila + 2, columna + 1, E.KpiNota);
    }

    // ---------- COMPARAR ----------

    private sealed class ConceptoComparar
    {
        public string Seccion;
        public string Concepto;
        public string Unidad;
        public double Valor;
        public bool Entero;
    }

    private static void Comparar(XlsxHoja h, ResumenProyecto p)
    {
        const int ultima = 8;
        h.ColorPestana = E.Acento;
        h.Ancho(2, 50); h.Ancho(3, 9); h.Ancho(4, 15); h.Ancho(5, 17); h.Ancho(6, 15); h.Ancho(7, 15); h.Ancho(8, 42);
        Titulo(h, "COMPARACIÓN CON INTERVENTORÍA", "Digita en las celdas amarillas lo que reporta interventoría: la diferencia y el estado de cada concepto se calculan solos.", ultima);

        h.Texto(5, 2, "Tolerancia de la comparación (±)", E.Banner);
        h.Numero(5, 4, Tolerancia, E.EntradaMl);
        h.Texto(5, 3, "ML / UND", E.TextoCentro);
        string tolerancia = "$D$5";

        List<ConceptoComparar> conceptos = ConceptosComparar(p);
        int encabezado = 9;
        int primera = encabezado + 1;
        int ultimaFila = primera + conceptos.Count + conceptos.Select(c => c.Seccion).Distinct().Count() - 1;
        string rangoEstado = "$G$" + primera + ":$G$" + ultimaFila;

        h.Texto(6, 2, "Coinciden", E.Banner);
        h.Texto(7, 2, "Con diferencia", E.Banner);
        h.Texto(5, 6, "Pendientes", E.Banner);
        h.Combinar(5, 6, 5, 7);
        h.Rellenar(5, 7, 5, 7, E.Banner);
        // Los contadores se calculan en Excel, sobre el estado de cada fila.
        int coinciden = 0, diferencia = 0, pendientes = conceptos.Count;
        h.Formula(6, 4, "COUNTIF(" + rangoEstado + ",\"Coincide\")", coinciden, E.Entero);
        h.Formula(7, 4, "COUNTIF(" + rangoEstado + ",\"Diferencia\")", diferencia, E.Entero);
        h.Formula(5, 8, "COUNTIF(" + rangoEstado + ",\"Pendiente\")", pendientes, E.Entero);
        h.Texto(6, 3, "conceptos", E.TextoCentro);
        h.Texto(7, 3, "conceptos", E.TextoCentro);
        h.Condicional(7, 4, 7, 4, "$D$7>0", E.DxfError);
        h.Condicional(6, 4, 6, 4, "$D$6>0", E.DxfOk);

        h.Texto(encabezado, 2, "Concepto", E.EncabezadoIzquierda);
        h.Texto(encabezado, 3, "Unidad", E.Encabezado);
        h.Texto(encabezado, 4, "Plano", E.Encabezado);
        h.Texto(encabezado, 5, "Interventoría", E.Encabezado);
        h.Texto(encabezado, 6, "Diferencia", E.Encabezado);
        h.Texto(encabezado, 7, "Estado", E.Encabezado);
        h.Texto(encabezado, 8, "Observación", E.EncabezadoIzquierda);
        h.Alto(encabezado, 22);

        int fila = primera;
        string seccionActual = null;
        foreach (ConceptoComparar c in conceptos)
        {
            if (!string.Equals(seccionActual, c.Seccion, StringComparison.Ordinal))
            {
                seccionActual = c.Seccion;
                h.Texto(fila, 2, c.Seccion, E.Banner);
                h.Rellenar(fila, 3, fila, ultima, E.Banner);
                fila++;
            }
            h.Texto(fila, 2, c.Concepto, E.Texto);
            h.Texto(fila, 3, c.Unidad, E.TextoCentro);
            h.Numero(fila, 4, Redondear(c.Valor), c.Entero ? E.Entero : E.MlCero);
            h.Vacia(fila, 5, E.EntradaMl);
            string d = XlsxHoja.Celda(fila, 4), i = XlsxHoja.Celda(fila, 5), dif = XlsxHoja.Celda(fila, 6);
            h.FormulaTexto(fila, 6, "IF(" + i + "=\"\",\"\"," + i + "-" + d + ")", string.Empty, E.Dif);
            h.FormulaTexto(fila, 7, "IF(" + i + "=\"\",\"Pendiente\",IF(ABS(" + i + "-" + d + ")<=" + tolerancia + ",\"Coincide\",\"Diferencia\"))", "Pendiente", E.TextoCentro);
            h.Vacia(fila, 8, E.EntradaTexto);
            fila++;
        }
        int ultimaDato = fila - 1;
        if (ultimaDato >= primera)
        {
            h.Condicional(primera, 7, ultimaDato, 7, "$G" + primera + "=\"Diferencia\"", E.DxfError);
            h.Condicional(primera, 7, ultimaDato, 7, "$G" + primera + "=\"Coincide\"", E.DxfOk);
            h.Condicional(primera, 7, ultimaDato, 7, "$G" + primera + "=\"Pendiente\"", E.DxfPendiente);
        }
        h.FijarFilas = encabezado;
        h.FilasTitulo = encabezado;
    }

    private static List<ConceptoComparar> ConceptosComparar(ResumenProyecto p)
    {
        var lista = new List<ConceptoComparar>();
        ResumenAnillo total = p.Consolidado;
        List<string> diametros = DiametrosUsados(p);

        void Agregar(string seccion, string concepto, string unidad, double valor, bool entero = false) =>
            lista.Add(new ConceptoComparar { Seccion = seccion, Concepto = concepto, Unidad = unidad, Valor = valor, Entero = entero });

        const string general = "Generalidades";
        Agregar(general, "Anillos (planos DETALLE)", "UND", p.Anillos.Count(a => !a.EsTroncal && a.TieneDetalle), true);
        if (p.Anillos.Any(a => a.EsTroncal && a.TieneDetalle)) Agregar(general, "Plano troncal", "UND", 1, true);

        const string tuberia = "Tubería por diámetro (proyecto + pruebas)";
        foreach (string d in diametros)
        {
            double ml = total.TuberiaDiametro(d);
            if (ml > 0.0 || d == "1/2" || d == "3/4") Agregar(tuberia, "Tubería " + ResumenCatalogo.Diametro(d), "ML", ml);
        }
        Agregar(tuberia, "Tubería total", "ML", total.Ucs.Sum(u => u.Tuberia));

        const string terreno = "Tubería por terreno y diámetro";
        foreach (ResumenUc uc in total.Ucs.Where(u => u.Tuberia > 0.0))
            Agregar(terreno, "Tubería " + ResumenCatalogo.Diametro(uc.Diametro) + " · " + ResumenCatalogo.Terreno(uc.Terreno), "ML", uc.Tuberia);

        const string excavacion = "Excavación (FT-O-108)";
        List<FilaUcProyecto> proyecto = ExcavacionProyecto(p);
        foreach (string d in diametros)
        {
            double ml = proyecto.Where(f => f.Diametro == d).Sum(f => f.Excavacion);
            double m3 = proyecto.Where(f => f.Diametro == d).Sum(f => f.Volumen);
            if (ml <= 0.0) continue;
            Agregar(excavacion, "Excavación " + ResumenCatalogo.Diametro(d), "ML", ml);
            Agregar(excavacion, "Zanja " + ResumenCatalogo.Diametro(d), "M3", m3);
        }

        const string restas = "Restas y actividades especiales";
        Agregar(restas, "Camisa instalada por la constructora", "ML", total.Ucs.Sum(u => u.Camisa));
        Agregar(restas, "Cruce con topo", "ML", total.Ucs.Sum(u => u.CruceTopo));
        Agregar(restas, "Cruce de arroyo", "ML", total.Ucs.Where(u => !u.EsCruceArroyo).Sum(u => -u.Arroyo));
        Agregar(restas, "Tubería de espiral de válvula", "ML", total.Ucs.Sum(u => u.Espiral));
        Agregar(restas, "Tubería de material de prueba", "ML", total.Ucs.Sum(u => u.Pruebas));
        Agregar(restas, "Pantalla", "ML", total.Ucs.Sum(u => u.Pantalla));
        Agregar(restas, "Viga en concreto", "ML", total.Ucs.Sum(u => u.VigaConcreto));
        Agregar(restas, "Empedrado", "ML", total.Ucs.Sum(u => u.Empedrado));

        const string accesorios = "Accesorios (UND)";
        foreach (ResumenMaterial m in total.Materiales.Where(x => x.Descripcion != NormalizeToken("TUBERIA")))
        {
            string nombre = ResumenCatalogo.Material(m.Descripcion) + " " + ResumenCatalogo.Diametro(m.Diametro);
            if (m.Proyecto > 0.0) Agregar(accesorios, nombre + " · proyecto", "UND", m.Proyecto, true);
            if (m.Pruebas > 0.0) Agregar(accesorios, nombre + " · pruebas", "UND", m.Pruebas, true);
        }

        const string porAnillo = "Tubería por anillo";
        foreach (ResumenAnillo a in p.Anillos)
            foreach (string d in diametros)
            {
                double ml = a.TuberiaDiametro(d);
                if (ml > 0.0) Agregar(porAnillo, a.Clave + " · Tubería " + ResumenCatalogo.Diametro(d), "ML", ml);
            }
        return lista;
    }

    // ---------- ANILLOS ----------

    private sealed class ColumnaAnillo
    {
        public string Titulo = string.Empty;
        public string Grupo = string.Empty;
        public double Ancho = 12;
        public Func<ResumenAnillo, double> Valor;
        public bool Entero;
        public bool Consolidable = true;     // tiene valor en el consolidado de GENERAREXCEL
    }

    private static void Anillos(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Acento;
        var columnas = new List<ColumnaAnillo>
        {
            new ColumnaAnillo { Grupo = "Cotas y restas (ML)", Titulo = "Cotas UC", Valor = a => a.Suma(u => u.Cotas) },
            new ColumnaAnillo { Grupo = "Cotas y restas (ML)", Titulo = "Cruce de arroyo", Valor = a => a.Ucs.Where(u => !u.EsCruceArroyo).Sum(u => -u.Arroyo) },
            new ColumnaAnillo { Grupo = "Cotas y restas (ML)", Titulo = "Camisa", Valor = a => a.Suma(u => u.Camisa) },
            new ColumnaAnillo { Grupo = "Cotas y restas (ML)", Titulo = "Cruce con topo", Valor = a => a.Suma(u => u.CruceTopo) },
            new ColumnaAnillo { Grupo = "Cotas y restas (ML)", Titulo = "Espiral", Valor = a => a.Suma(u => u.Espiral) },
            new ColumnaAnillo { Grupo = "Cotas y restas (ML)", Titulo = "Tubería de pruebas", Valor = a => a.Suma(u => u.Pruebas) },
        };
        List<string> diametros = DiametrosUsados(p);
        foreach (string d in diametros)
        {
            string diametro = d;
            columnas.Add(new ColumnaAnillo { Grupo = "Tubería por diámetro (ML)", Titulo = ResumenCatalogo.Diametro(diametro), Valor = a => a.TuberiaDiametro(diametro) });
        }
        columnas.Add(new ColumnaAnillo { Grupo = "Tubería por diámetro (ML)", Titulo = "Total", Valor = a => a.Suma(u => u.Tuberia) });
        columnas.Add(new ColumnaAnillo { Grupo = "Excavación", Titulo = "ML", Valor = a => a.Suma(u => u.Excavacion), Consolidable = false });
        columnas.Add(new ColumnaAnillo { Grupo = "Excavación", Titulo = "M³", Valor = a => a.Suma(u => u.Volumen), Consolidable = false });
        string[][] piezas =
        {
            new[] { "VALVULA", "Válvulas" }, new[] { "UNION", "Uniones" }, new[] { "TEE", "Tees" },
            new[] { "TAPON", "Tapones" }, new[] { "REDUCCION", "Reducciones" }, new[] { "SILLETA", "Silletas" },
        };
        foreach (string[] pieza in piezas)
        {
            string descripcion = pieza[0];
            columnas.Add(new ColumnaAnillo { Grupo = "Material del proyecto (UND)", Titulo = pieza[1], Entero = true, Valor = a => a.Material(descripcion) });
        }
        columnas.Add(new ColumnaAnillo
        {
            Grupo = "Pruebas (UND)", Titulo = "Piezas de prueba", Entero = true,
            Valor = a => a.Materiales.Where(m => m.Descripcion != NormalizeToken("TUBERIA")).Sum(m => m.Pruebas),
        });

        int primeraColumna = 4;
        int ultima = primeraColumna + columnas.Count + 1;  // + errores + avisos
        Titulo(h, "ANILLOS", "Un renglón por anillo con lo que el plano arroja. El total se compara contra el consolidado del Excel de legalización (GENERAREXCEL).", ultima);
        h.Ancho(2, 14); h.Ancho(3, 18);
        for (int i = 0; i < columnas.Count; i++) h.Ancho(primeraColumna + i, columnas[i].Ancho);
        h.Ancho(ultima - 1, 9); h.Ancho(ultima, 9);

        int grupoFila = 5, encabezado = 6, primera = 7;
        // Grupos (celdas combinadas por familia de columnas).
        h.Texto(grupoFila, 2, string.Empty, E.Grupo);
        h.Combinar(grupoFila, 2, grupoFila, 3);
        h.Vacia(grupoFila, 3, E.Grupo);
        int inicio = 0;
        while (inicio < columnas.Count)
        {
            int fin = inicio;
            while (fin + 1 < columnas.Count && columnas[fin + 1].Grupo == columnas[inicio].Grupo) fin++;
            h.Texto(grupoFila, primeraColumna + inicio, columnas[inicio].Grupo, E.Grupo);
            if (fin > inicio) { h.Combinar(grupoFila, primeraColumna + inicio, grupoFila, primeraColumna + fin); h.Rellenar(grupoFila, primeraColumna + inicio + 1, grupoFila, primeraColumna + fin, E.Grupo); }
            inicio = fin + 1;
        }
        h.Texto(grupoFila, ultima - 1, "Alertas", E.Grupo);
        h.Combinar(grupoFila, ultima - 1, grupoFila, ultima);
        h.Vacia(grupoFila, ultima, E.Grupo);

        h.Texto(encabezado, 2, "Anillo", E.EncabezadoIzquierda);
        h.Texto(encabezado, 3, "Planos", E.Encabezado);
        for (int i = 0; i < columnas.Count; i++) h.Texto(encabezado, primeraColumna + i, columnas[i].Titulo, E.Encabezado);
        h.Texto(encabezado, ultima - 1, "Errores", E.Encabezado);
        h.Texto(encabezado, ultima, "Avisos", E.Encabezado);
        h.Alto(encabezado, 32);

        var cacheTotales = new double[columnas.Count];
        int fila = primera;
        foreach (ResumenAnillo a in p.Anillos)
        {
            h.Texto(fila, 2, a.Clave, E.TextoNegrita);
            string planos = (a.TieneDetalle ? "DETALLE ✓" : "DETALLE ✗") + "  " + (a.TieneUc ? "UC ✓" : "UC ✗");
            h.Texto(fila, 3, planos, E.TextoCentro);
            for (int i = 0; i < columnas.Count; i++)
            {
                double valor = Redondear(columnas[i].Valor(a));
                h.Numero(fila, primeraColumna + i, valor, columnas[i].Entero ? E.Entero : E.Ml);
                cacheTotales[i] += valor;
            }
            h.Numero(fila, ultima - 1, Errores(p, a.Clave), E.Entero);
            h.Numero(fila, ultima, Avisos(p, a.Clave), E.Entero);
            fila++;
        }
        int ultimaDato = fila - 1;
        if (p.Anillos.Count == 0)
        {
            h.Texto(fila, 2, "El dibujo no tiene layouts ANILLO n DETALLE / UC ni TRONCAL.", E.Nota);
            h.Combinar(fila, 2, fila, ultima);
            return;
        }

        int filaTotal = fila;
        h.Texto(filaTotal, 2, "TOTAL ANILLOS", E.TotalTexto);
        h.Vacia(filaTotal, 3, E.TotalTexto);
        for (int i = 0; i < columnas.Count; i++)
        {
            int c = primeraColumna + i;
            h.Formula(filaTotal, c, "SUM(" + XlsxHoja.Celda(primera, c) + ":" + XlsxHoja.Celda(ultimaDato, c) + ")", Redondear(cacheTotales[i]), columnas[i].Entero ? E.TotalEntero : E.TotalMl);
        }
        h.Formula(filaTotal, ultima - 1, "SUM(" + XlsxHoja.Celda(primera, ultima - 1) + ":" + XlsxHoja.Celda(ultimaDato, ultima - 1) + ")", p.Anillos.Sum(a => Errores(p, a.Clave)), E.TotalEntero);
        h.Formula(filaTotal, ultima, "SUM(" + XlsxHoja.Celda(primera, ultima) + ":" + XlsxHoja.Celda(ultimaDato, ultima) + ")", p.Anillos.Sum(a => Avisos(p, a.Clave)), E.TotalEntero);

        // Consolidado del Excel de legalización (todo el dibujo a la vez) y la diferencia.
        int filaConsolidado = filaTotal + 1, filaDiferencia = filaTotal + 2;
        h.Texto(filaConsolidado, 2, "Consolidado GENERAREXCEL", E.TextoNegrita);
        h.Vacia(filaConsolidado, 3, E.Texto);
        h.Texto(filaDiferencia, 2, "Diferencia", E.TextoNegrita);
        h.Vacia(filaDiferencia, 3, E.Texto);
        for (int i = 0; i < columnas.Count; i++)
        {
            int c = primeraColumna + i;
            if (!columnas[i].Consolidable)
            {
                h.Texto(filaConsolidado, c, "n/a", E.TextoCentro);
                h.Texto(filaDiferencia, c, "n/a", E.TextoCentro);
                continue;
            }
            double consolidado = Redondear(columnas[i].Valor(p.Consolidado));
            h.Numero(filaConsolidado, c, consolidado, columnas[i].Entero ? E.Entero : E.Ml);
            h.Formula(filaDiferencia, c, XlsxHoja.Celda(filaTotal, c) + "-" + XlsxHoja.Celda(filaConsolidado, c), Redondear(cacheTotales[i] - consolidado), E.Dif);
        }
        h.Condicional(filaDiferencia, primeraColumna, filaDiferencia, primeraColumna + columnas.Count - 1,
            "AND(ISNUMBER(" + XlsxHoja.Celda(filaDiferencia, primeraColumna) + "),ABS(" + XlsxHoja.Celda(filaDiferencia, primeraColumna) + ")>" + Tolerancia.ToString(CultureInfo.InvariantCulture) + ")", E.DxfAviso);
        h.Condicional(primera, ultima - 1, ultimaDato, ultima - 1, XlsxHoja.Celda(primera, ultima - 1) + ">0", E.DxfError);
        h.Condicional(primera, ultima, ultimaDato, ultima, XlsxHoja.Celda(primera, ultima) + ">0", E.DxfAviso);

        h.Texto(filaDiferencia + 2, 2,
            "El consolidado repite el cálculo de GENERAREXCEL con todo el dibujo junto; el total suma los anillos ya calculados uno a uno. " +
            "Una diferencia suele venir de una resta (camisa, topo o arroyo) que supera las cotas de su anillo.", E.Nota);
        h.Combinar(filaDiferencia + 2, 2, filaDiferencia + 2, ultima);
        h.Alto(filaDiferencia + 2, 30);

        h.FijarFilas = encabezado;
        h.FijarColumnas = 2;
        h.FilasTitulo = encabezado;
    }

    // ---------- UC POR ANILLO ----------

    // Columnas de la tabla (las usa también UC CONSOLIDADO en sus SUMIFS).
    private const int CAnillo = 2, CDiametro = 3, CTerreno = 4, CCotas = 5, CArroyo = 6, CNetas = 7, CCamisa = 8, CTopo = 9,
        CCanalizacion = 10, CEspiral = 11, CPruebas = 12, CTuberia = 13, CExcavacion = 14, CVolumen = 15, CPantalla = 16,
        CViga = 17, CEmpedrado = 18, CPiezas = 19;

    private static void UcPorAnillo(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Acento;
        Titulo(h, "UC POR ANILLO", "Cada unidad constructiva (diámetro y terreno) de cada anillo. Usa el filtro de la fila de títulos para ver un anillo o un terreno.", CPiezas);
        string[] titulos =
        {
            "Anillo", "Diámetro", "Terreno", "Cotas UC (ML)", "Cruce de arroyo (±)", "Cotas netas", "Camisa", "Cruce con topo",
            "Canalización", "Espiral", "Tubería de pruebas", "Tubería UC", "Excavación (ML)", "Zanja (M³)", "Pantalla",
            "Viga en concreto", "Empedrado (ML)", "Piezas de material",
        };
        double[] anchos = { 13, 10, 20, 12, 13, 12, 11, 11, 13, 11, 12, 12, 13, 11, 11, 12, 12, 12 };
        for (int i = 0; i < titulos.Length; i++)
        {
            h.Texto(5, 2 + i, titulos[i], i < 3 ? E.EncabezadoIzquierda : E.Encabezado);
            h.Ancho(2 + i, anchos[i]);
        }
        h.Alto(5, 32);

        int fila = 6;
        foreach (ResumenAnillo a in p.Anillos)
            foreach (ResumenUc uc in a.Ucs)
            {
                h.Texto(fila, CAnillo, a.Clave, E.Texto);
                h.Texto(fila, CDiametro, ResumenCatalogo.Diametro(uc.Diametro), E.TextoCentro);
                h.Texto(fila, CTerreno, ResumenCatalogo.Terreno(uc.Terreno), E.Texto);
                h.Numero(fila, CCotas, Redondear(uc.Cotas), E.Ml);
                h.Numero(fila, CArroyo, Redondear(uc.Arroyo), E.MlSigno);
                h.Numero(fila, CNetas, Redondear(uc.CotasNetas), E.Ml);
                h.Numero(fila, CCamisa, Redondear(uc.Camisa), E.Ml);
                h.Numero(fila, CTopo, Redondear(uc.CruceTopo), E.Ml);
                h.Numero(fila, CCanalizacion, Redondear(uc.Canalizacion), E.Ml);
                h.Numero(fila, CEspiral, Redondear(uc.Espiral), E.Ml);
                h.Numero(fila, CPruebas, Redondear(uc.Pruebas), E.Ml);
                h.Numero(fila, CTuberia, Redondear(uc.Tuberia), E.Ml);
                h.Numero(fila, CExcavacion, Redondear(uc.Excavacion), E.Ml);
                h.Numero(fila, CVolumen, Redondear(uc.Volumen), E.Ml);
                h.Numero(fila, CPantalla, Redondear(uc.Pantalla), E.Ml);
                h.Numero(fila, CViga, Redondear(uc.VigaConcreto), E.Ml);
                h.Numero(fila, CEmpedrado, Redondear(uc.Empedrado), E.Ml);
                h.Numero(fila, CPiezas, Redondear(uc.Accesorios), E.Entero);
                fila++;
            }
        int ultimaDato = fila - 1;
        if (ultimaDato < 6)
        {
            h.Texto(6, 2, "El dibujo no tiene unidades constructivas para mostrar.", E.Nota);
            h.Combinar(6, 2, 6, CPiezas);
            return;
        }

        // Los totales se recalculan con el filtro (SUBTOTAL ignora las filas ocultas).
        h.Texto(fila, CAnillo, "TOTAL (filas visibles)", E.TotalTexto);
        h.Rellenar(fila, CDiametro, fila, CTerreno, E.TotalTexto);
        for (int c = CCotas; c <= CPiezas; c++)
        {
            double suma = 0.0;
            for (int r = 6; r <= ultimaDato; r++) { XlsxCelda celda; if (h.Filas[r].TryGetValue(c, out celda)) suma += celda.Numero; }
            h.Formula(fila, c, "SUBTOTAL(109," + XlsxHoja.Celda(6, c) + ":" + XlsxHoja.Celda(ultimaDato, c) + ")", Redondear(suma), c == CPiezas ? E.TotalEntero : E.TotalMl);
        }

        // Una canalización negativa o restas mayores que las cotas netas quedan resaltadas.
        h.Condicional(6, CAnillo, ultimaDato, CPiezas,
            "OR($" + XlsxHoja.NombreColumna(CCanalizacion) + "6<-0.0001,$" + XlsxHoja.NombreColumna(CCamisa) + "6+$" + XlsxHoja.NombreColumna(CTopo) + "6>$" + XlsxHoja.NombreColumna(CNetas) + "6+0.0001)",
            E.DxfError);
        h.AutoFiltro(5, 2, ultimaDato, CPiezas);
        h.FijarFilas = 5;
        h.FijarColumnas = 4;
        h.FilasTitulo = 5;
    }

    // ---------- UC CONSOLIDADO ----------

    private static void UcConsolidado(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Acento;
        Titulo(h, "UC CONSOLIDADO", "Cada unidad constructiva de todo el proyecto: suma de los anillos (fórmulas sobre UC POR ANILLO) contra lo que calcula GENERAREXCEL con el dibujo completo.", 18);
        string[] titulos =
        {
            "Diámetro", "Terreno", "Cotas UC (ML)", "Cruce de arroyo (±)", "Cotas netas", "Camisa", "Cruce con topo", "Canalización",
            "Espiral", "Tubería de pruebas", "Tubería UC", "Excavación (ML)", "Zanja (M³)",
            "Tubería GENERAREXCEL", "Diferencia", "Canalización GENERAREXCEL", "Diferencia",
        };
        double[] anchos = { 10, 20, 12, 13, 12, 11, 11, 13, 11, 12, 12, 13, 11, 15, 12, 16, 12 };
        for (int i = 0; i < titulos.Length; i++)
        {
            h.Texto(5, 2 + i, titulos[i], i < 2 ? E.EncabezadoIzquierda : E.Encabezado);
            h.Ancho(2 + i, anchos[i]);
        }
        h.Alto(5, 32);

        string hoja = XlsxLibro.CitarHoja(HojaUcAnillo);
        Func<int, string> col = c => hoja + "!$" + XlsxHoja.NombreColumna(c) + ":$" + XlsxHoja.NombreColumna(c);

        var claves = new HashSet<UcKey>();
        foreach (ResumenAnillo a in p.Anillos) foreach (ResumenUc uc in a.Ucs) claves.Add(uc.Clave);
        foreach (ResumenUc uc in p.Consolidado.Ucs) claves.Add(uc.Clave);
        List<UcKey> ordenadas = claves.OrderBy(k => GetSurfaceOrder(k.Surface)).ThenBy(k => ResumenCatalogo.OrdenDiametro(k.Diameter))
            .ThenBy(k => k.Surface, StringComparer.OrdinalIgnoreCase).ToList();

        // Columnas de esta hoja y la de UC POR ANILLO de donde sale cada una.
        int[] origen = { CCotas, CArroyo, CNetas, CCamisa, CTopo, CCanalizacion, CEspiral, CPruebas, CTuberia, CExcavacion };
        var suma = new double[19];
        int fila = 6;
        foreach (UcKey clave in ordenadas)
        {
            h.Texto(fila, 2, ResumenCatalogo.Diametro(clave.Diameter), E.TextoCentro);
            h.Texto(fila, 3, ResumenCatalogo.Terreno(clave.Surface), E.Texto);

            var porAnillo = new List<ResumenUc>();
            foreach (ResumenAnillo a in p.Anillos) porAnillo.AddRange(a.Ucs.Where(u => u.Clave.Equals(clave)));
            double[] valores =
            {
                porAnillo.Sum(u => u.Cotas), porAnillo.Sum(u => u.Arroyo), porAnillo.Sum(u => u.CotasNetas), porAnillo.Sum(u => u.Camisa),
                porAnillo.Sum(u => u.CruceTopo), porAnillo.Sum(u => u.Canalizacion), porAnillo.Sum(u => u.Espiral), porAnillo.Sum(u => u.Pruebas),
                porAnillo.Sum(u => u.Tuberia), porAnillo.Sum(u => u.Excavacion),
            };
            for (int i = 0; i < origen.Length; i++)
            {
                string formula = "SUMIFS(" + col(origen[i]) + "," + col(CDiametro) + ",$B" + fila + "," + col(CTerreno) + ",$C" + fila + ")";
                h.Formula(fila, 4 + i, formula, Redondear(valores[i]), origen[i] == CArroyo ? E.MlSigno : E.Ml);
                suma[4 + i] += valores[i];
            }

            // Zanja con la regla del formato FT-O-108: ML de la UC x factor, truncado a 2 decimales.
            double volumen = ControlExcavacionBuilder.Volume(clave.Diameter, valores[9]);
            string celdaExcavacion = XlsxHoja.Celda(fila, 4 + 9);
            h.Formula(fila, 14, "TRUNC(" + celdaExcavacion + "*" + ResumenCatalogo.FactorZanja(clave.Diameter).ToString(CultureInfo.InvariantCulture) + "+0.0000000001,2)", volumen, E.Ml);
            suma[14] += volumen;

            ResumenUc consolidado = p.Consolidado.Ucs.FirstOrDefault(u => u.Clave.Equals(clave));
            double tuberiaConsolidada = consolidado == null ? 0.0 : consolidado.Tuberia;
            double canalizacionConsolidada = consolidado == null ? 0.0 : consolidado.Canalizacion;
            h.Numero(fila, 15, Redondear(tuberiaConsolidada), E.Ml);
            h.Formula(fila, 16, XlsxHoja.Celda(fila, 12) + "-" + XlsxHoja.Celda(fila, 15), Redondear(valores[8] - tuberiaConsolidada), E.Dif);
            h.Numero(fila, 17, Redondear(canalizacionConsolidada), E.Ml);
            h.Formula(fila, 18, XlsxHoja.Celda(fila, 9) + "-" + XlsxHoja.Celda(fila, 17), Redondear(valores[5] - canalizacionConsolidada), E.Dif);
            suma[15] += tuberiaConsolidada;
            suma[16] += valores[8] - tuberiaConsolidada;
            suma[17] += canalizacionConsolidada;
            suma[18] += valores[5] - canalizacionConsolidada;
            fila++;
        }
        int ultimaDato = fila - 1;
        if (ultimaDato < 6)
        {
            h.Texto(6, 2, "El dibujo no tiene unidades constructivas para mostrar.", E.Nota);
            h.Combinar(6, 2, 6, 18);
            return;
        }

        h.Texto(fila, 2, "TOTAL", E.TotalTexto);
        h.Vacia(fila, 3, E.TotalTexto);
        for (int c = 4; c <= 18; c++)
        {
            XlsxEstilo estilo = (c == 16 || c == 18) ? E.TotalDif : E.TotalMl;
            h.Formula(fila, c, "SUM(" + XlsxHoja.Celda(6, c) + ":" + XlsxHoja.Celda(ultimaDato, c) + ")", Redondear(suma[c]), estilo);
        }
        h.Condicional(6, 16, ultimaDato, 16, "ABS(" + XlsxHoja.Celda(6, 16) + ")>" + Tolerancia.ToString(CultureInfo.InvariantCulture), E.DxfAviso);
        h.Condicional(6, 18, ultimaDato, 18, "ABS(" + XlsxHoja.Celda(6, 18) + ")>" + Tolerancia.ToString(CultureInfo.InvariantCulture), E.DxfAviso);

        h.Texto(fila + 2, 2,
            "El cruce de arroyo suma cero en el total: sale de la UC donde se anotó (negativo) y entra a la UC especial «Cruce de arroyo» (positivo). " +
            "La zanja usa la regla del formato FT-O-108 (0.28 m³/ML en 1/2\" y 3/4\"; 0.30 en troncal; truncado a 2 decimales).", E.Nota);
        h.Combinar(fila + 2, 2, fila + 2, 18);
        h.Alto(fila + 2, 30);

        h.AutoFiltro(5, 2, ultimaDato, 18);
        h.FijarFilas = 5;
        h.FijarColumnas = 3;
        h.FilasTitulo = 5;
    }

    // ---------- ACTIVIDADES ----------

    private static List<string> CodigosDeActividad(ResumenProyecto p)
    {
        var codigos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ResumenAnillo a in p.Anillos) foreach (ResumenUc u in a.Ucs) foreach (string c in u.Actividades.Keys) codigos.Add(c);
        foreach (ResumenUc u in p.Consolidado.Ucs) foreach (string c in u.Actividades.Keys) codigos.Add(c);
        return codigos.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static double CantidadActividad(ResumenAnillo a, string codigo)
    {
        double total = 0.0;
        foreach (ResumenUc u in a.Ucs)
        {
            double cantidad;
            if (u.Actividades.TryGetValue(codigo, out cantidad)) total += cantidad;
        }
        return total;
    }

    private static void Actividades(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Acento;
        List<string> codigos = CodigosDeActividad(p);
        int primeraAnillo = 5;
        int columnaTotal = primeraAnillo + p.Anillos.Count;
        int columnaConsolidado = columnaTotal + 1;
        int columnaDiferencia = columnaTotal + 2;
        Titulo(h, "ACTIVIDADES", "Cantidad de cada código de actividad (la que va al Excel de legalización) por anillo, con su total y el consolidado de GENERAREXCEL.", columnaDiferencia);
        h.Ancho(2, 13); h.Ancho(3, 58); h.Ancho(4, 8);
        for (int i = 0; i < p.Anillos.Count; i++) h.Ancho(primeraAnillo + i, 12);
        h.Ancho(columnaTotal, 13); h.Ancho(columnaConsolidado, 15); h.Ancho(columnaDiferencia, 12);

        h.Texto(5, 2, "Código", E.EncabezadoIzquierda);
        h.Texto(5, 3, "Actividad", E.EncabezadoIzquierda);
        h.Texto(5, 4, "Unidad", E.Encabezado);
        for (int i = 0; i < p.Anillos.Count; i++) h.Texto(5, primeraAnillo + i, p.Anillos[i].Clave, E.Encabezado);
        h.Texto(5, columnaTotal, "Total anillos", E.Encabezado);
        h.Texto(5, columnaConsolidado, "Consolidado GENERAREXCEL", E.Encabezado);
        h.Texto(5, columnaDiferencia, "Diferencia", E.Encabezado);
        h.Alto(5, 32);

        int fila = 6;
        double sumaDiferencias = 0.0;
        foreach (string codigo in codigos)
        {
            h.Texto(fila, 2, codigo, E.Texto);
            h.Texto(fila, 3, ResumenCatalogo.Actividad(codigo), E.Texto);
            h.Texto(fila, 4, ResumenCatalogo.UnidadActividad(codigo), E.TextoCentro);
            double total = 0.0;
            for (int i = 0; i < p.Anillos.Count; i++)
            {
                double valor = CantidadActividad(p.Anillos[i], codigo);
                h.Numero(fila, primeraAnillo + i, Redondear(valor), E.Ml);
                total += valor;
            }
            double consolidado = CantidadActividad(p.Consolidado, codigo);
            if (p.Anillos.Count > 0)
                h.Formula(fila, columnaTotal, "SUM(" + XlsxHoja.Celda(fila, primeraAnillo) + ":" + XlsxHoja.Celda(fila, columnaTotal - 1) + ")", Redondear(total), E.TotalMl);
            else
                h.Numero(fila, columnaTotal, 0.0, E.TotalMl);
            h.Numero(fila, columnaConsolidado, Redondear(consolidado), E.Ml);
            h.Formula(fila, columnaDiferencia, XlsxHoja.Celda(fila, columnaTotal) + "-" + XlsxHoja.Celda(fila, columnaConsolidado), Redondear(total - consolidado), E.Dif);
            sumaDiferencias += Math.Abs(total - consolidado);
            fila++;
        }
        if (codigos.Count == 0)
        {
            h.Texto(fila, 2, "No hay actividades para mostrar.", E.Nota);
            h.Combinar(fila, 2, fila, columnaDiferencia);
            return;
        }
        h.Condicional(6, columnaDiferencia, fila - 1, columnaDiferencia, "ABS(" + XlsxHoja.Celda(6, columnaDiferencia) + ")>" + Tolerancia.ToString(CultureInfo.InvariantCulture), E.DxfAviso);
        h.AutoFiltro(5, 2, fila - 1, columnaDiferencia);
        h.FijarFilas = 5;
        h.FijarColumnas = 3;
        h.FilasTitulo = 5;
    }

    private static void ActividadesPorUc(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Acento;
        Titulo(h, "ACTIVIDADES POR UC", "Las actividades que lleva cada unidad constructiva en su formato de legalización (consolidado del dibujo completo).", 7);
        string[] titulos = { "Diámetro", "Terreno", "Código", "Actividad", "Cantidad", "Unidad" };
        double[] anchos = { 10, 20, 13, 58, 13, 9 };
        for (int i = 0; i < titulos.Length; i++)
        {
            h.Texto(5, 2 + i, titulos[i], i == 3 || i < 2 ? E.EncabezadoIzquierda : E.Encabezado);
            h.Ancho(2 + i, anchos[i]);
        }
        h.Alto(5, 22);

        int fila = 6;
        foreach (ResumenUc uc in p.Consolidado.Ucs)
            foreach (KeyValuePair<string, double> actividad in uc.Actividades.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                h.Texto(fila, 2, ResumenCatalogo.Diametro(uc.Diametro), E.TextoCentro);
                h.Texto(fila, 3, ResumenCatalogo.Terreno(uc.Terreno), E.Texto);
                h.Texto(fila, 4, actividad.Key, E.Texto);
                h.Texto(fila, 5, ResumenCatalogo.Actividad(actividad.Key), E.Texto);
                h.Numero(fila, 6, Redondear(actividad.Value), E.Ml);
                h.Texto(fila, 7, ResumenCatalogo.UnidadActividad(actividad.Key), E.TextoCentro);
                fila++;
            }
        if (fila == 6)
        {
            h.Texto(6, 2, "No hay actividades para mostrar.", E.Nota);
            h.Combinar(6, 2, 6, 7);
            return;
        }
        h.AutoFiltro(5, 2, fila - 1, 7);
        h.FijarFilas = 5;
        h.FilasTitulo = 5;
    }

    // ---------- MATERIALES ----------

    private static string ClaveMaterial(ResumenMaterial m) => m.Descripcion + "|" + m.Diametro + "|" + m.Unidad + "|" + m.Codigo;

    private static void Materiales(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Acento;
        int primeraAnillo = 9;
        int columnaSuma = primeraAnillo + p.Anillos.Count;
        int columnaDiferencia = columnaSuma + 1;
        Titulo(h, "MATERIALES", "Material del proyecto y de pruebas del consolidado (como lo calcula GENERAREXCEL) y cómo se reparte entre los anillos (proyecto + pruebas).", columnaDiferencia);
        double[] anchos = { 16, 10, 13, 8, 12, 12, 12 };
        string[] titulos = { "Material", "Diámetro", "Código", "Unidad", "Proyecto", "Pruebas", "Total" };
        for (int i = 0; i < titulos.Length; i++)
        {
            h.Texto(5, 2 + i, titulos[i], i < 4 ? E.EncabezadoIzquierda : E.Encabezado);
            h.Ancho(2 + i, anchos[i]);
        }
        for (int i = 0; i < p.Anillos.Count; i++) { h.Texto(5, primeraAnillo + i, p.Anillos[i].Clave, E.Encabezado); h.Ancho(primeraAnillo + i, 12); }
        h.Texto(5, columnaSuma, "Suma anillos", E.Encabezado);
        h.Texto(5, columnaDiferencia, "Diferencia", E.Encabezado);
        h.Ancho(columnaSuma, 13); h.Ancho(columnaDiferencia, 12);
        h.Alto(5, 32);

        var porAnillo = new List<Dictionary<string, ResumenMaterial>>();
        foreach (ResumenAnillo a in p.Anillos) porAnillo.Add(a.Materiales.ToDictionary(ClaveMaterial, m => m, StringComparer.OrdinalIgnoreCase));

        int fila = 6;
        foreach (ResumenMaterial m in p.Consolidado.Materiales)
        {
            bool tuberia = m.Descripcion == NormalizeToken("TUBERIA");
            h.Texto(fila, 2, ResumenCatalogo.Material(m.Descripcion), E.Texto);
            h.Texto(fila, 3, ResumenCatalogo.Diametro(m.Diametro), E.TextoCentro);
            h.Texto(fila, 4, m.Codigo, E.Texto);
            h.Texto(fila, 5, m.Unidad, E.TextoCentro);
            XlsxEstilo estilo = tuberia ? E.Ml : E.Entero;
            h.Numero(fila, 6, Redondear(m.Proyecto), estilo);
            h.Numero(fila, 7, Redondear(m.Pruebas), estilo);
            h.Formula(fila, 8, XlsxHoja.Celda(fila, 6) + "+" + XlsxHoja.Celda(fila, 7), Redondear(m.Total), tuberia ? E.TotalMl : E.TotalEntero);
            double suma = 0.0;
            for (int i = 0; i < p.Anillos.Count; i++)
            {
                ResumenMaterial delAnillo;
                double valor = porAnillo[i].TryGetValue(ClaveMaterial(m), out delAnillo) ? delAnillo.Total : 0.0;
                h.Numero(fila, primeraAnillo + i, Redondear(valor), estilo);
                suma += valor;
            }
            if (p.Anillos.Count > 0)
                h.Formula(fila, columnaSuma, "SUM(" + XlsxHoja.Celda(fila, primeraAnillo) + ":" + XlsxHoja.Celda(fila, columnaSuma - 1) + ")", Redondear(suma), tuberia ? E.TotalMl : E.TotalEntero);
            else
                h.Numero(fila, columnaSuma, 0.0, E.TotalMl);
            h.Formula(fila, columnaDiferencia, XlsxHoja.Celda(fila, columnaSuma) + "-" + XlsxHoja.Celda(fila, 8), Redondear(suma - m.Total), E.Dif);
            fila++;
        }
        if (fila == 6)
        {
            h.Texto(6, 2, "No hay material para mostrar.", E.Nota);
            h.Combinar(6, 2, 6, columnaDiferencia);
            return;
        }
        h.Condicional(6, columnaDiferencia, fila - 1, columnaDiferencia, "ABS(" + XlsxHoja.Celda(6, columnaDiferencia) + ")>" + Tolerancia.ToString(CultureInfo.InvariantCulture), E.DxfAviso);
        h.AutoFiltro(5, 2, fila - 1, columnaDiferencia);
        h.FijarFilas = 5;
        h.FijarColumnas = 2;
        h.FilasTitulo = 5;
    }

    // ---------- VERIFICACIONES ----------

    private static void Verificaciones(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Acento;
        Titulo(h, "VERIFICACIONES", "Lo que se revisó del plano. Los errores cambian los números; los avisos conviene confirmarlos. Corrige en el dibujo y vuelve a generar el resumen.", 6);
        h.Ancho(2, 11); h.Ancho(3, 13); h.Ancho(4, 34); h.Ancho(5, 70); h.Ancho(6, 52);
        string[] titulos = { "Estado", "Anillo", "Verificación", "Detalle", "Cómo corregirlo" };
        for (int i = 0; i < titulos.Length; i++) h.Texto(5, 2 + i, titulos[i], i >= 2 ? E.EncabezadoIzquierda : E.Encabezado);
        h.Alto(5, 22);

        var orden = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < p.Anillos.Count; i++) orden[p.Anillos[i].Clave] = i + 1;
        List<ResumenHallazgo> lista = p.Hallazgos
            .OrderBy(x => (int)x.Severidad)
            .ThenBy(x => { int o; return x.Anillo.Length == 0 ? 0 : (orden.TryGetValue(x.Anillo, out o) ? o : int.MaxValue); })
            .ToList();

        int fila = 6;
        foreach (ResumenHallazgo x in lista)
        {
            XlsxEstilo chip; string texto;
            switch (x.Severidad)
            {
                case ResumenSeveridad.Error: chip = E.SevError; texto = "ERROR"; break;
                case ResumenSeveridad.Aviso: chip = E.SevAviso; texto = "AVISO"; break;
                case ResumenSeveridad.Info: chip = E.SevInfo; texto = "INFO"; break;
                default: chip = E.SevOk; texto = "OK"; break;
            }
            h.Texto(fila, 2, texto, chip);
            h.Texto(fila, 3, x.Anillo.Length > 0 ? x.Anillo : "Proyecto", E.TextoCentro);
            h.Texto(fila, 4, x.Verificacion, E.TextoAjustado);
            h.Texto(fila, 5, x.Detalle, E.TextoAjustado);
            h.Texto(fila, 6, x.Correccion, E.TextoAjustado);
            h.Alto(fila, Math.Max(AltoTexto(x.Detalle, 70), Math.Max(AltoTexto(x.Correccion, 52), AltoTexto(x.Verificacion, 34))));
            fila++;
        }
        h.AutoFiltro(5, 2, Math.Max(5, fila - 1), 6);
        h.FijarFilas = 5;
        h.FilasTitulo = 5;
    }

    // ---------- LEEME ----------

    private static void EscribirLeeme(XlsxHoja h, ResumenProyecto p)
    {
        h.ColorPestana = E.Secundario;
        Titulo(h, "LEEME", "Cómo leer este libro y de dónde sale cada número.", 3);
        h.Ancho(2, 30); h.Ancho(3, 110);

        int fila = 5;
        void Bloque(string titulo, params string[] parrafos)
        {
            Seccion(h, fila, titulo, 2, 3);
            fila++;
            foreach (string parrafo in parrafos)
            {
                int separador = parrafo.IndexOf('|');
                string etiqueta = separador > 0 ? parrafo.Substring(0, separador) : string.Empty;
                string texto = separador > 0 ? parrafo.Substring(separador + 1) : parrafo;
                h.Texto(fila, 2, etiqueta, E.TextoNegrita);
                h.Texto(fila, 3, texto, E.TextoAjustado);
                h.Alto(fila, AltoTexto(texto, 110));
                fila++;
            }
            fila++;
        }

        Bloque("Para qué sirve",
            "Objetivo|Reúne en un solo libro todo lo que el plano ya procesa (anillos, restas, actividades y materiales) para compararlo con la información de interventoría, encontrar discrepancias y corregirlas en el dibujo.",
            "Qué no hace|No cambia el dibujo ni la lógica de las otras herramientas. Solo lee lo que ya está anotado y lo presenta; los números son los mismos del Excel de legalización (GENERAREXCEL) y de los informes (RESUMENOBRA).");

        Bloque("Cómo usarlo",
            "1|Genera el resumen con el plano abierto y guardado (botón «Resumen proyecto»).",
            "2|Revisa VERIFICACIONES: los errores cambian los números del plano; los avisos son cosas por confirmar.",
            "3|En COMPARAR digita en las celdas amarillas lo que reporta interventoría. La diferencia y el estado («Coincide», «Diferencia», «Pendiente») se calculan solos con la tolerancia de arriba.",
            "4|Si hay diferencia, ubica el anillo en ANILLOS y la unidad constructiva en UC POR ANILLO; corrige en el plano y vuelve a generar.");

        Bloque("Qué trae cada hoja",
            "RESUMEN|Datos del cajetín, cifras principales, tubería por terreno y diámetro, alertas e índice.",
            "COMPARAR|Plano contra interventoría, por concepto: tubería, excavación, restas, accesorios y tubería por anillo.",
            "ANILLOS|Una fila por anillo y los totales; debajo, el consolidado de GENERAREXCEL y la diferencia.",
            "UC POR ANILLO|Cada diámetro y terreno de cada anillo con cotas, cruce de arroyo, camisa, topo, espiral, pruebas, tubería, excavación y zanja. Se puede filtrar.",
            "UC CONSOLIDADO|Cada diámetro y terreno de todo el proyecto: suma de los anillos (con fórmulas) contra lo que calcula GENERAREXCEL.",
            "ACTIVIDADES|Código de actividad del Excel de legalización por anillo, total y consolidado.",
            "ACTIVIDADES POR UC|Las actividades que lleva cada unidad constructiva en su formato.",
            "MATERIALES|Material del proyecto y de pruebas y su reparto por anillo.",
            "VERIFICACIONES|Revisión automática del plano con la corrección sugerida.");

        Bloque("Cómo se calcula",
            "Anillo|Cada anillo se calcula aparte con sus layouts «ANILLO n DETALLE» y «ANILLO n UC», como lo hace el Excel de legalización; el consolidado repite el cálculo con todo el dibujo a la vez.",
            "Cotas UC|Suma de las cotas de cada capa UC por terreno (el color o el dato del terreno de cada cota).",
            "Restas|Camisa y cruce con topo se restan de la canalización y de la excavación de su UC. El cruce de arroyo sale de su UC y pasa a la UC especial «Cruce de arroyo» (queda en cero, nunca negativo). La espiral y el material de prueba suman a la tubería.",
            "Tubería|Cotas netas + espiral + pruebas, por unidad constructiva. Es la cantidad de TUBERIA y de planos as-built del Excel de legalización.",
            "Excavación|Cotas netas menos camisa y cruce con topo, sin negativos (igual que FT-O-108). La zanja multiplica por 0.28 m³/ML en 1/2\" y 3/4\" y por 0.30 en troncal. En el consolidado se trunca a 2 decimales por unidad constructiva, como el formato; por anillo se muestra sin truncar, así que la suma de anillos puede diferir unos centésimos.",
            "Colores|Amarillo: se digita. Azul oscuro: título o encabezado. Banda azul clara: totales. Rojo: error o diferencia. Ámbar: aviso.");

        Bloque("Qué no incluye todavía",
            "Rendimientos|Esta hoja no calcula rendimientos por día ni días en obra. El libro está armado por secciones independientes: se pueden agregar hojas nuevas (como rendimientos por día) sin tocar las que ya hay.",
            "Tolerancia|Las diferencias se comparan con la tolerancia de COMPARAR (por defecto 0.01).");

        Bloque("Datos de esta generación",
            "Dibujo|" + p.Dibujo,
            "Generado|" + Fecha(p.Generado) + (p.Version.Length > 0 ? "   ·   AutoKADN " + p.Version : string.Empty),
            "Anillos leídos|" + p.Anillos.Count + (p.Anillos.Count > 0 ? " (" + string.Join(", ", p.Anillos.Select(a => a.Clave)) + ")" : string.Empty));
    }
}
