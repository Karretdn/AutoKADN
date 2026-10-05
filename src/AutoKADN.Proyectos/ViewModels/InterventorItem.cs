namespace AutoKADN.Proyectos.ViewModels;

/// <summary>Carpeta de interventor existente y, si está en roles.xlsx, su nombre completo con código.</summary>
public sealed class InterventorItem
{
    public InterventorItem(string folderName, string? fullName)
    {
        FolderName = folderName;
        FullName = fullName;
    }

    public string FolderName { get; }

    /// <summary>"IVAN MARTINEZ (14236)" o null si la carpeta no está en roles.xlsx.</summary>
    public string? FullName { get; }

    public override string ToString() => FolderName;
}
