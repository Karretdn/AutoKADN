using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace AutoKADN.Tools.Resumen;

// Paleta y estilos del libro del resumen: sobrios, de informe (azul petróleo, bandas claras, números alineados).
internal static class ResumenEstilos
{
    public const string Primario = "FF17445C";
    public const string Acento = "FF2E8B9A";
    public const string Banda = "FFE8F1F4";
    public const string Gris = "FFF4F6F7";
    public const string Borde = "FFC9D3D8";
    public const string Secundario = "FF5B6B75";
    public const string Entrada = "FFFFF8DC";
    public const string Blanco = "FFFFFFFF";

    // Formatos numéricos: el cero se ve como guion en las tablas densas y los negativos en rojo.
    public const string FmtMl = "#,##0.00;[Red]-#,##0.00;\"–\"";
    public const string FmtMlCero = "#,##0.00;[Red]-#,##0.00;0.00";
    public const string FmtEntero = "#,##0;[Red]-#,##0;\"–\"";
    public const string FmtMlSigno = "#,##0.00;-#,##0.00;\"–\"";
    public const string FmtDif = "+#,##0.00;[Red]-#,##0.00;0.00";

    public static readonly XlsxEstilo Titulo = new XlsxEstilo { Tamano = 20, Negrita = true, ColorTexto = Primario };
    public static readonly XlsxEstilo Subtitulo = new XlsxEstilo { Tamano = 11, ColorTexto = Secundario };
    public static readonly XlsxEstilo Nota = new XlsxEstilo { Tamano = 9, ColorTexto = Secundario, Cursiva = true, Ajustar = true, AlineacionVCentro = false };

    public static readonly XlsxEstilo Seccion = new XlsxEstilo
    {
        Tamano = 12, Negrita = true, ColorTexto = Primario, Bordes = XlsxBorde.Inferior, ColorBorde = Acento, BordeGrueso = true,
    };

    public static readonly XlsxEstilo Encabezado = new XlsxEstilo
    {
        Negrita = true, ColorTexto = Blanco, Relleno = Primario, Bordes = XlsxBorde.Todos, ColorBorde = Blanco,
        AlineacionH = XlsxAlineacion.Centro, Ajustar = true,
    };

    public static readonly XlsxEstilo EncabezadoIzquierda = Encabezado.Con(e => e.AlineacionH = XlsxAlineacion.Izquierda);

    public static readonly XlsxEstilo Grupo = new XlsxEstilo
    {
        Negrita = true, ColorTexto = Primario, Relleno = Banda, Bordes = XlsxBorde.Todos, ColorBorde = Borde,
        AlineacionH = XlsxAlineacion.Centro, Ajustar = true,
    };

    public static readonly XlsxEstilo Texto = new XlsxEstilo { Bordes = XlsxBorde.Todos, ColorBorde = Borde };
    public static readonly XlsxEstilo TextoAjustado = Texto.Con(e => { e.Ajustar = true; e.AlineacionVCentro = false; });
    public static readonly XlsxEstilo TextoCentro = Texto.Con(e => e.AlineacionH = XlsxAlineacion.Centro);
    public static readonly XlsxEstilo TextoNegrita = Texto.Con(e => e.Negrita = true);
    public static readonly XlsxEstilo Ml = Texto.Con(e => { e.Formato = FmtMl; e.AlineacionH = XlsxAlineacion.Derecha; });
    // Cantidad con signo propio (el cruce de arroyo resta de una UC y suma en otra): el negativo no es una alarma.
    public static readonly XlsxEstilo MlSigno = Texto.Con(e => { e.Formato = FmtMlSigno; e.AlineacionH = XlsxAlineacion.Derecha; });
    public static readonly XlsxEstilo MlCero = Texto.Con(e => { e.Formato = FmtMlCero; e.AlineacionH = XlsxAlineacion.Derecha; });
    public static readonly XlsxEstilo Entero = Texto.Con(e => { e.Formato = FmtEntero; e.AlineacionH = XlsxAlineacion.Derecha; });
    public static readonly XlsxEstilo Dif = Texto.Con(e => { e.Formato = FmtDif; e.AlineacionH = XlsxAlineacion.Derecha; });
    public static readonly XlsxEstilo EntradaMl = MlCero.Con(e => { e.Relleno = Entrada; e.ColorBorde = Acento; });
    public static readonly XlsxEstilo EntradaTexto = Texto.Con(e => { e.Relleno = Entrada; e.ColorBorde = Acento; e.Ajustar = true; });

    public static readonly XlsxEstilo TotalTexto = new XlsxEstilo
    {
        Negrita = true, Relleno = Banda, Bordes = XlsxBorde.Todos, ColorBorde = Borde, ColorTexto = Primario,
    };
    public static readonly XlsxEstilo TotalMl = TotalTexto.Con(e => { e.Formato = FmtMlCero; e.AlineacionH = XlsxAlineacion.Derecha; });
    public static readonly XlsxEstilo TotalEntero = TotalTexto.Con(e => { e.Formato = FmtEntero; e.AlineacionH = XlsxAlineacion.Derecha; });
    public static readonly XlsxEstilo TotalDif = TotalTexto.Con(e => { e.Formato = FmtDif; e.AlineacionH = XlsxAlineacion.Derecha; });

    public static readonly XlsxEstilo Banner = new XlsxEstilo
    {
        Negrita = true, ColorTexto = Primario, Relleno = Banda, Bordes = XlsxBorde.Todos, ColorBorde = Borde,
    };

    public static readonly XlsxEstilo Enlace = new XlsxEstilo { ColorTexto = "FF0563C1", Subrayado = true };
    public static readonly XlsxEstilo EnlaceTabla = Texto.Con(e => { e.ColorTexto = "FF0563C1"; e.Subrayado = true; });

    public static readonly XlsxEstilo KpiEtiqueta = new XlsxEstilo
    {
        Tamano = 9, ColorTexto = Secundario, Relleno = Banda, Bordes = XlsxBorde.Superior | XlsxBorde.Izquierdo | XlsxBorde.Derecho,
        ColorBorde = Acento, AlineacionH = XlsxAlineacion.Centro, BordeGrueso = true,
    };

    public static readonly XlsxEstilo KpiValor = new XlsxEstilo
    {
        Tamano = 20, Negrita = true, ColorTexto = Primario, Relleno = Banda, Bordes = XlsxBorde.Izquierdo | XlsxBorde.Derecho,
        ColorBorde = Acento, AlineacionH = XlsxAlineacion.Centro, Formato = "#,##0.##", BordeGrueso = true,
    };

    public static readonly XlsxEstilo KpiNota = new XlsxEstilo
    {
        Tamano = 8, ColorTexto = Secundario, Relleno = Banda, Bordes = XlsxBorde.Inferior | XlsxBorde.Izquierdo | XlsxBorde.Derecho,
        ColorBorde = Acento, AlineacionH = XlsxAlineacion.Centro, BordeGrueso = true,
    };

    // Chips de severidad de las verificaciones.
    public static readonly XlsxEstilo SevError = new XlsxEstilo
    {
        Negrita = true, ColorTexto = "FF9C0006", Relleno = "FFFFC7CE", Bordes = XlsxBorde.Todos, ColorBorde = Borde, AlineacionH = XlsxAlineacion.Centro,
    };
    public static readonly XlsxEstilo SevAviso = SevError.Con(e => { e.ColorTexto = "FF7F6000"; e.Relleno = "FFFFEB9C"; });
    public static readonly XlsxEstilo SevInfo = SevError.Con(e => { e.ColorTexto = "FF1F4E79"; e.Relleno = "FFDDEBF7"; });
    public static readonly XlsxEstilo SevOk = SevError.Con(e => { e.ColorTexto = "FF006100"; e.Relleno = "FFC6EFCE"; });

    public static readonly XlsxDxf DxfError = new XlsxDxf { ColorTexto = "FF9C0006", Relleno = "FFFFC7CE", Negrita = true };
    public static readonly XlsxDxf DxfAviso = new XlsxDxf { ColorTexto = "FF7F6000", Relleno = "FFFFEB9C", Negrita = true };
    public static readonly XlsxDxf DxfOk = new XlsxDxf { ColorTexto = "FF006100", Relleno = "FFC6EFCE" };
    public static readonly XlsxDxf DxfPendiente = new XlsxDxf { ColorTexto = "FF7F7F7F" };
}
