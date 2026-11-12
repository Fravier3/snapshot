# Manual de uso — Fran Capture Studio

## 1. Primer inicio

Al abrir la aplicación por primera vez se pregunta el idioma, la tecla para abrir el selector de captura (**F9** por defecto,
con botón *Configurar tecla*), qué hacer al cerrar la ventana y si debe iniciarse con Windows. Todo puede cambiarse después.

## 2. Tres formas de uso

- **Modo completo:** la ventana principal (Inicio, Biblioteca, Grabación, OCR, Herramientas, Reporte de errores,
  Automatizaciones, Atajos de teclado, Almacenamiento y Configuración).
- **Modo rápido:** un panel pequeño junto al cursor (asígnale un atajo en *Atajos → Mostrar panel rápido* o ábrelo desde la bandeja).
- **Modo bandeja:** al cerrar la ventana con la X, la aplicación sigue en la bandeja respondiendo a los atajos.
  Clic derecho en el icono: Capturar región, Capturar pantalla, Iniciar/Finalizar grabación, Abrir, Últimas capturas,
  Panel rápido, Configuración y **Salir completamente**. Clic izquierdo: abre la ventana.

En *Configuración → Inicio y cierre* decides si la X minimiza a la bandeja o cierra por completo.

## 3. Capturas

| Modo | Dónde |
|---|---|
| Región (selector) | F9 · Inicio · bandeja |
| Pantalla completa | Ctrl+Shift+F9 · Inicio · bandeja |
| Ventana por clic / ventana activa | Inicio · Herramientas · atajo |
| Monitor específico, última región, región exacta (X, Y, ancho, alto) | Herramientas |
| Temporizador 3/5/10 s, capturas periódicas, captura con pasos | Herramientas |
| Captura extendida (desplazamiento vertical) | botón **Extendida** del selector · Herramientas |

**Selector de región:** la pantalla se congela; arrastra con el cursor en cruz, verás las dimensiones en píxeles y una lupa
con las coordenadas y el color. Tras soltar puedes mover o redimensionar la selección y elegir en la barra:
Capturar, Editar, Copiar, Guardar como…, OCR, Fijar, Extendida, Grabar o cancelar (`Esc`).

**Captura extendida:** selecciona la zona que se desplaza; la aplicación gira la rueda automáticamente y ensambla los
fotogramas (detecta cabeceras y pies fijos y evita duplicados). `Esc` la detiene. Si el contenido no se desplaza de forma
uniforme (video, animaciones, paginación) se avisa y no se genera una imagen dañada.

Qué ocurre después de capturar lo deciden las **Automatizaciones** (guardar, copiar, abrir editor, fecha en el nombre,
convertir a JPG, marca de agua).

## 4. Editor de imágenes

- **Herramientas (barra izquierda):** seleccionar, recortar, lápiz, línea, flecha, rectángulo, elipse, texto, número de paso
  automático, resaltador, cuadro de diálogo, pixelar, desenfocar y censura sólida.
- **Propiedades (barra superior):** color, grosor, tamaño de texto, intensidad del pixelado/desenfoque, relleno, duplicar,
  traer al frente y eliminar. Las anotaciones son objetos: se mueven, se redimensionan con los tiradores y el texto se edita
  con doble clic.
- **Imagen ▾:** redimensionar, rotar, voltear, borde, sombra, marca de agua, insertar imagen, eliminar fondo
  (rotar/voltear/redimensionar/borde/sombra funden las anotaciones; se puede deshacer).
- **Privacidad ▾:** *Ocultar datos sensibles automáticamente* busca con OCR local correos, teléfonos, tarjetas e IP y los tapa
  con censura sólida (revisa siempre el resultado). La **censura sólida** es el método seguro; pixelado y desenfoque son
  solo visuales. La imagen exportada se aplana y no contiene lo que había debajo ni metadatos.
- **Enviar a ▾:** fijar en pantalla, OCR, agregar al reporte de errores, leer QR.
- **Guardar** escribe el archivo (y lo confirma en la barra de estado y con una notificación); **Guardar como…** abre el
  diálogo nativo de Windows. *Archivo ▾ → Guardar proyecto editable* conserva las anotaciones (`.fcsproj`).
- Si cierras con cambios se pregunta antes; además se autoguarda una copia de recuperación cada 20 s que se ofrece al
  reiniciar tras un cierre inesperado.

## 5. Grabación de pantalla

En **Grabación** elige qué grabar (monitor, todas las pantallas, ventana, región o GIF de una región) y las opciones:
FPS (30/60), calidad, resolución, formato (MP4 H.264 o WebM VP9), cuenta regresiva, aceleración por hardware, cursor,
resaltado de clics, teclas pulsadas, audio del sistema, micrófono (y dispositivo) y cámara web.

Durante la grabación aparece una **barra flotante** (duración, pausar/reanudar, detener, descartar y estado del micrófono y
del audio del sistema) que no sale en el video. También puedes usar los atajos. Al detener, el archivo se guarda en
`Videos\Fran Capture Studio` y se abre el editor de video (configurable).

Si la aplicación o el equipo se cierran durante una grabación, al volver a abrirla se ofrece **recuperar** lo grabado.

## 6. Editor de video y GIF

Reproduce, **recorta inicio y final** (I / O), **divide** (S) y **elimina o restaura segmentos** (Supr), quita el audio,
cambia la velocidad (0.5×–4×), añade **texto y flechas** en un intervalo (*+ Texto*, *+ Flecha*, *Desde aquí / Hasta aquí*),
elige un **fotograma de portada** y exporta a **MP4**, **WebM** o **GIF** (FPS y ancho configurables) con barra de progreso
y botón de cancelar. El original nunca se modifica.

## 7. OCR

*OCR → Capturar región / Abrir imagen / Desde el portapapeles.* El texto reconocido es editable; puedes copiarlo, guardarlo
como TXT, exportarlo a DOCX, abrir los enlaces detectados (con confirmación) o buscar el texto seleccionado en el navegador.
El reconocimiento usa el motor de Windows y es 100 % local. Los idiomas disponibles son los paquetes instalados en
*Configuración de Windows → Hora e idioma → Idioma y región* (español e inglés suelen estarlo).

## 8. Herramientas

Selector de colores (HEX/RGB, clic para copiar), regla en pantalla, lupa en vivo, código QR (leer y generar), fijar imagen
en pantalla (rueda = tamaño, Ctrl+rueda = opacidad), comparador (lado a lado, deslizador, diferencias), captura con pasos,
combinador (vertical/horizontal, separación, igualar tamaño), conversor por lotes (PNG, JPG, WebP, BMP, GIF, TIFF) y eliminar
fondo (por color uniforme, o con un modelo local U²-Net opcional copiado en la carpeta de modelos).

## 9. Biblioteca

Todas las capturas y grabaciones creadas con la aplicación, con miniaturas, búsqueda por nombre o etiqueta, filtros
(imágenes/videos/GIF/favoritos/colección), orden por fecha, cuadrícula o lista, y acciones: abrir, copiar, favorito,
etiquetas, colección, renombrar, abrir en el Explorador, quitar de la biblioteca o eliminar (con confirmación, a la Papelera).
Los metadatos están en SQLite, separados de los archivos; si un archivo ya no existe deja de mostrarse.

## 10. Reporte de errores (modo QA)

1. **Capturar el error** (se abre el editor para marcarlo) y adjuntar las capturas necesarias.
2. Rellenar título, qué ocurrió, qué se esperaba y los pasos (uno por línea).
3. Opcional: información técnica editable (sistema, versión, registro saneado).
4. **Copiar para GitHub / chat** (Markdown estructurado) o exportar a **PDF**, **Markdown** o **HTML**.

## 11. Almacenamiento y configuración

- **Almacenamiento:** carpetas de capturas, videos y GIF; plantilla de nombre (`{fecha} {hora} {n}`…); formato y calidad;
  guardar automáticamente; preguntar ubicación; copiar al portapapeles; borrar historial y temporales.
- **Configuración:** tema claro/oscuro, idioma (español/inglés), inicio con Windows, comportamiento al cerrar,
  notificaciones, sonidos, temporizador, cursor, idioma de OCR, buscador, registro local, **exportar/importar configuración**
  (el archivo se valida antes de aplicarse).

## 12. Dónde se guardan los datos

| Qué | Ubicación |
|---|---|
| Capturas / GIF | `Imágenes\Fran Capture Studio` (y `\GIF`) |
| Videos | `Videos\Fran Capture Studio` |
| Configuración y biblioteca | `%AppData%\FranCaptureStudio` (`settings.json`, `library.db`, `models\`) |
| Temporales, miniaturas, recuperación, registro | `%LocalAppData%\FranCaptureStudio` |

Desinstalar no borra tus capturas ni videos; la configuración solo se elimina si lo confirmas.
