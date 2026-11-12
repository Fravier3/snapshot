# Resultado de compilación y pruebas

Todo lo que aparece como «ejecutado» se ejecutó de verdad en GitHub Actions (`windows-latest`, escritorio 1024×768 a 100 %,
sin GPU, sin dispositivos de audio ni cámara, OCR `en-US`). Los resultados de la última ejecución quedan en
[`docs/capturas/`](capturas/) (`resumen-pruebas.txt`, `prueba-de-humo.txt`, `pruebas-omitidas.txt` y capturas de pantalla).
Lo que no pudo ejecutarse está en «Pruebas manuales pendientes»; **nada de esa lista se declara probado**.

## Compilación

| Paso | Resultado |
|---|---|
| `dotnet build` de la solución (Release, win-x64) | ✅ 0 errores |
| Descarga de FFmpeg 8.1 LGPL con verificación SHA-256 | ✅ |
| Publicación autocontenida (ReadyToRun) | ✅ |
| Instalador Inno Setup `FranCaptureStudio-Setup.exe` | ✅ ≈ 103 MB · **sin firma digital** (`NotSigned`) |
| Artefacto `FranCaptureStudio-Setup` en GitHub Actions | ✅ (ejecución n.º 9, commit `50168ec`) |

## Pruebas automatizadas ejecutadas

- **Núcleo — 70 pruebas unitarias: 70 correctas** (también se ejecutan en Linux): atajos (análisis, validación, conflictos,
  alternativas), configuración (persistencia, archivo dañado, normalización, importación validada), automatizaciones, nombres de
  archivo, biblioteca SQLite, pixelado/desenfoque/eliminación de fondo, diferencias, ensamblado de captura extendida,
  argumentos de FFmpeg, línea de tiempo, enlaces, datos sensibles, DOCX, reporte, idiomas.
- **Integración en Windows — 22 pruebas: 22 sin fallos.** Datos reales medidos:
  - Captura de pantalla: el píxel central de una ventana magenta se captura como R255 G0 B255; una ventana marcada como
    excluida no aparece en la captura.
  - Grabación con pausa y reanudación → MP4 480×320, 3,00 s, 30 fps, 90 fotogramas, `libopenh264`, decodificación completa sin errores.
  - GIF 320×213 de 1,5 s y WebM (`libvpx-vp9`) de 1,5 s.
  - Grabación con el codificador interrumpido a la mitad → se recuperan 2,0 s de video válido.
  - Editor de video: recorte + división + segmento eliminado + texto superpuesto → 2,00 s (esperado 2,00 s); exportación a GIF.
  - OCR: reconoce «Fran Capture Studio 2026» y «https://example.com/prueba».
  - Flujo de captura: archivo PNG en la carpeta elegida, imagen idéntica en el portapapeles, registro en la biblioteca; reglas JPG, fecha y marca de agua.
  - Editor: dibujo, censura sólida presente en el archivo guardado, deshacer/rehacer, mover, duplicar, recorte, rotación, guardado; proyecto `.fcsproj`.
  - QR (ida y vuelta), combinador, conversor (JPG, BMP, GIF, TIFF, WebP), eliminar fondo por color, numeración de pasos, PDF/Markdown/HTML del reporte.
  - Atajos: registro, detección de «en uso por otra aplicación», liberación, diálogo «Configurar tecla» con conflicto y alternativas.
  - Todas las páginas se construyen en dos tamaños de ventana, ambos temas y ambos idiomas.
- **Prueba de humo sobre el instalador real — 28 comprobaciones correctas, 1 no verificada** (ver `capturas/prueba-de-humo.txt`):
  instalación silenciosa, archivos, accesos directos, versión, sin inicio automático no pedido, arranque desde el acceso
  directo, instancia única, cerrar a bandeja, captura guardada (1024×768), SQLite, restaurar ventana, salir completamente,
  persistencia, desinstalación que conserva capturas y configuración.

### Importante sobre los atajos

El runner de CI **no entrega de forma fiable pulsaciones sintéticas** al sistema. Por eso:

- «F9 abre el selector con la app en la bandeja» (prueba de humo) quedó como **NO VERIFICADO**.
- De las dos pruebas de integración que disparan un atajo de verdad, «la tecla configurada abre el selector de captura»
  **pasó** en las ejecuciones n.º 5 y n.º 9 (la pulsación llegó con `keybd_event` tras fallar `SendInput`), y «el atajo se
  dispara y deja de dispararse al liberarlo» quedó *omitida* porque la pulsación no llegó (ver `capturas/pruebas-omitidas.txt`).
  Es un comportamiento del entorno, no repetible de forma fiable.

Lo que sí está verificado siempre: Windows acepta el registro, lo rechaza si la combinación está ocupada y lo libera al salir.
**El disparo real de F9 con teclado físico debe comprobarse manualmente** (primera prueba de la lista siguiente).

## Correspondencia con las 20 pruebas obligatorias

| # | Prueba | Estado |
|---|---|---|
| 1 | La aplicación inicia | ✅ ejecutada (humo) |
| 2 | El instalador instala los archivos esperados | ✅ ejecutada |
| 3 | El acceso directo funciona | ✅ ejecutada |
| 4 | La ventana se abre desde la bandeja | 🟡 restaurar con el proceso en bandeja ✅; clic en el icono: manual |
| 5 | Los atajos se registran y desregistran | ✅ ejecutada |
| 6 | Una tecla configurable abre el selector | 🟡 pasó en proceso de pruebas (2 ejecuciones); con la app instalada y teclado físico: **manual** |
| 7 | La selección de región funciona | 🟡 recorte y resultado ✅; arrastre con ratón: manual |
| 8 | La imagen se guarda en la carpeta seleccionada | ✅ ejecutada |
| 9 | Copiar al portapapeles | ✅ ejecutada |
| 10 | El editor aplica cambios | ✅ ejecutada |
| 11 | Deshacer y rehacer | ✅ ejecutada |
| 12 | Iniciar, pausar, reanudar y detener grabación | ✅ ejecutada (motor); barra flotante: manual |
| 13 | Los videos exportados se reproducen | ✅ decodificación completa con FFmpeg; reproductor de Windows: manual |
| 14 | El OCR devuelve texto | ✅ ejecutada (inglés); español: manual |
| 15 | La biblioteca muestra archivos existentes | ✅ ejecutada |
| 16 | La configuración persiste | ✅ ejecutada |
| 17 | Sin procesos duplicados | ✅ ejecutada |
| 18 | Controles en distintas resoluciones y escalados | 🟡 dos tamaños a 100 % ✅; escalados 125–200 %: manual |
| 19 | No se pierde una grabación interrumpida | ✅ ejecutada (codificador interrumpido); apagado real: manual |
| 20 | El desinstalador no elimina documentos | ✅ ejecutada |

## Pruebas manuales pendientes (no ejecutadas)

1. Pulsar **F9** con teclado físico desde otra aplicación; cambiarlo a F8/F10 y a ImprPant.
2. Selector de región con ratón: arrastrar, ajustar tiradores, barra de acciones, Esc; en 2–3 monitores con escalados distintos.
3. Icono de bandeja: clic izquierdo, menú contextual, notificaciones.
4. Grabación con **micrófono y audio del sistema** (sincronía tras pausas), **cámara web**, 60 FPS y codificadores de hardware (NVENC/Quick Sync/AMF).
5. Barra flotante de grabación y cuenta regresiva: que no aparezcan en el video.
6. Grabar una ventana y moverla; grabar todas las pantallas.
7. Captura extendida en navegador, Explorador y un PDF; captura por clic de ventana; temporizador; capturas periódicas.
8. Editor con ratón: todas las herramientas, edición de texto, zoom, «Guardar como» (diálogo nativo), recuperación tras cerrar el proceso a la fuerza.
9. Editor de video: reproducción, línea de tiempo con ratón y teclado, portada.
10. OCR en español; «Ocultar datos sensibles» sobre capturas reales.
11. Selector de colores, regla, lupa, fijar imagen, comparador, panel rápido.
12. Eliminar fondo con un modelo `u2netp.onnx`.
13. Instalador interactivo (asistente, tareas opcionales, desinstalación con la pregunta de borrar configuración), actualización sobre una versión previa, inicio con Windows tras reiniciar sesión.
14. Revisión visual en Windows 11 con escalado 125 %, 150 % y 200 %.
