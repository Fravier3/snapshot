# Atajos de teclado

Los atajos son **globales**: funcionan en cualquier programa mientras Fran Capture Studio esté abierto o en la bandeja.
Se registran con la API oficial de Windows `RegisterHotKey`; Windows solo notifica las combinaciones registradas, por lo que la
aplicación no observa ninguna otra tecla. Al **salir completamente** se liberan todos.

## Atajos predeterminados

| Acción | Atajo |
|---|---|
| Abrir el selector de región | **F9** |
| Capturar pantalla completa | Ctrl+Shift+F9 |
| Iniciar grabación | Ctrl+Shift+F10 |
| Pausar o continuar grabación | Ctrl+Shift+F11 |
| Finalizar grabación | Ctrl+Shift+F12 |
| Resto (abrir la app, ventana activa, GIF, OCR, editor, biblioteca, última región, panel rápido) | sin asignar |

## Cambiar un atajo

1. Abre **Fran Capture Studio → Atajos de teclado**.
2. En la fila de la acción pulsa **Configurar tecla**.
3. Pulsa la tecla o combinación (por ejemplo `F8`, `F10`, `Ctrl+Alt+S`). Se muestra al instante lo detectado.
4. La aplicación valida la combinación y **comprueba con Windows si está libre**:
   - ✓ *Disponible*: pulsa **Guardar** (o Enter).
   - Si choca con otro atajo propio, con otra aplicación o con una combinación reservada, se explica el motivo y se ofrecen
     **alternativas disponibles** (botones) que ya han sido comprobadas.
5. Para quitar un atajo usa el icono de papelera; para volver a los valores iniciales, **Restaurar atajos predeterminados**.

## Reglas

- Pueden usarse **solas**: F2–F11, F13–F24, ImprPant y Pausa. (F1 y F12 están reservadas: ayuda y depurador de Windows.)
- Letras, números y demás teclas necesitan **Ctrl** o **Alt** (así no interfieren con la escritura).
- No se admiten combinaciones con la tecla **Windows** ni las reservadas por el sistema (Alt+F4, Alt+Tab, Ctrl+Esc, Ctrl+C/V/X/Z/A/S…).
- **ImprPant**: solo puede registrarse si Windows no la tiene reservada. En Windows 11 desactiva
  *Configuración → Accesibilidad → Teclado → «Usar la tecla Imprimir pantalla para abrir Recortes»*. No se garantiza en todos los equipos.
- Si al iniciar algún atajo guardado está ocupado por otra aplicación, se avisa y se marca en rojo en la página de atajos.

## Dentro del selector de región

`Esc` cancelar · `Enter` o doble clic capturar · flechas mover 1 px (`Ctrl` = 10 px) · `Shift`+flechas cambiar tamaño ·
`Ctrl+C` copiar · `Ctrl+A` seleccionar todo · clic derecho deshacer la selección.

## Editor de imágenes

`V` seleccionar · `C` recortar · `P` lápiz · `L` línea · `A` flecha · `R` rectángulo · `E` elipse · `T` texto · `N` paso ·
`H` resaltador · `G` cuadro de diálogo · `X` pixelar · `U` desenfocar · `K` censura sólida ·
`Ctrl+Z` / `Ctrl+Y` deshacer/rehacer · `Ctrl+S` guardar · `Ctrl+Shift+S` guardar como · `Ctrl+C` copiar · `Ctrl+V` pegar imagen ·
`Ctrl+D` duplicar · `Supr` eliminar · flechas mover (Shift = 10 px) · `Ctrl+rueda` zoom · `Ctrl+0` ajustar · `Ctrl+1` 100 %.

## Editor de video

`Espacio` reproducir/pausar · `←/→` 1 s (`Shift` 5 s, `Ctrl` un fotograma) · `Inicio/Fin` · `I` inicio aquí · `O` final aquí ·
`S` dividir · `Supr` eliminar/restaurar segmento · `Ctrl+Z` deshacer.

## Línea de comandos

`FranCaptureStudio.exe --capture-region | --capture-screen | --capture-window | --record | --ocr | --quick | --minimized | --exit`
(si ya hay una instancia, la orden se le reenvía; nunca se abren dos procesos).
