# Primera prueba de FolderTwin entre C: y D:

Este recorrido usa la candidata local `6f782af` de Windows 11 x64 con datos sintéticos. RC2 es la vista previa publicada. El [piloto formal](PILOTO.md) conserva su propio procedimiento y sus comprobaciones pendientes.

1. Usa la muestra `C:\FolderTwin-Prueba-Sintetica\Origen\muestra.txt`, el destino `D:\FolderTwin-Prueba-Sintetica\Destinos` y la salida de recuperación `C:\FolderTwin-Prueba-Sintetica\Recuperaciones`. En otro equipo, crea carpetas desechables equivalentes y escribe un texto inventado en `muestra.txt`.
2. Cierra las ventanas anteriores de FolderTwin. Abre **FolderTwin · Prueba sintética** en el Escritorio o **FolderTwin.exe** desde la extracción completa. Comprueba `6f782af` en **Ayuda y versión**. El acceso local usa la configuración habitual al abrirlo desde el Escritorio. Si ves otros trabajos, crea uno aparte para estas carpetas y opera solo sobre ese trabajo. En Ruta, pulsa **Nuevo trabajo** o **Crear mi primer respaldo**, selecciona las dos carpetas sintéticas, elige **Versionado** y deja marcada la opción **Automático al detectar cambios**. Puedes dejar la hora diaria en blanco. Revisa las dos rutas antes de guardar.
3. Espera a ver **Vigilando cambios con la sesión iniciada** y una fecha en **Último respaldo completo**. Cierra la ventana, guarda un cambio en `muestra.txt` y vuelve a abrir FolderTwin. En **Recuperar archivos**, elige el punto más reciente y comprueba que aparece la modificación. Un intento sin cambios conserva la fecha del punto anterior.
4. Pulsa **Verificar respaldo**. Selecciona `muestra.txt`, pulsa **Recuperar archivo elegido**, revisa la carpeta de recuperación y confirma con **Recuperar en carpeta nueva**. Abre la salida y comprueba que contiene el texto editado.
5. Si Ruta indica **Monitor detenido o iniciándose** y el estado persiste, abre **Editar trabajo y automatización → Iniciar monitor**. Si indica **Esperando disco**, devuelve el destino registrado y pulsa **Reintentar respaldo**. Para dejar de vigilar solo el trabajo sintético, usa la pausa de ese trabajo en sus ajustes.

## Evidencia de escritorio

Se abrió `FolderTwin.exe` sin argumentos desde el paquete verificado, con un perfil temporal que resolvía su propio `LocalAppData`. Desde el asistente visible se creó un trabajo de versionado entre C: y D:, con la vigilancia marcada y sin horario diario. La tarea nativa del perfil temporal llegó a registrarse, pero su primer inicio terminó con código 1. El supervisor iniciado por Windows utiliza el `LocalAppData` de la sesión y valida que el ejecutor esté bajo la ubicación estable de esa sesión. La instalación del proceso de prueba estaba bajo su perfil temporal.

Para separar ambas comprobaciones, se inició el monitor sin ventana del mismo paquete sobre el registro sintético. Observamos el estado activo, la comparación en ejecución y el punto recuperable. Cerramos Ruta, guardamos un cambio y comprobamos la segunda confirmación antes de reabrir. En **Recuperar archivos** la interfaz mostró `muestra.txt` como modificado. Se verificó el punto y se restauró a una carpeta nueva con el origen apartado. El SHA-256 de la edición y de la salida coincidió en `e700d05a71096dfbaa51bf5330eed5e0f727efe9cce4488ac16851735c48b231`.

Con el monitor detenido, Ruta ofreció iniciar el monitor. Con el destino sintético apartado, el intento quedó **Esperando disco** y pidió reconectar el destino registrado y reintentar. Conservó las fechas de respaldo completo y verificación. Se devolvió la carpeta con su identidad original.

El acceso nuevo abrió una sola ventana de Ruta desde la extracción de `6f782af`, sin argumentos y con un registro aislado vacío. El acceso habitual **FolderTwin candidata** sigue apuntando a `598fbc7`. El ZIP local de `6f782af` conserva el SHA-256 `da80446b9f2960d06331b61e1efb117ba391a047337273e2b8d150cf4ba140e3`.

## Actualización y retirada

En un registro sintético independiente se ejecutó el Programador de tareas desde la sesión interactiva de Windows. El ejecutor estable de `598fbc7` confirmó el primer punto. El arnés llamó al mismo flujo de actualización que usa **Aplicación**. Instaló `6f782af`, conservó el inicio de vigilancia al iniciar sesión y sustituyó el monitor anterior. El trabajo conservó su configuración, identidades e historial. El monitor nuevo confirmó la edición siguiente.

Se retiró la tarea sintética y se deshabilitó ese trabajo. El flujo revisado de retirada quitó solo la instalación creada por el ensayo y conservó la versión anterior. Con el origen apartado se restauraron ambos puntos y se cotejaron los SHA-256 con los de los originales sintéticos.

| Punto | SHA-256 de la restauración |
| --- | --- |
| Antes de actualizar | `5795aaffea1f8b2f79f1d7c10663ecd0b6f99be631f007f1b20317672a00e381` |
| Después de actualizar | `b61d728a627ffc69886ee57d51eecca892f295b6ad4b979a7646ba808e6bfa08` |

La primera retirada desde el lanzador de herramientas conservó un diario incompleto al volver a enumerar un directorio. La sesión interactiva reanudó ese diario correctamente. El recorrido nativo completo posterior terminó con código 0, incluida una retirada nueva. El arnés y sus requisitos están en [Ensayos complementarios](experiments/AutomationAcceptance/README.md).

El flujo visual y el inicio nativo del monitor se comprobaron por separado. La persistencia tras reinicio, suspensión o cierre de sesión, otra cuenta o equipo, USB físico, nube real, accesibilidad externa, el VHDX de `598fbc7` y el piloto formal siguen pendientes.
