# FolderTwin

*Copias locales. Versiones recuperables.*

App sencilla de respaldo para Windows: elegir origen, destino y cómo conservar los archivos. Sin cuenta, suscripción, servidor ni conexión obligatoria. Estado actual: **prueba técnica inicial; aún no hay aplicación de respaldo utilizable**.

## Leer primero

Este proyecto está dedicado al respaldo cotidiano local de carpetas.

- [Plan de trabajo](PLAN.md): experiencia, arquitectura, prioridades, pruebas y límites.
- [Investigación](INVESTIGACION.md): necesidades observadas, documentación técnica y fuentes.
- [Nombres](NOMBRES.md): elección, alternativas anteriores y estado de la comprobación de disponibilidad.

## Decisión propuesta

C# + .NET 10 LTS + WPF con tema Fluent, APIs de archivos de Windows y Programador de tareas. Una interfaz y un ejecutor sin ventana comparten la misma lógica. Sin Electron, servicio remoto ni IA necesaria para funcionar. SQLite en disco fijo local solo para índice/estado; objetos, manifiestos JSONL duplicados y diario recuperable en el destino.

Primero comprobar copia y restauración en carpetas desechables. Después versionado con objetos por contenido SHA-256 y programación. El espejo y la limpieza se incorporan cuando sus protecciones hayan pasado pruebas de interrupción. La interfaz sencilla no debe ocultar fallos ni archivos omitidos; los archivos solo disponibles en la nube no se descargan automáticamente.

Con el SDK de .NET 10, ejecuta `dotnet run --project experiments/CopyProbe` en Windows. El experimento genera sus propios datos temporales y comprueba copia, flush, verificación SHA-256 y restauración. Incluye fallos inyectados, archivos ocupados, corrupción y recuperación de una copia interrumpida a partir de su registro en disco. GitHub Actions ejecuta las mismas comprobaciones. El alcance actual se limita a datos sintéticos en un volumen.

Añade `-- --compare` para comparar el flujo de .NET, CopyFile2 y Robocopy sobre el mismo conjunto sintético. Consulta el [resultado y sus límites](PLAN.md#resultado-de-la-comparación-de-transferencia).

Usa `-- --uncached` para comparar la verificación con y sin la caché de Windows, o `-- --paths` para probar la selección de una raíz local, la sustitución de carpetas y la reapertura de un destino registrado. [Resultados del laboratorio](PLAN.md#fallos-de-es-rutas-y-verificación-sin-caché).

Para abrir la ventana de laboratorio en Windows, ejecuta `dotnet run --project experiments/CopyLab`. Elige las carpetas **Origen** y **Destino** que se muestran en el selector. Después usa **Copiar y verificar** y **Restaurar y verificar**. La ventana trabaja con un archivo generado para la sesión y retira sus archivos temporales al cerrar normalmente. CI ejecuta el recorrido con `-- --smoke`.

La revisión de opiniones externas incorpora diagnóstico de archivos en uso, recuperación por carpetas/fecha, bandeja y cuarentena previa al borrado. Las optimizaciones de E/S y consumo se deciden mediante pruebas. El plan distingue medidas de protección de garantías todavía no demostradas.

Este trabajo no sustituye el respaldo existente: no usar prototipos con datos personales.

## Licencia

El proyecto se distribuye bajo [GNU GPL versión 3](LICENSE).
