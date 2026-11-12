# Arquitectura

## Visión general

```
┌──────────────────────────── FranCaptureStudio.exe (WPF, .NET 10, win-x64) ────────────────────────────┐
│ Views (MainWindow + páginas, editores, diálogos)      Tools (QR, lupa, comparador, combinador…)       │
│ Editor (lienzo, anotaciones, historial, proyecto)     Recording (sesión, audio, barra, cámara)        │
│ Capture (GDI, selector WinForms, coordinador, captura extendida)                                      │
│ Services (atajos, bandeja, almacenamiento, biblioteca, OCR, portapapeles, instancia única, FFmpeg)    │
│ Interop (P/Invoke: user32, dwmapi, shcore)                                                            │
└───────────────┬───────────────────────────────────────────────────────────────────────────────────────┘
                │ referencia
┌───────────────▼────────────── FranCapture.Core.dll (.NET 10, sin interfaz, multiplataforma) ──────────┐
│ Settings · Hotkeys · Automation · Storage(naming) · Library(SQLite) · Imaging(stitch, diff, pixel ops)│
│ Video(args FFmpeg, línea de tiempo) · Text(enlaces, datos sensibles, DOCX) · Qa(reporte) · Loc · Log  │
└───────────────────────────────────────────────────────────────────────────────────────────────────────┘
        proceso externo: ffmpeg\ffmpeg.exe (LGPL)        WinRT: Windows.Media.Ocr        WASAPI (NAudio)
```

Toda la lógica que no depende de Windows vive en **FranCapture.Core** y se prueba con pruebas unitarias en cualquier
sistema. La aplicación WPF contiene la interfaz y la integración con el sistema operativo, y se prueba con pruebas de
integración en un runner de Windows.

## Decisiones técnicas (y por qué)

| Tema | Decisión | Motivo |
|---|---|---|
| Interfaz | **WPF** sobre .NET 10 con tema Fluent (`ThemeMode`) y paleta propia | Más maduro y estable que WinUI 3 para una app con ventanas superpuestas, transparencias y bandeja; se distribuye autocontenida |
| Patrón | MVVM ligero: estado y reglas en `Core` (modelos, planes, validadores); las páginas de ajustes se construyen por código enlazando directamente con el modelo; las vistas muy interactivas (lienzo del editor, selector, línea de tiempo) usan código de vista | Evita capas de enlace artificiales donde la interacción es gráfica |
| Captura de imagen | **GDI `BitBlt`** (`Graphics.CopyFromScreen`) con la app en *Per-Monitor DPI Aware v2* | Coordenadas en píxeles físicos exactos en uno o varios monitores con escalados distintos; no requiere permisos ni muestra el borde amarillo. Windows Graphics Capture se evaluó (sugerido en el encargo) pero necesita interoperabilidad Direct3D y no aporta ventajas para capturas fijas; queda como mejora futura para grabar ventanas ocultas |
| Selector de región | Formulario **Windows Forms** a pantalla completa sobre una imagen congelada | Dibuja en píxeles físicos sin virtualización de DPI, lo que evita desajustes entre monitores |
| Exclusión de ventanas propias | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` en barra de grabación, cuenta regresiva, panel rápido, lupa y captura por pasos; la ventana principal se oculta antes de capturar | API oficial; no afecta a otras aplicaciones |
| Atajos | `RegisterHotKey` en una ventana de solo mensajes; sin ganchos de teclado | Windows solo entrega las combinaciones registradas; nada de registro de teclas |
| Grabación | Hilo propio que captura fotogramas a ritmo constante (reloj activo) y los canaliza a **FFmpeg** por stdin; audio **WASAPI** (micrófono y *loopback*) a WAV alineado con el mismo reloj; contenedor temporal **Matroska** y mezcla final | Control total de cursor, clics y teclas; pausa real; sincronía audio/video; el temporal es recuperable si el proceso muere |
| Codificador | Prueba real en este orden: NVENC → Quick Sync → AMF → Media Foundation → OpenH264 → MPEG-4 (con aceleración desactivada: OpenH264 → MF → MPEG-4). WebM: VP9 → VP8 | Hardware cuando existe, software si no. **No se usa libx264** (GPL) |
| FFmpeg | Compilación **LGPL** de BtbN, ejecutable independiente, SHA-256 verificado al compilar | Cumple la licencia sin enlazar código GPL |
| «Mostrar teclas» | Gancho `WH_KEYBOARD_LL` **solo durante una grabación con la opción activada**; muestra únicamente atajos (Ctrl/Alt/Win+tecla) y teclas especiales; nunca caracteres | El texto tecleado y las contraseñas no se muestran ni se guardan |
| OCR | `Windows.Media.Ocr` (WinRT) | Local, sin modelos adicionales |
| Biblioteca | SQLite (`Microsoft.Data.Sqlite`) con solo metadatos y rutas | Los archivos siguen siendo del usuario; borrar un registro no borra el archivo |
| Reportes | PDF con PDFsharp; Markdown/HTML/DOCX con generadores propios | Sin servicios externos |
| Localización | El español es la clave; diccionario es→en generado desde `build/english.tsv` | Cambio de idioma en vivo |
| Instancia única | `Mutex` + canalización con nombre (solo usuario actual) para reenviar órdenes | Sin procesos duplicados |
| Instalador | Inno Setup, por usuario, sin privilegios de administrador | Instalación y desinstalación limpias |

## Flujo de una captura

`Atajo/bandeja/botón → CaptureCoordinator.CaptureAsync → (OverlayForm | ScreenCapture) → ProcessAsync →
AutomationPlan (marca de agua → guardar → biblioteca → portapapeles → editor/notificación)`.

## Flujo de una grabación

`RecordingController.StartAsync → cuenta regresiva + EncoderSelector → RecordingSession (hilo de captura → ffmpeg stdin → video.mkv;
AudioTrack → mic.wav/system.wav) → StopCaptureAsync → FinalizeAsync (mezcla → MP4/WebM, o GIF con paleta) → biblioteca → editor de video`.
Los temporales viven en `%LocalAppData%\FranCaptureStudio\recordings\<sesión>`; si quedan huérfanos se ofrece recuperarlos.

## Seguridad y privacidad

- Sin red: la aplicación no abre conexiones. Las únicas salidas son acciones explícitas del usuario que abren el navegador
  (abrir un enlace detectado, «Buscar texto seleccionado»), siempre con `http/https` validado.
- Sin telemetría, sin cuentas, sin subida de archivos, sin ejecución de comandos configurables.
- El registro local sanea rutas de usuario, correos e IP; puede desactivarse y borrarse.
- La importación de configuración y la apertura de proyectos validan tamaño y estructura.
- `requestedExecutionLevel=asInvoker`: nunca pide elevación.
- No se intenta capturar contenido protegido (DRM o ventanas con afinidad de exclusión): aparece en negro.
