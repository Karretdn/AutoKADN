using Autodesk.AutoCAD.DatabaseServices;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Lo último que se eligió en el panel «Importar y calcar», guardado en el DWG (diccionario AUTOKADN_CALCO_PANEL): la hoja
// (el gasificado, casi siempre el mismo PDF para todos los anillos del proyecto), su página, el plano del proyecto y las
// casillas. Así el siguiente anillo solo cambia la página.
internal sealed class CalcoMemoria
{
    private const string Clave = "AUTOKADN_CALCO_PANEL";

    public string Hoja { get; set; } = string.Empty;
    public int Pagina { get; set; } = 1;
    public string Plano { get; set; } = string.Empty;
    public bool SoloImportar { get; set; }
    public bool Bloques { get; set; } = true;
    public bool TextosPredio { get; set; } = true;
    public bool CotasVerdes { get; set; } = true;
    public bool CotasMagenta { get; set; } = true;
    public bool Aledanas { get; set; }

    public static CalcoMemoria Leer(Database database)
    {
        var memoria = new CalcoMemoria();
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var diccionario = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (diccionario.Contains(Clave) && transaction.GetObject(diccionario.GetAt(Clave), OpenMode.ForRead) is Xrecord registro && registro.Data != null)
        {
            TypedValue[] v = registro.Data.AsArray();
            if (v.Length >= 4)
            {
                memoria.Hoja = v[0].Value as string ?? string.Empty;
                memoria.Pagina = v[1].Value is int p && p > 0 ? p : 1;
                memoria.Plano = v[2].Value as string ?? string.Empty;
                int c = v[3].Value is int k ? k : 0;
                memoria.SoloImportar = (c & 1) != 0;
                memoria.Bloques = (c & 2) != 0;
                memoria.TextosPredio = (c & 4) != 0;
                memoria.CotasVerdes = (c & 8) != 0;
                memoria.CotasMagenta = (c & 16) != 0;
                memoria.Aledanas = (c & 32) != 0;
            }
        }
        transaction.Commit();
        return memoria;
    }

    public void Guardar(Database database)
    {
        int casillas = (SoloImportar ? 1 : 0) | (Bloques ? 2 : 0) | (TextosPredio ? 4 : 0) | (CotasVerdes ? 8 : 0) | (CotasMagenta ? 16 : 0) | (Aledanas ? 32 : 0);
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var diccionario = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        var datos = new ResultBuffer(
            new TypedValue((int)DxfCode.Text, Hoja),
            new TypedValue((int)DxfCode.Int32, Pagina),
            new TypedValue((int)DxfCode.Text, Plano),
            new TypedValue((int)DxfCode.Int32, casillas));
        if (diccionario.Contains(Clave))
        {
            var registro = (Xrecord)transaction.GetObject(diccionario.GetAt(Clave), OpenMode.ForWrite);
            registro.Data = datos;
        }
        else
        {
            diccionario.UpgradeOpen();
            var registro = new Xrecord { Data = datos };
            diccionario.SetAt(Clave, registro);
            transaction.AddNewlyCreatedDBObject(registro, true);
        }
        transaction.Commit();
    }
}
