# Registro de funcionalidades y limitaciones

Leyenda: ✅ implementado · 🟡 implementado con una limitación que se explica · ❌ no implementado.
«Verificado» indica cómo se comprobó (ver [PRUEBAS.md](PRUEBAS.md)): **A** = prueba automatizada ejecutada en Windows (CI),
**U** = prueba unitaria, **M** = requiere verificación manual en un equipo real (no se ha ejecutado).

## Fase 1 — Base: bandeja, atajos y capturas

| Función | Estado | Verificado |
|---|---|---|
| Aplicación nativa WPF, tema oscuro/claro, cabeceras sólidas, español/inglés | ✅ | A (construcción de todas las páginas en ambos temas e idiomas) · M (revisión visual) |
| Icono original (marco de captura con «F») | ✅ | — |
| Atajos globales con `RegisterHotKey`; registro, disparo y liberación | ✅ | A |
| «Configurar tecla»: detección, validación, conflicto propio y con otras apps, alternativas | ✅ | A |
| F9 por defecto para el selector; teclas F solas; ImprPant si Windows lo permite | ✅ | A (F9 real en la prueba de humo) · M (ImprPant) |
| Bandeja: menú contextual, cerrar a bandeja o salir, instancia única, restaurar | ✅ | A (proceso/instancia/restaurar) · M (clic en el icono y su menú) |
| Inicio con Windows (opcional, minimizado) | ✅ | A (clave de registro) · M (reinicio real de sesión) |
| Captura de región con pantalla congelada, cruz, dimensiones, ajuste, Esc, barra de acciones | ✅ | A (recorte y resultado) · M (arrastre con ratón) |
| Pantalla completa, monitor específico, ventana activa, ventana por clic, última región, región exacta | ✅ | A (pantalla, región) · M (resto) |
| Temporizador 3/5/10 s y capturas periódicas | ✅ | M |
| Captura extendida por desplazamiento (detección de movimiento, ensamblado, anti-duplicados, aviso de incompatibilidad) | 🟡 | U (algoritmo) · M (aplicaciones reales) |
| Multimonitor y DPI mixto (Per-Monitor v2, píxeles físicos) | ✅ | M (el runner de CI tiene un solo monitor a 100 %) |
| Exclusión de ventanas propias de las capturas | ✅ | A |

🟡 **Captura extendida:** funciona con contenido que se desplaza con la rueda de forma uniforme (documentos, páginas,
listas). Con desplazamiento suave muy lento, contenido animado, video o carga diferida puede detenerse antes de tiempo:
en ese caso conserva lo ensamblado o avisa de que la aplicación no es compatible; nunca genera una imagen corrupta.

## Fase 2 — Editor y biblioteca

| Función | Estado | Verificado |
|---|---|---|
| Recortar, redimensionar, rotar, voltear, borde, sombra, marca de agua, insertar imagen | ✅ | A (recorte, rotación) · M (resto) |
| Lápiz, línea, flecha, rectángulo, elipse, texto, pasos automáticos, resaltador, cuadro de diálogo | ✅ | A (dibujo y exportación) · M (interacción con ratón) |
| Objetos editables: mover, redimensionar, color, grosor, texto, duplicar, traer al frente, eliminar | ✅ | A (mover, duplicar, eliminar) · M |
| Deshacer / rehacer | ✅ | A |
| Pixelado, desenfoque y censura sólida irreversible al exportar | ✅ | A |
| Ocultar datos sensibles automáticamente (OCR local + patrones) | 🟡 | U (patrones) · M (sobre capturas reales) |
| Exportación sin metadatos | ✅ | (los archivos se recodifican desde píxeles) |
| Zoom, desplazamiento, dimensiones y coordenadas | ✅ | M |
| Guardar (escribe y confirma), Guardar como (diálogo nativo), copiar | ✅ | A (guardar, copiar) · M (diálogo nativo) |
| Proyecto editable `.fcsproj`, aviso de cambios sin guardar, autoguardado y recuperación | ✅ | A (proyecto) · M (recuperación tras cierre forzado) |
| Biblioteca SQLite: miniaturas, búsqueda, filtros, orden, favoritos, etiquetas, colecciones, cuadrícula/lista, detalles, Explorador, copiar, renombrar, eliminar con confirmación | ✅ | U + A (datos) · M (interfaz) |
| Almacenamiento: carpetas, plantilla de nombre, formato, guardar automático, preguntar ubicación, copiar | ✅ | A (carpeta elegida, formato, portapapeles) |

🟡 **Ocultar datos sensibles:** es una ayuda, no una garantía; depende de que el OCR lea bien el texto. La aplicación lo
advierte y siempre hay que revisar la imagen. Al rotar, voltear, redimensionar o añadir borde/sombra las anotaciones se
funden con la imagen (se puede deshacer).

## Fase 3 — Grabación

| Función | Estado | Verificado |
|---|---|---|
| Grabar pantalla, monitor, todas las pantallas, ventana, región | ✅ | A (región) · M (resto) |
| Iniciar, pausar, reanudar, detener; barra flotante fuera del video | ✅ | A (motor) · M (barra) |
| MP4 (H.264), WebM (VP9/VP8), GIF | ✅ | A |
| 30/60 FPS, calidad, resolución, cuenta regresiva | ✅ | A (30 FPS) · M (60 FPS depende del equipo) |
| Aceleración por hardware (NVENC/QSV/AMF) con alternativa por software | 🟡 | A (software) · M (hardware: el runner no tiene GPU) |
| Cursor, resaltado de clics, teclas pulsadas (solo atajos, nunca texto) | ✅ | A (regla de privacidad) · M (aspecto en el video) |
| Micrófono y audio del sistema (WASAPI), selección de dispositivo | 🟡 | M (el runner no tiene dispositivos de audio) |
| Cámara web flotante | 🟡 | M (no hay cámara en CI) |
| Temporales recuperables y recuperación tras interrupción | ✅ | A (codificador interrumpido a mitad) · M (cierre real del equipo) |

🟡 **Ventana:** se graba la zona de pantalla que ocupa la ventana y se la sigue si se mueve; si otra ventana la tapa, aparece
lo que hay encima (no se usa Windows Graphics Capture). **Audio:** implementado con relleno de silencios y respeto de
pausas para mantener la sincronía, pero no pudo probarse en CI. **Cámara:** usa DirectShow a través de FFmpeg.
**60 FPS** con captura GDI depende de la resolución y la CPU; si el equipo no llega, se repiten fotogramas (la duración es correcta).

## Fase 4 — Video, OCR y herramientas

| Función | Estado | Verificado |
|---|---|---|
| Editor de video: reproducir, recortar, dividir, eliminar segmentos, sin audio, velocidad, texto/flechas por intervalo, exportar | ✅ | A (recorte, división, superposición, exportación) · M (reproducción e interacción) |
| Convertir a GIF con FPS y ancho; optimización con paleta | ✅ | A |
| Fotograma de portada | 🟡 | M |
| OCR local (Windows.Media.Ocr), español/inglés según paquetes; copiar, editar, TXT, DOCX, enlaces, buscar | ✅ | A (reconocimiento) · U (enlaces, DOCX) |
| Selector de colores, regla, lupa | ✅ | M |
| QR: leer y generar | ✅ | A |
| Fijar imagen en pantalla (tamaño y opacidad) | ✅ | M |
| Comparador (lado a lado, deslizador, diferencias) | ✅ | U (diferencias) · M |
| Captura con pasos numerados | ✅ | A (numeración) · M |
| Combinador y conversor (PNG, JPG, WebP, BMP, GIF, TIFF) | ✅ | A |
| Eliminar fondo por color | ✅ | A |
| Eliminar fondo con modelo local U²-Net (opcional) | 🟡 | M (no probado: el modelo no se distribuye) |

🟡 **Portada:** se guarda como `nombre.portada.png` junto al video y se usa como miniatura en la biblioteca; no se incrusta
dentro del MP4. **Vista previa de WebM:** el reproductor de Windows normalmente no reproduce VP9; la edición y exportación
funcionan igualmente.

## Fase 5 — Productividad

| Función | Estado | Verificado |
|---|---|---|
| Automatizaciones (8 acciones) con activación por regla; sin comandos ni subidas | ✅ | U + A |
| Reporte de errores: capturas, descripción, esperado, pasos, técnica opcional editable; PDF, Markdown, HTML; copiar para GitHub | ✅ | U + A |
| Panel rápido | ✅ | M |
| Exportar/importar configuración con validación | ✅ | U |
| Borrar historial, temporales y registro | ✅ | M |

## Fase 6 — Entrega

| Función | Estado | Verificado |
|---|---|---|
| Instalador `FranCaptureStudio-Setup.exe` (Inno Setup, por usuario, autocontenido) | ✅ | A (prueba de humo) |
| Accesos directos, inicio con Windows opcional y desmarcado, desinstalación que conserva documentos y configuración | ✅ | A |
| GitHub Actions en runner de Windows con artefacto descargable | ✅ | A |
| Firma digital | ❌ | No hay certificado de firma de código: el instalador **no está firmado** |

## Limitaciones conocidas

1. **Sin firma de código**: SmartScreen puede advertir al ejecutar el instalador.
2. **Captura por GDI**: el contenido protegido (DRM, ventanas con exclusión de captura) sale en negro, por diseño. Algunos
   juegos a pantalla completa exclusiva no pueden capturarse con este método.
3. **Grabación de ventana** sin Windows Graphics Capture (ver arriba).
4. **Audio, cámara, codificadores de hardware, multimonitor/DPI mixto e ImprPant** no han podido verificarse en el entorno
   de compilación (sin esos dispositivos); están implementados y quedan como pruebas manuales pendientes.
5. **OCR**: usa los idiomas instalados en Windows; si falta el pedido se usa otro y se avisa.
6. **FFmpeg** añade ~150 MB a la instalación (ejecutable estático LGPL).
7. El editor de video no reproduce la vista previa de formatos que Windows Media Foundation no admite.
8. La interfaz no ha sido revisada por una persona en un equipo con Windows 11: se validó por compilación, pruebas
   automatizadas y capturas generadas en CI.
