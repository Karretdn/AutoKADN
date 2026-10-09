using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace AutoKADN.Tools.Resumen;

// Escritor mínimo de .xlsx (OpenXML a mano). El plugin no depende de librerías de terceros y también compila para
// .NET Framework, así que aquí solo está lo que necesita el resumen del proyecto: hojas con textos, números y
// fórmulas (con su valor ya calculado), estilos, combinar celdas, paneles fijos, filtros, hipervínculos internos,
// formato condicional, y configuración de impresión. No tiene nada de AutoCAD: se puede probar solo.

[Flags]
internal enum XlsxBorde { Ninguno = 0, Izquierdo = 1, Derecho = 2, Superior = 4, Inferior = 8, Todos = 15 }

internal enum XlsxAlineacion { General, Izquierda, Centro, Derecha }

// Estilo de celda. Es una descripción: el libro la convierte en fuente/relleno/borde/formato al guardar. Se compara por
// valor, así que dos estilos iguales comparten el mismo índice.
internal sealed class XlsxEstilo
{
    public string Fuente = "Calibri";
    public double Tamano = 10.0;
    public bool Negrita;
    public bool Cursiva;
    public bool Subrayado;
    public string ColorTexto = "FF1F2933";
    public string Relleno;                     // ARGB; null = sin relleno
    public XlsxBorde Bordes = XlsxBorde.Ninguno;
    public string ColorBorde = "FFBFC9CF";
    public bool BordeGrueso;
    public string Formato;                     // código de formato numérico de Excel; null = General
    public XlsxAlineacion AlineacionH = XlsxAlineacion.General;
    public bool AlineacionVCentro = true;
    public bool Ajustar;                       // ajustar texto
    public int Sangria;

    public XlsxEstilo Con(Action<XlsxEstilo> cambio)
    {
        var copia = (XlsxEstilo)MemberwiseClone();
        cambio(copia);
        return copia;
    }

    internal string Clave() =>
        string.Join("|", Fuente, Tamano.ToString("R", CultureInfo.InvariantCulture), Negrita, Cursiva, Subrayado, ColorTexto, Relleno ?? "-",
            (int)Bordes, ColorColor(), BordeGrueso, Formato ?? "-", (int)AlineacionH, AlineacionVCentro, Ajustar, Sangria);

    private string ColorColor() => Bordes == XlsxBorde.Ninguno ? "-" : ColorBorde;
}

// Formato diferencial (para el formato condicional).
internal sealed class XlsxDxf
{
    public string ColorTexto;
    public string Relleno;
    public bool Negrita;

    internal string Clave() => (ColorTexto ?? "-") + "|" + (Relleno ?? "-") + "|" + Negrita;
}

internal sealed class XlsxLibro
{
    private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly List<XlsxHoja> _hojas = new List<XlsxHoja>();
    private readonly List<XlsxEstilo> _estilos = new List<XlsxEstilo>();
    private readonly Dictionary<string, int> _indiceEstilo = new Dictionary<string, int>();
    private readonly List<string> _cadenas = new List<string>();
    private readonly Dictionary<string, int> _indiceCadena = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly List<XlsxDxf> _dxfs = new List<XlsxDxf>();
    private readonly Dictionary<string, int> _indiceDxf = new Dictionary<string, int>();
    private int _totalCadenas;

    public string Titulo = string.Empty;
    public string Autor = "AutoKADN";
    public string Descripcion = string.Empty;

    public XlsxLibro()
    {
        // El estilo 0 es el predeterminado del libro (lo que usa toda celda sin estilo).
        IndiceEstilo(new XlsxEstilo());
    }

    public IReadOnlyList<XlsxHoja> Hojas => _hojas;

    public XlsxHoja AgregarHoja(string nombre)
    {
        var hoja = new XlsxHoja(this, NombreValido(nombre));
        _hojas.Add(hoja);
        return hoja;
    }

    // Nombre de hoja válido para Excel: sin []:*?/\ y de 31 caracteres como máximo.
    internal static string NombreValido(string nombre)
    {
        var sb = new StringBuilder();
        foreach (char c in nombre ?? string.Empty)
            sb.Append("[]:*?/\\".IndexOf(c) >= 0 ? ' ' : c);
        // Excel tampoco admite un apóstrofo al principio ni al final del nombre.
        string limpio = sb.ToString().Trim().Trim('\x27').Trim();
        if (limpio.Length == 0) limpio = "Hoja";
        return limpio.Length > 31 ? limpio.Substring(0, 31).TrimEnd() : limpio;
    }

    internal int IndiceEstilo(XlsxEstilo estilo)
    {
        string clave = estilo.Clave();
        if (_indiceEstilo.TryGetValue(clave, out int indice)) return indice;
        indice = _estilos.Count;
        _estilos.Add(estilo);
        _indiceEstilo[clave] = indice;
        return indice;
    }

    internal int IndiceCadena(string texto)
    {
        _totalCadenas++;
        if (_indiceCadena.TryGetValue(texto, out int indice)) return indice;
        indice = _cadenas.Count;
        _cadenas.Add(texto);
        _indiceCadena[texto] = indice;
        return indice;
    }

    internal int IndiceDxf(XlsxDxf dxf)
    {
        string clave = dxf.Clave();
        if (_indiceDxf.TryGetValue(clave, out int indice)) return indice;
        indice = _dxfs.Count;
        _dxfs.Add(dxf);
        _indiceDxf[clave] = indice;
        return indice;
    }

    public void Guardar(string ruta)
    {
        if (_hojas.Count == 0) throw new InvalidOperationException("El libro no tiene hojas.");
        // Las hojas se escriben primero para que las cadenas compartidas y los estilos queden completos.
        var partes = new List<KeyValuePair<string, byte[]>>();
        for (int i = 0; i < _hojas.Count; i++)
            partes.Add(new KeyValuePair<string, byte[]>("xl/worksheets/sheet" + (i + 1) + ".xml", Escribir(w => EscribirHoja(w, _hojas[i], i))));
        partes.Add(new KeyValuePair<string, byte[]>("xl/styles.xml", Escribir(EscribirEstilos)));
        partes.Add(new KeyValuePair<string, byte[]>("xl/sharedStrings.xml", Escribir(EscribirCadenas)));
        partes.Add(new KeyValuePair<string, byte[]>("xl/workbook.xml", Escribir(EscribirLibro)));
        partes.Add(new KeyValuePair<string, byte[]>("xl/_rels/workbook.xml.rels", Escribir(EscribirRelacionesLibro)));
        partes.Add(new KeyValuePair<string, byte[]>("_rels/.rels", Escribir(EscribirRelacionesRaiz)));
        partes.Add(new KeyValuePair<string, byte[]>("docProps/core.xml", Escribir(EscribirPropiedades)));
        partes.Add(new KeyValuePair<string, byte[]>("docProps/app.xml", Escribir(EscribirPropiedadesApp)));
        partes.Insert(0, new KeyValuePair<string, byte[]>("[Content_Types].xml", Escribir(EscribirTipos)));

        string carpeta = Path.GetDirectoryName(Path.GetFullPath(ruta));
        if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);
        if (File.Exists(ruta)) File.Delete(ruta);
        using (var archivo = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(archivo, ZipArchiveMode.Create))
        {
            foreach (KeyValuePair<string, byte[]> parte in partes)
            {
                ZipArchiveEntry entrada = zip.CreateEntry(parte.Key, CompressionLevel.Optimal);
                using (Stream salida = entrada.Open()) salida.Write(parte.Value, 0, parte.Value.Length);
            }
        }
    }

    private static byte[] Escribir(Action<XmlWriter> cuerpo)
    {
        using (var memoria = new MemoryStream())
        {
            var ajustes = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false };
            using (XmlWriter w = XmlWriter.Create(memoria, ajustes))
            {
                w.WriteStartDocument(true);
                cuerpo(w);
                w.WriteEndDocument();
            }
            return memoria.ToArray();
        }
    }

    // ---------- partes del paquete ----------

    private void EscribirTipos(XmlWriter w)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        w.WriteStartElement("Types", ns);
        Por(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        Por(w, "xml", "application/xml");
        Sobre(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        for (int i = 0; i < _hojas.Count; i++)
            Sobre(w, "/xl/worksheets/sheet" + (i + 1) + ".xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        Sobre(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        Sobre(w, "/xl/sharedStrings.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml");
        Sobre(w, "/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Sobre(w, "/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        w.WriteEndElement();
    }

    private static void Por(XmlWriter w, string extension, string tipo)
    {
        w.WriteStartElement("Default");
        w.WriteAttributeString("Extension", extension);
        w.WriteAttributeString("ContentType", tipo);
        w.WriteEndElement();
    }

    private static void Sobre(XmlWriter w, string parte, string tipo)
    {
        w.WriteStartElement("Override");
        w.WriteAttributeString("PartName", parte);
        w.WriteAttributeString("ContentType", tipo);
        w.WriteEndElement();
    }

    private static void EscribirRelacionesRaiz(XmlWriter w)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        w.WriteStartElement("Relationships", ns);
        Relacion(w, "rId1", NsRel + "/officeDocument", "xl/workbook.xml");
        Relacion(w, "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        Relacion(w, "rId3", NsRel + "/extended-properties", "docProps/app.xml");
        w.WriteEndElement();
    }

    private void EscribirRelacionesLibro(XmlWriter w)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        w.WriteStartElement("Relationships", ns);
        for (int i = 0; i < _hojas.Count; i++)
            Relacion(w, "rId" + (i + 1), NsRel + "/worksheet", "worksheets/sheet" + (i + 1) + ".xml");
        Relacion(w, "rId" + (_hojas.Count + 1), NsRel + "/styles", "styles.xml");
        Relacion(w, "rId" + (_hojas.Count + 2), NsRel + "/sharedStrings", "sharedStrings.xml");
        w.WriteEndElement();
    }

    private static void Relacion(XmlWriter w, string id, string tipo, string destino)
    {
        w.WriteStartElement("Relationship");
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", tipo);
        w.WriteAttributeString("Target", destino);
        w.WriteEndElement();
    }

    private void EscribirPropiedades(XmlWriter w)
    {
        const string cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string dcterms = "http://purl.org/dc/terms/";
        const string xsi = "http://www.w3.org/2001/XMLSchema-instance";
        w.WriteStartElement("cp", "coreProperties", cp);
        w.WriteAttributeString("xmlns", "dc", null, dc);
        w.WriteAttributeString("xmlns", "dcterms", null, dcterms);
        w.WriteAttributeString("xmlns", "xsi", null, xsi);
        w.WriteElementString("dc", "title", dc, Limpiar(Titulo));
        w.WriteElementString("dc", "creator", dc, Limpiar(Autor));
        w.WriteElementString("dc", "description", dc, Limpiar(Descripcion));
        w.WriteStartElement("dcterms", "created", dcterms);
        w.WriteAttributeString("xsi", "type", xsi, "dcterms:W3CDTF");
        w.WriteString(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private void EscribirPropiedadesApp(XmlWriter w)
    {
        const string ns = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
        w.WriteStartElement("Properties", ns);
        w.WriteElementString("Application", ns, "AutoKADN");
        w.WriteEndElement();
    }

    private void EscribirLibro(XmlWriter w)
    {
        w.WriteStartElement("workbook", NsMain);
        w.WriteAttributeString("xmlns", "r", null, NsRel);
        w.WriteStartElement("bookViews");
        w.WriteStartElement("workbookView");
        w.WriteAttributeString("xWindow", "0");
        w.WriteAttributeString("yWindow", "0");
        w.WriteAttributeString("windowWidth", "28800");
        w.WriteAttributeString("windowHeight", "15000");
        w.WriteAttributeString("activeTab", "0");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("sheets");
        for (int i = 0; i < _hojas.Count; i++)
        {
            w.WriteStartElement("sheet");
            w.WriteAttributeString("name", _hojas[i].Nombre);
            w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("id", NsRel, "rId" + (i + 1));
            w.WriteEndElement();
        }
        w.WriteEndElement();

        // Nombres definidos: títulos de impresión y rango del filtro automático de cada hoja.
        var nombres = new List<KeyValuePair<string, KeyValuePair<int, string>>>();
        for (int i = 0; i < _hojas.Count; i++)
        {
            XlsxHoja h = _hojas[i];
            string cita = CitarHoja(h.Nombre);
            if (h.FilasTitulo > 0)
                nombres.Add(new KeyValuePair<string, KeyValuePair<int, string>>("_xlnm.Print_Titles", new KeyValuePair<int, string>(i, cita + "!$1:$" + h.FilasTitulo.ToString(CultureInfo.InvariantCulture))));
            if (h.Filtro != null)
                nombres.Add(new KeyValuePair<string, KeyValuePair<int, string>>("_xlnm._FilterDatabase", new KeyValuePair<int, string>(i, cita + "!" + h.Filtro.Absoluto())));
        }
        if (nombres.Count > 0)
        {
            w.WriteStartElement("definedNames");
            foreach (KeyValuePair<string, KeyValuePair<int, string>> nombre in nombres)
            {
                w.WriteStartElement("definedName");
                w.WriteAttributeString("name", nombre.Key);
                w.WriteAttributeString("localSheetId", nombre.Value.Key.ToString(CultureInfo.InvariantCulture));
                if (nombre.Key == "_xlnm._FilterDatabase") w.WriteAttributeString("hidden", "1");
                w.WriteString(nombre.Value.Value);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("calcPr");
        w.WriteAttributeString("calcId", "191029");
        w.WriteAttributeString("fullCalcOnLoad", "1");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private void EscribirCadenas(XmlWriter w)
    {
        w.WriteStartElement("sst", NsMain);
        w.WriteAttributeString("count", _totalCadenas.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("uniqueCount", _cadenas.Count.ToString(CultureInfo.InvariantCulture));
        foreach (string cadena in _cadenas)
        {
            w.WriteStartElement("si");
            w.WriteStartElement("t");
            if (cadena.Length > 0 && (char.IsWhiteSpace(cadena[0]) || char.IsWhiteSpace(cadena[cadena.Length - 1]) || cadena.IndexOf('\n') >= 0))
                w.WriteAttributeString("xml", "space", null, "preserve");
            w.WriteString(cadena);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private void EscribirEstilos(XmlWriter w)
    {
        // Tablas de fuentes, rellenos, bordes y formatos que usan los estilos registrados.
        var fuentes = new List<string>();
        var rellenos = new List<string>();
        var bordes = new List<string>();
        var formatos = new List<string>();
        var fuentesXml = new List<Action<XmlWriter>>();
        var rellenosXml = new List<Action<XmlWriter>>();
        var bordesXml = new List<Action<XmlWriter>>();

        // Rellenos obligatorios: ninguno y gris 12.5%.
        rellenos.Add("none"); rellenosXml.Add(x => PatronRelleno(x, "none", null));
        rellenos.Add("gray125"); rellenosXml.Add(x => PatronRelleno(x, "gray125", null));
        bordes.Add("-"); bordesXml.Add(x => BordeVacio(x));

        var xfs = new List<int[]>();
        foreach (XlsxEstilo e in _estilos)
        {
            string fk = e.Fuente + "|" + e.Tamano.ToString("R", CultureInfo.InvariantCulture) + "|" + e.Negrita + "|" + e.Cursiva + "|" + e.Subrayado + "|" + e.ColorTexto;
            int fi = fuentes.IndexOf(fk);
            if (fi < 0)
            {
                fi = fuentes.Count; fuentes.Add(fk);
                XlsxEstilo ee = e;
                fuentesXml.Add(x => Fuente(x, ee));
            }

            int ri = 0;
            if (e.Relleno != null)
            {
                string rk = e.Relleno;
                ri = rellenos.IndexOf(rk);
                if (ri < 0)
                {
                    ri = rellenos.Count; rellenos.Add(rk);
                    string color = e.Relleno;
                    rellenosXml.Add(x => PatronRelleno(x, "solid", color));
                }
            }

            int bi = 0;
            if (e.Bordes != XlsxBorde.Ninguno)
            {
                string bk = (int)e.Bordes + "|" + e.ColorBorde + "|" + e.BordeGrueso;
                bi = bordes.IndexOf(bk);
                if (bi < 0)
                {
                    bi = bordes.Count; bordes.Add(bk);
                    XlsxEstilo ee = e;
                    bordesXml.Add(x => Borde(x, ee));
                }
            }

            int ni = 0;
            if (!string.IsNullOrEmpty(e.Formato))
            {
                int pos = formatos.IndexOf(e.Formato);
                if (pos < 0) { pos = formatos.Count; formatos.Add(e.Formato); }
                ni = 164 + pos;
            }

            xfs.Add(new[] { ni, fi, ri, bi });
        }

        w.WriteStartElement("styleSheet", NsMain);

        if (formatos.Count > 0)
        {
            w.WriteStartElement("numFmts");
            w.WriteAttributeString("count", formatos.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < formatos.Count; i++)
            {
                w.WriteStartElement("numFmt");
                w.WriteAttributeString("numFmtId", (164 + i).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("formatCode", formatos[i]);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("fonts");
        w.WriteAttributeString("count", fuentesXml.Count.ToString(CultureInfo.InvariantCulture));
        foreach (Action<XmlWriter> f in fuentesXml) f(w);
        w.WriteEndElement();

        w.WriteStartElement("fills");
        w.WriteAttributeString("count", rellenosXml.Count.ToString(CultureInfo.InvariantCulture));
        foreach (Action<XmlWriter> r in rellenosXml) r(w);
        w.WriteEndElement();

        w.WriteStartElement("borders");
        w.WriteAttributeString("count", bordesXml.Count.ToString(CultureInfo.InvariantCulture));
        foreach (Action<XmlWriter> b in bordesXml) b(w);
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs");
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("xf");
        w.WriteAttributeString("numFmtId", "0"); w.WriteAttributeString("fontId", "0");
        w.WriteAttributeString("fillId", "0"); w.WriteAttributeString("borderId", "0");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellXfs");
        w.WriteAttributeString("count", xfs.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < xfs.Count; i++)
        {
            XlsxEstilo e = _estilos[i];
            int[] x = xfs[i];
            w.WriteStartElement("xf");
            w.WriteAttributeString("numFmtId", x[0].ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fontId", x[1].ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fillId", x[2].ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("borderId", x[3].ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("xfId", "0");
            if (x[0] != 0) w.WriteAttributeString("applyNumberFormat", "1");
            w.WriteAttributeString("applyFont", "1");
            if (x[2] != 0) w.WriteAttributeString("applyFill", "1");
            if (x[3] != 0) w.WriteAttributeString("applyBorder", "1");
            w.WriteAttributeString("applyAlignment", "1");
            w.WriteStartElement("alignment");
            if (e.AlineacionH != XlsxAlineacion.General)
                w.WriteAttributeString("horizontal", e.AlineacionH == XlsxAlineacion.Izquierda ? "left" : e.AlineacionH == XlsxAlineacion.Centro ? "center" : "right");
            w.WriteAttributeString("vertical", e.AlineacionVCentro ? "center" : "top");
            if (e.Ajustar) w.WriteAttributeString("wrapText", "1");
            if (e.Sangria > 0) w.WriteAttributeString("indent", e.Sangria.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("cellStyles");
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle");
        w.WriteAttributeString("name", "Normal"); w.WriteAttributeString("xfId", "0"); w.WriteAttributeString("builtinId", "0");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("dxfs");
        w.WriteAttributeString("count", _dxfs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (XlsxDxf d in _dxfs)
        {
            w.WriteStartElement("dxf");
            if (d.ColorTexto != null || d.Negrita)
            {
                w.WriteStartElement("font");
                if (d.Negrita) w.WriteElementString("b", string.Empty);
                if (d.ColorTexto != null)
                {
                    w.WriteStartElement("color");
                    w.WriteAttributeString("rgb", d.ColorTexto);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            if (d.Relleno != null)
            {
                w.WriteStartElement("fill");
                w.WriteStartElement("patternFill");
                w.WriteStartElement("bgColor");
                w.WriteAttributeString("rgb", d.Relleno);
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("tableStyles");
        w.WriteAttributeString("count", "0");
        w.WriteAttributeString("defaultTableStyle", "TableStyleMedium2");
        w.WriteAttributeString("defaultPivotStyle", "PivotStyleLight16");
        w.WriteEndElement();

        w.WriteEndElement();
    }

    private static void Fuente(XmlWriter w, XlsxEstilo e)
    {
        w.WriteStartElement("font");
        if (e.Negrita) w.WriteElementString("b", string.Empty);
        if (e.Cursiva) w.WriteElementString("i", string.Empty);
        if (e.Subrayado) w.WriteElementString("u", string.Empty);
        w.WriteStartElement("sz"); w.WriteAttributeString("val", e.Tamano.ToString("R", CultureInfo.InvariantCulture)); w.WriteEndElement();
        w.WriteStartElement("color"); w.WriteAttributeString("rgb", e.ColorTexto); w.WriteEndElement();
        w.WriteStartElement("name"); w.WriteAttributeString("val", e.Fuente); w.WriteEndElement();
        w.WriteStartElement("family"); w.WriteAttributeString("val", "2"); w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void PatronRelleno(XmlWriter w, string patron, string color)
    {
        w.WriteStartElement("fill");
        w.WriteStartElement("patternFill");
        w.WriteAttributeString("patternType", patron);
        if (color != null)
        {
            w.WriteStartElement("fgColor"); w.WriteAttributeString("rgb", color); w.WriteEndElement();
            w.WriteStartElement("bgColor"); w.WriteAttributeString("indexed", "64"); w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void BordeVacio(XmlWriter w)
    {
        w.WriteStartElement("border");
        w.WriteElementString("left", string.Empty);
        w.WriteElementString("right", string.Empty);
        w.WriteElementString("top", string.Empty);
        w.WriteElementString("bottom", string.Empty);
        w.WriteElementString("diagonal", string.Empty);
        w.WriteEndElement();
    }

    private static void Borde(XmlWriter w, XlsxEstilo e)
    {
        w.WriteStartElement("border");
        string trazo = e.BordeGrueso ? "medium" : "thin";
        Lado(w, "left", (e.Bordes & XlsxBorde.Izquierdo) != 0, trazo, e.ColorBorde);
        Lado(w, "right", (e.Bordes & XlsxBorde.Derecho) != 0, trazo, e.ColorBorde);
        Lado(w, "top", (e.Bordes & XlsxBorde.Superior) != 0, trazo, e.ColorBorde);
        Lado(w, "bottom", (e.Bordes & XlsxBorde.Inferior) != 0, trazo, e.ColorBorde);
        w.WriteElementString("diagonal", string.Empty);
        w.WriteEndElement();
    }

    private static void Lado(XmlWriter w, string nombre, bool activo, string trazo, string color)
    {
        w.WriteStartElement(nombre);
        if (activo)
        {
            w.WriteAttributeString("style", trazo);
            w.WriteStartElement("color"); w.WriteAttributeString("rgb", color); w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // ---------- hojas ----------

    private void EscribirHoja(XmlWriter w, XlsxHoja h, int indice)
    {
        w.WriteStartElement("worksheet", NsMain);
        w.WriteAttributeString("xmlns", "r", null, NsRel);

        w.WriteStartElement("sheetPr");
        if (h.ColorPestana != null)
        {
            w.WriteStartElement("tabColor"); w.WriteAttributeString("rgb", h.ColorPestana); w.WriteEndElement();
        }
        w.WriteStartElement("pageSetUpPr"); w.WriteAttributeString("fitToPage", "1"); w.WriteEndElement();
        w.WriteEndElement();

        int ultimaFila = 1, ultimaColumna = 1;
        foreach (KeyValuePair<int, SortedDictionary<int, XlsxCelda>> fila in h.Filas)
        {
            if (fila.Key > ultimaFila) ultimaFila = fila.Key;
            foreach (int columna in fila.Value.Keys) if (columna > ultimaColumna) ultimaColumna = columna;
        }
        w.WriteStartElement("dimension");
        w.WriteAttributeString("ref", "A1:" + XlsxHoja.NombreColumna(ultimaColumna) + ultimaFila.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();

        w.WriteStartElement("sheetViews");
        w.WriteStartElement("sheetView");
        if (!h.MostrarCuadricula) w.WriteAttributeString("showGridLines", "0");
        if (indice == 0) w.WriteAttributeString("tabSelected", "1");
        w.WriteAttributeString("zoomScale", h.Zoom.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("zoomScaleNormal", h.Zoom.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("workbookViewId", "0");
        if (h.FijarFilas > 0 || h.FijarColumnas > 0)
        {
            w.WriteStartElement("pane");
            if (h.FijarColumnas > 0) w.WriteAttributeString("xSplit", h.FijarColumnas.ToString(CultureInfo.InvariantCulture));
            if (h.FijarFilas > 0) w.WriteAttributeString("ySplit", h.FijarFilas.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("topLeftCell", XlsxHoja.NombreColumna(h.FijarColumnas + 1) + (h.FijarFilas + 1).ToString(CultureInfo.InvariantCulture));
            string activo = h.FijarFilas > 0 && h.FijarColumnas > 0 ? "bottomRight" : h.FijarFilas > 0 ? "bottomLeft" : "topRight";
            w.WriteAttributeString("activePane", activo);
            w.WriteAttributeString("state", "frozen");
            w.WriteEndElement();
            w.WriteStartElement("selection");
            w.WriteAttributeString("pane", activo);
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("sheetFormatPr");
        w.WriteAttributeString("defaultRowHeight", "15");
        w.WriteEndElement();

        if (h.Anchos.Count > 0)
        {
            w.WriteStartElement("cols");
            foreach (KeyValuePair<int, double> ancho in h.Anchos)
            {
                w.WriteStartElement("col");
                w.WriteAttributeString("min", ancho.Key.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("max", ancho.Key.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("width", ancho.Value.ToString("R", CultureInfo.InvariantCulture));
                w.WriteAttributeString("customWidth", "1");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("sheetData");
        foreach (KeyValuePair<int, SortedDictionary<int, XlsxCelda>> fila in h.Filas)
        {
            w.WriteStartElement("row");
            w.WriteAttributeString("r", fila.Key.ToString(CultureInfo.InvariantCulture));
            double alto;
            if (h.Altos.TryGetValue(fila.Key, out alto))
            {
                w.WriteAttributeString("ht", alto.ToString("R", CultureInfo.InvariantCulture));
                w.WriteAttributeString("customHeight", "1");
            }
            foreach (KeyValuePair<int, XlsxCelda> par in fila.Value)
                EscribirCelda(w, XlsxHoja.NombreColumna(par.Key) + fila.Key.ToString(CultureInfo.InvariantCulture), par.Value);
            w.WriteEndElement();
        }
        // Filas con altura pero sin celdas.
        w.WriteEndElement();

        if (h.Filtro != null)
        {
            w.WriteStartElement("autoFilter");
            w.WriteAttributeString("ref", h.Filtro.Texto());
            w.WriteEndElement();
        }

        if (h.Combinadas.Count > 0)
        {
            w.WriteStartElement("mergeCells");
            w.WriteAttributeString("count", h.Combinadas.Count.ToString(CultureInfo.InvariantCulture));
            foreach (XlsxRango r in h.Combinadas)
            {
                w.WriteStartElement("mergeCell");
                w.WriteAttributeString("ref", r.Texto());
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        int prioridad = 1;
        foreach (XlsxFormatoCondicional fc in h.Condicionales)
        {
            w.WriteStartElement("conditionalFormatting");
            w.WriteAttributeString("sqref", fc.Rango.Texto());
            w.WriteStartElement("cfRule");
            w.WriteAttributeString("type", "expression");
            w.WriteAttributeString("dxfId", IndiceDxf(fc.Formato).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("priority", (prioridad++).ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("formula", fc.Formula);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        if (h.Enlaces.Count > 0)
        {
            w.WriteStartElement("hyperlinks");
            foreach (XlsxEnlace e in h.Enlaces)
            {
                w.WriteStartElement("hyperlink");
                w.WriteAttributeString("ref", e.Celda);
                w.WriteAttributeString("location", e.Destino);
                if (!string.IsNullOrEmpty(e.Ayuda)) w.WriteAttributeString("tooltip", Limpiar(e.Ayuda));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("pageMargins");
        w.WriteAttributeString("left", "0.4"); w.WriteAttributeString("right", "0.4");
        w.WriteAttributeString("top", "0.6"); w.WriteAttributeString("bottom", "0.6");
        w.WriteAttributeString("header", "0.3"); w.WriteAttributeString("footer", "0.3");
        w.WriteEndElement();

        w.WriteStartElement("pageSetup");
        w.WriteAttributeString("orientation", h.Horizontal ? "landscape" : "portrait");
        w.WriteAttributeString("fitToWidth", "1");
        w.WriteAttributeString("fitToHeight", "0");
        w.WriteEndElement();

        w.WriteStartElement("headerFooter");
        w.WriteElementString("oddFooter", "&L&8&F  ·  &A&C&8Página &P de &N&R&8AutoKADN");
        w.WriteEndElement();

        w.WriteEndElement();
    }

    private void EscribirCelda(XmlWriter w, string referencia, XlsxCelda c)
    {
        w.WriteStartElement("c");
        w.WriteAttributeString("r", referencia);
        if (c.Estilo != 0) w.WriteAttributeString("s", c.Estilo.ToString(CultureInfo.InvariantCulture));
        switch (c.Tipo)
        {
            case XlsxTipoCelda.Texto:
                w.WriteAttributeString("t", "s");
                w.WriteElementString("v", IndiceCadena(Limpiar(c.Texto)).ToString(CultureInfo.InvariantCulture));
                break;
            case XlsxTipoCelda.Numero:
                w.WriteElementString("v", Numero(c.Numero));
                break;
            case XlsxTipoCelda.FormulaNumero:
                w.WriteElementString("f", c.Formula);
                w.WriteElementString("v", Numero(c.Numero));
                break;
            case XlsxTipoCelda.FormulaTexto:
                w.WriteAttributeString("t", "str");
                w.WriteElementString("f", c.Formula);
                w.WriteElementString("v", Limpiar(c.Texto));
                break;
        }
        w.WriteEndElement();
    }

    private static string Numero(double valor)
    {
        if (double.IsNaN(valor) || double.IsInfinity(valor)) valor = 0.0;
        return valor.ToString("R", CultureInfo.InvariantCulture);
    }

    // El XML no admite caracteres de control salvo tabulación y saltos de línea.
    internal static string Limpiar(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return string.Empty;
        StringBuilder sb = null;
        for (int i = 0; i < texto.Length; i++)
        {
            char c = texto[i];
            bool valido = c == '\t' || c == '\n' || c == '\r' || (c >= ' ' && c != '￾' && c != '￿' && !char.IsSurrogate(c));
            if (valido) { sb?.Append(c); continue; }
            if (sb == null) { sb = new StringBuilder(); sb.Append(texto, 0, i); }
            sb.Append(' ');
        }
        return sb == null ? texto : sb.ToString();
    }

    // 'ANILLOS'!  con comillas simples escapadas.
    internal static string CitarHoja(string nombre) => "'" + nombre.Replace("'", "''") + "'";
}

internal enum XlsxTipoCelda { Vacia, Texto, Numero, FormulaNumero, FormulaTexto }

internal struct XlsxCelda
{
    public XlsxTipoCelda Tipo;
    public string Texto;
    public double Numero;
    public string Formula;
    public int Estilo;
}

internal sealed class XlsxRango
{
    public XlsxRango(int fila1, int columna1, int fila2, int columna2)
    {
        Fila1 = fila1; Columna1 = columna1; Fila2 = fila2; Columna2 = columna2;
    }

    public int Fila1, Columna1, Fila2, Columna2;

    public string Texto() =>
        XlsxHoja.NombreColumna(Columna1) + Fila1.ToString(CultureInfo.InvariantCulture) + ":" +
        XlsxHoja.NombreColumna(Columna2) + Fila2.ToString(CultureInfo.InvariantCulture);

    public string Absoluto() =>
        "$" + XlsxHoja.NombreColumna(Columna1) + "$" + Fila1.ToString(CultureInfo.InvariantCulture) + ":$" +
        XlsxHoja.NombreColumna(Columna2) + "$" + Fila2.ToString(CultureInfo.InvariantCulture);
}

internal sealed class XlsxFormatoCondicional
{
    public XlsxRango Rango;
    public string Formula;
    public XlsxDxf Formato;
}

internal sealed class XlsxEnlace
{
    public string Celda;
    public string Destino;
    public string Ayuda;
}

internal sealed class XlsxHoja
{
    private readonly XlsxLibro _libro;

    internal XlsxHoja(XlsxLibro libro, string nombre) { _libro = libro; Nombre = nombre; }

    public string Nombre { get; private set; }
    public string ColorPestana;
    public bool MostrarCuadricula;
    public bool Horizontal = true;
    public int Zoom = 90;
    public int FijarFilas;
    public int FijarColumnas;
    public int FilasTitulo;              // filas que se repiten arriba en la impresión (1..n)
    public XlsxRango Filtro;

    internal readonly SortedDictionary<int, SortedDictionary<int, XlsxCelda>> Filas = new SortedDictionary<int, SortedDictionary<int, XlsxCelda>>();
    internal readonly SortedDictionary<int, double> Anchos = new SortedDictionary<int, double>();
    internal readonly Dictionary<int, double> Altos = new Dictionary<int, double>();
    internal readonly List<XlsxRango> Combinadas = new List<XlsxRango>();
    internal readonly List<XlsxFormatoCondicional> Condicionales = new List<XlsxFormatoCondicional>();
    internal readonly List<XlsxEnlace> Enlaces = new List<XlsxEnlace>();

    // A, B, ... Z, AA, ...
    public static string NombreColumna(int columna)
    {
        var sb = new StringBuilder();
        while (columna > 0)
        {
            int resto = (columna - 1) % 26;
            sb.Insert(0, (char)('A' + resto));
            columna = (columna - 1) / 26;
        }
        return sb.ToString();
    }

    public static string Celda(int fila, int columna) => NombreColumna(columna) + fila.ToString(CultureInfo.InvariantCulture);

    // Referencia con el nombre de la hoja, para fórmulas de otras hojas.
    public string Ref(int fila, int columna) => XlsxLibro.CitarHoja(Nombre) + "!" + Celda(fila, columna);
    public string RefColumna(int columna) => XlsxLibro.CitarHoja(Nombre) + "!$" + NombreColumna(columna) + ":$" + NombreColumna(columna);

    private void Poner(int fila, int columna, XlsxCelda celda, XlsxEstilo estilo)
    {
        celda.Estilo = estilo == null ? 0 : _libro.IndiceEstilo(estilo);
        SortedDictionary<int, XlsxCelda> f;
        if (!Filas.TryGetValue(fila, out f)) { f = new SortedDictionary<int, XlsxCelda>(); Filas[fila] = f; }
        f[columna] = celda;
    }

    public void Texto(int fila, int columna, string valor, XlsxEstilo estilo = null)
    {
        if (string.IsNullOrEmpty(valor)) { Vacia(fila, columna, estilo); return; }
        Poner(fila, columna, new XlsxCelda { Tipo = XlsxTipoCelda.Texto, Texto = valor }, estilo);
    }

    public void Numero(int fila, int columna, double valor, XlsxEstilo estilo = null) =>
        Poner(fila, columna, new XlsxCelda { Tipo = XlsxTipoCelda.Numero, Numero = valor }, estilo);

    // Fórmula con resultado numérico (el valor calculado se guarda para que cualquier lector lo vea sin recalcular).
    public void Formula(int fila, int columna, string formula, double valorCalculado, XlsxEstilo estilo = null) =>
        Poner(fila, columna, new XlsxCelda { Tipo = XlsxTipoCelda.FormulaNumero, Formula = formula, Numero = valorCalculado }, estilo);

    public void FormulaTexto(int fila, int columna, string formula, string valorCalculado, XlsxEstilo estilo = null) =>
        Poner(fila, columna, new XlsxCelda { Tipo = XlsxTipoCelda.FormulaTexto, Formula = formula, Texto = valorCalculado ?? string.Empty }, estilo);

    public void Vacia(int fila, int columna, XlsxEstilo estilo = null) =>
        Poner(fila, columna, new XlsxCelda { Tipo = XlsxTipoCelda.Vacia }, estilo);

    // Pinta el estilo en un rango (para bordes y rellenos de celdas combinadas o bandas).
    public void Rellenar(int fila1, int columna1, int fila2, int columna2, XlsxEstilo estilo)
    {
        for (int f = fila1; f <= fila2; f++)
            for (int c = columna1; c <= columna2; c++)
            {
                SortedDictionary<int, XlsxCelda> fila;
                if (Filas.TryGetValue(f, out fila) && fila.ContainsKey(c)) continue;
                Vacia(f, c, estilo);
            }
    }

    public void Combinar(int fila1, int columna1, int fila2, int columna2) =>
        Combinadas.Add(new XlsxRango(fila1, columna1, fila2, columna2));

    public void Ancho(int columna, double ancho) => Anchos[columna] = ancho;
    public void Alto(int fila, double alto) => Altos[fila] = alto;

    public void AutoFiltro(int fila1, int columna1, int fila2, int columna2) =>
        Filtro = new XlsxRango(fila1, columna1, fila2, columna2);

    // Enlace dentro del libro: lleva a una celda de otra hoja.
    public void Enlace(int fila, int columna, string hojaDestino, string celdaDestino = "A1", string ayuda = null) =>
        Enlaces.Add(new XlsxEnlace { Celda = Celda(fila, columna), Destino = XlsxLibro.CitarHoja(hojaDestino) + "!" + celdaDestino, Ayuda = ayuda });

    // Formato condicional por expresión (la fórmula se escribe para la primera celda del rango, con referencias relativas).
    public void Condicional(int fila1, int columna1, int fila2, int columna2, string formula, XlsxDxf formato) =>
        Condicionales.Add(new XlsxFormatoCondicional { Rango = new XlsxRango(fila1, columna1, fila2, columna2), Formula = formula, Formato = formato });
}
