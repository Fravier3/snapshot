# Componentes de terceros y licencias

Fran Capture Studio incluye o utiliza los siguientes componentes. Ninguno es GPL enlazado con la aplicación.

| Componente | Versión | Licencia | Uso | Origen |
|---|---|---|---|---|
| .NET Runtime y WPF / Windows Forms | 10.0 | MIT | Plataforma (se distribuye autocontenida) | https://github.com/dotnet |
| FFmpeg (compilación **LGPL** de BtbN, `win64-lgpl`) | 8.1 | LGPL v2.1 o posterior | Codificación de video/GIF/WebP. Se distribuye como **ejecutable independiente** (`ffmpeg\ffmpeg.exe`), no enlazado | https://ffmpeg.org · https://github.com/BtbN/FFmpeg-Builds |
| OpenH264 (dentro de FFmpeg) | — | BSD de 2 cláusulas | Codificador H.264 por software | https://github.com/cisco/openh264 |
| libvpx, libwebp, libopus (dentro de FFmpeg) | — | BSD de 3 cláusulas | WebM (VP9/VP8), WebP y audio Opus | https://www.webmproject.org |
| NAudio (NAudio.Wasapi, NAudio.Core) | 2.2.1 | MIT | Captura de audio WASAPI (micrófono y audio del sistema) | https://github.com/naudio/NAudio |
| ZXing.Net | 0.16.11 | Apache 2.0 | Lectura y generación de códigos QR | https://github.com/micjahn/ZXing.Net |
| Microsoft.Data.Sqlite.Core | 10.0.15 | MIT | Acceso a SQLite (biblioteca local) | https://github.com/dotnet/efcore |
| SQLitePCLRaw + SQLite | 3.0.5 | Apache 2.0 / dominio público | Motor SQLite | https://github.com/ericsink/SQLitePCL.raw · https://sqlite.org |
| PDFsharp (PDFsharp-WPF) | 6.2.4 | MIT | Exportación de reportes a PDF | https://github.com/empira/PDFsharp |
| ONNX Runtime | 1.20.1 | MIT | Ejecución opcional de un modelo local de eliminación de fondo (el modelo no se incluye) | https://github.com/microsoft/onnxruntime |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Utilidades MVVM | https://github.com/CommunityToolkit/dotnet |
| Inno Setup | 6 | Licencia Inno Setup (libre) | Solo para generar el instalador | https://jrsoftware.org/isinfo.php |

## FFmpeg (LGPL)

Este software usa código de [FFmpeg](https://ffmpeg.org) bajo la licencia [LGPL v2.1](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html).
El binario procede sin modificaciones de las compilaciones `win64-lgpl` de https://github.com/BtbN/FFmpeg-Builds, que no incluyen
componentes GPL ni «nonfree» (por ejemplo, **no** contiene libx264). El archivo `ffmpeg\VERSION.txt` de la instalación indica el paquete exacto
y su suma SHA-256, verificada al compilar. El código fuente correspondiente está disponible en https://github.com/FFmpeg/FFmpeg y los
guiones de compilación en el repositorio de BtbN. El usuario puede sustituir `ffmpeg\ffmpeg.exe` por otra compilación compatible.

H.264 y otros formatos pueden estar sujetos a patentes en algunos países; el uso de codificadores de hardware (NVENC, Quick Sync, AMF)
o de Media Foundation depende de los controladores y componentes del propio sistema.

## Proyectos consultados como referencia (sin copiar código)

ShareX (GPL-3.0), ScreenToGif (MS-PL), Flameshot (GPL-3.0) y Windows App SDK (MIT) se consultaron solo como referencia funcional.
Fran Capture Studio es una implementación propia y no contiene código de esos proyectos.

## Modelo opcional de eliminación de fondo

La aplicación puede usar un modelo U²-Net en formato ONNX (`u2netp.onnx` o `u2net.onnx`, licencia Apache 2.0,
https://github.com/xuebinqin/U-2-Net) si el usuario lo copia en la carpeta de modelos. El modelo **no** se distribuye con el instalador.
