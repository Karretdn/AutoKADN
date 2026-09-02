# AutoKADN

Plugin para AutoCAD 2027 orientado a agilizar tareas repetitivas mediante herramientas interactivas.

## Primera herramienta

### Nomenclatura vial

Flujo previsto:

1. Activar la herramienta.
2. Hacer clic en el plano.
3. Introducir el contenido.
4. Presionar Enter.
5. Crear el texto en el punto seleccionado.
6. Repetir hasta presionar Esc.

## Estructura

- `src/AutoKADN/Commands/` — comandos de AutoCAD.
- `src/AutoKADN/Core/` — infraestructura común.
- `src/AutoKADN/Tools/` — herramientas funcionales.
- `src/AutoKADN/PluginEntry.cs` — entrada del plugin.

## Requisitos

- AutoCAD 2027.
- Visual Studio 2022.
- .NET compatible con la API de AutoCAD 2027.
- Referencias `AcCoreMgd.dll`, `AcDbMgd.dll` y `AcMgd.dll` de AutoCAD 2027.
