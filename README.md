# Fran Capture Studio

Aplicación nativa para **Windows 11 (x64)** de capturas de pantalla, grabación de video/GIF, edición, OCR y documentación de errores.
Funciona en local: sin cuentas, sin telemetría y sin subir nada a ningún servicio.

- **Fabricante:** Desarrollos Francisco J Vazquez Santiago
- **Ejecutable:** `FranCaptureStudio.exe` · **Instalador:** `FranCaptureStudio-Setup.exe`
- **Tecnología:** C# · .NET 10 · WPF (+ Windows Forms para el selector) · SQLite · FFmpeg (LGPL)

## Descargar el instalador

El instalador se genera en cada compilación de GitHub Actions (runner de Windows):

1. Abre la pestaña **Actions** del repositorio → flujo **«Compilar e instalar (Windows)»** → la ejecución más reciente en verde.
2. En **Artifacts**, descarga **`FranCaptureStudio-Setup`** (un .zip con `FranCaptureStudio-Setup.exe` y su SHA-256).
3. Ejecuta `FranCaptureStudio-Setup.exe`.

> El instalador **no está firmado digitalmente** (no hay certificado de firma de código). Windows SmartScreen puede mostrar
> «Windows protegió su PC»: pulsa *Más información → Ejecutar de todas formas*. Puedes comprobar el archivo con el SHA-256 publicado.

La instalación es por usuario (no pide permisos de administrador), crea el acceso directo del menú Inicio y, opcionalmente,
el del escritorio y el inicio con Windows (ambos desmarcados por defecto). Incluye el runtime de .NET: no hay que instalar nada más.

## Uso rápido

1. Pulsa **F9** (configurable) desde cualquier programa: aparece el selector de región sobre la pantalla congelada.
2. Arrastra para seleccionar; ajusta con los tiradores; elige **Capturar**, **Editar**, **Copiar**, **Guardar como…**, **OCR**, **Fijar**, **Extendida** o **Grabar**.
3. La captura se guarda en `Imágenes\Fran Capture Studio`, se copia al portapapeles y se abre en el editor (todo configurable en *Automatizaciones*).
4. La aplicación sigue en la bandeja del sistema respondiendo a los atajos hasta que elijas **Salir completamente**.

Más detalles en [docs/MANUAL.md](docs/MANUAL.md) y [docs/ATAJOS.md](docs/ATAJOS.md).

## Documentación

| Documento | Contenido |
|---|---|
| [docs/MANUAL.md](docs/MANUAL.md) | Manual de uso |
| [docs/ATAJOS.md](docs/ATAJOS.md) | Cómo modificar los atajos de teclado |
| [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) | Arquitectura y decisiones técnicas |
| [docs/FUNCIONALIDADES.md](docs/FUNCIONALIDADES.md) | Registro de funcionalidades implementadas y **limitaciones reales** |
| [docs/PRUEBAS.md](docs/PRUEBAS.md) | Resultado de compilación y de pruebas (automatizadas y manuales pendientes) |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Dependencias y licencias |

## Compilar

Requisitos en Windows: SDK de .NET 10 e Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
pwsh build\build.ps1          # descarga FFmpeg (verifica SHA-256), compila, prueba, publica y crea artifacts\FranCaptureStudio-Setup.exe
pwsh build\build.ps1 -SkipTests
pwsh build\smoke-test.ps1     # prueba de extremo a extremo del instalador (instala, F9, captura, desinstala)
```

El código también compila en Linux/macOS (`dotnet build`, gracias a `EnableWindowsTargeting`) y allí se ejecutan las pruebas del
núcleo (`dotnet test tests/FranCapture.Core.Tests`); las pruebas de integración y el instalador requieren Windows.

## Estructura

```
src/FranCapture.Core        Lógica sin interfaz: configuración, atajos, biblioteca SQLite, imagen, video, reportes, idiomas
src/FranCaptureStudio       Aplicación WPF: captura, grabación, editor, herramientas, vistas, servicios de Windows
tests/FranCapture.Core.Tests        Pruebas unitarias (multiplataforma)
tests/FranCaptureStudio.WinTests    Pruebas de integración reales en Windows (captura, atajos, grabación, OCR…)
installer/                  Guion de Inno Setup
build/                      Guiones de compilación, descarga de FFmpeg, prueba de humo, icono y traducciones
.github/workflows/build.yml Compilación en runner de Windows y publicación del instalador como artefacto
```
