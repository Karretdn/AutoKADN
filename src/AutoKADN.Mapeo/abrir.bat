@echo off
rem Abre la app web de mapeo. Sirve la carpeta src en http://localhost:8765 (hace falta Python) para que la
rem lista "Formatos" (AutoKADN.Proyectos\Recursos\Formatos) funcione y el guardado escriba directo sobre el .mapa.json.
cd /d "%~dp0.."
where python >nul 2>nul
if errorlevel 1 (
  echo No se encontro Python. Puedes abrir web\index.html con doble clic y cargar el PDF y el mapa con los botones.
  pause
  exit /b 1
)
start "" http://localhost:8765/AutoKADN.Mapeo/web/index.html
python -m http.server 8765 --bind 127.0.0.1
