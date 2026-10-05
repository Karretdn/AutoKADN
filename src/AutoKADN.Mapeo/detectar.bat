@echo off
rem Reconocimiento automatico: lee los PDF de AutoKADN.Proyectos\Recursos\Formatos y escribe un <codigo>.mapa.json por cada uno.
rem OJO: sobrescribe los .mapa.json existentes; si ya los corregiste en la app web, guarda una copia antes.
cd /d "%~dp0"
dotnet run -c Release -- "..\AutoKADN.Proyectos\Recursos\Formatos" %*
pause
