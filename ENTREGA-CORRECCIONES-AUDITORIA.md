# Candidata local de correcciones de FolderTwin

La candidata `9c3c59bf472689f26dbff739bd4c21ac12a1a25b` pasó la aceptación de software en Windows 11 x64 con datos sintéticos. Incluye la recuperación local independiente, la tercera copia cifrada y recuperación remota, la conservación de `Zone.Identifier` y la limpieza manual de metadatos. El informe se generó después de verificar la extracción. [Salida resumida de las pruebas](experiments/AutomationAcceptance/audit-candidate-evidence.txt)

| Identidad | Valor |
| --- | --- |
| ZIP | `FolderTwin-win-x64-9c3c59b.zip` |
| Tamaño | 125.451.370 bytes |
| SHA-256 del ZIP | `adb06a6f596b8527a15f615da3e359ee69424f92c5a0fd8aaa5f08aad4acb6f3` |
| SHA-256 de `SHA256SUMS.txt` | `4e90f2635df3d97a241d070c820cc49ae33c246bdeecd494c327fc1e971fcfdc` |
| SHA-256 de `FolderTwin-source.tar` | `100d6c76d7fbb93da412779dc63ca6a9ca06c5a8fffd437a755ac403f08b50e9` |
| SDK y runtime | SDK 10.0.404, .NET y Windows Desktop 10.0.15 |
| Herramientas de nube | Kopia 0.23.1 y rclone 1.76.0 |

Se archivó el commit y se extrajo en dos directorios distintos. Cada compilación publicó su propio árbol y creó un ZIP con el orden y las fechas fijados. Ambos ZIP midieron lo mismo y devolvieron el SHA-256 indicado. `Test-Portable.ps1 -RequireCloudTools` pasó sobre la extracción. Verificó 507 hashes, inventario exacto, fuentes, licencias, documentación y componentes CycloneDX. Windows identificó `FolderTwin.exe`, `CopyLab.exe` y rclone como sin firma Authenticode. La firma de Kopia se observó como válida. La revisión del archivo de fuentes buscó rutas personales, archivos privados y salidas de compilación. [Procedimiento de seguridad](SEGURIDAD-DISTRIBUCION.md)

## Matriz de hallazgos

| Hallazgo | Evidencia y alcance |
| --- | --- |
| A14-01 | Monitor real en C: → D: con tres modos. Una credencial rechazada y un registro de pausa remota dañado conservaron la incidencia y permitieron confirmar dos trabajos locales. Los fallos de transporte y cuota tampoco cambiaron el último punto local. |
| A14-02 | Lector FTVERS03 y ventana independiente desde perfil vacío. Dos fechas por modo con el origen apartado. Se denegó escritura por ACL al respaldo. La prueba cubrió registro de generaciones dañado, réplica dañada, objeto alterado y salida ocupada. |
| A14-03 | Diez transiciones de compactación, dos terminaciones reales y una cadena de 16.775.724 bytes bajo el límite de producción. Se redujo a 1.158 bytes, completó el abandono pendiente y conservó verificable el punto anterior. |
| A14-04 | WPF controló el envío concreto por trabajo, repositorio y fecha. Comprobó pausa, cancelación y reintento del envío seleccionado con otro presente. |
| A14-05 | Ruta y bandeja mostraron historial local confirmado, estado de cuota, atraso remoto y monitor desde el mismo perfil. |
| A14-06 | Fallos e interrupciones de la purga, reanudación, cuarentena y deshacer. Tres modos conservaron recuperable la fecha retenida con el origen apartado. Las contradicciones o catálogos dañados impidieron purgar. |
| A14-07 | `Zone.Identifier` pasó en los tres modos y en transporte S3/rclone sintético. Incluye flujo vacío, ausente y de 16 KiB, bytes opacos, cambio exclusivo de marca e interrupción. Se cotejaron bytes del contenido y de cada marca de manera independiente. El formato remoto anterior sigue legible. |
| A14-08 | WPF seleccionó fecha, archivo y carpeta y mostró el mismo intento tras cerrar y reabrir. Procesos reales probaron cancelación mientras Kopia esperaba, terminación, salida conservada, raíz sustituida y copia local simultánea. El ejecutor del ZIP rechazó una salida ocupada antes de conectar. |
| A14-09 | WPF rechazó una confirmación incorrecta y la creación sin revisión de los datos de emergencia. Reabrió el repositorio en solo lectura antes de activar. Recuperó desde un perfil vacío. |
| A14-10 | Workflow de Windows fija SDK y hashes de herramientas. `actionlint` aprobó la definición y se ejecutaron sus recorridos de compilación y aceptación de nube localmente. Ambos ZIP coincidieron. La ejecución hospedada de esta revisión queda asociada a la integración de la rama. |
| A14-11 | Cabecera y acciones accesibles en tamaño mínimo. Tema claro, oscuro y rama sintética de alto contraste, teclado, foco, ayuda y retorno. A 600×500 la vista compacta dedicó 131,9 DIP a detalles con las acciones secundarias plegadas. |
| A14-12 | Páginas locales de treinta intentos, filtro que encontró una incidencia entre 65 intentos, foco estable y páginas remotas. Se comprobaron stdout excesivo, stderr continuo, salida interrumpida y cancelación de procesos. |
| A14-13 | Inicio normal sin argumentos en perfil aislado. El ensayo de actualización comprobó huellas distintas de los ejecutores, cambio nativo de paquete, historial y recuperación de ambos puntos después de retirar la instalación nueva de prueba. Se incluye la guía autónoma y una pestaña local de ejecutores. |

`9a541ae` pasó la prueba integral de interfaz, instalación, retirada y medición de espacio compacto. `9c3c59b` añadió comprobaciones de identidad y límites al lector remoto. Desde la nueva extracción se repitieron el inicio, recorrido de nube, temas, superficie WPF, retención, monitor C: → D:, actualización nativa, regresiones del motor y pruebas de transporte. El arnés externo cotejó el SHA-256 del motor de la extracción antes de usarlo.

La validación de permisos en loopback comprobó un envío con el puente de escritura habilitado y leyó la misma copia a través del puente de solo lectura. Una escritura autenticada se rechazó con ese puente y no cambió el inventario ni sus hashes. La evidencia cubre la pasarela del producto. Las políticas reales dependen del proveedor. El ensayo de disco lleno y la purga usan presupuestos y fallos inyectados y carpetas de prueba. [Permisos y límites](SEGURIDAD-DISTRIBUCION.md)

## Entrega y pendientes de entorno

Para probar, extrae todo el ZIP y abre `FolderTwin.exe`. Consulta **Ayuda y versión** y **Ejecutores** para identificar la ventana y el paquete del Programador de tareas. La guía explica verificar un respaldo, recuperar en una carpeta nueva y cerrar un intento remoto interrumpido. [Guía de uso](GUIA.md) · [Recuperación de emergencia](RECUPERACION-NUBE.md)

- Piloto sintético de 7–14 días, USB físico NTFS y ciclos reales de reinicio, suspensión y sesión. [Procedimiento](PILOTO.md)
- Otra cuenta o equipo, lector de pantalla y escalado real. [Matriz de validación externa](V1.md)
- AWS S3, B2, Filen y MEGA reales. Probar autenticación, revocación, límites, cuotas y protección contra borrado, incluidas versiones y Object Lock donde se ofrezcan. [Ensayos por proveedor](NUBE.md) · [Permisos](SEGURIDAD-DISTRIBUCION.md)
- La prueba separada del VHDX de `598fbc7` conserva su evidencia y criterio de reanudación. [Laboratorio de capacidad y alias](experiments/AutomationAcceptance/README.md)

La candidata queda destinada a pruebas sintéticas locales. RC2 sigue como entrega experimental pública. [Descarga pública anterior](https://github.com/mikepchelper-spec/FolderTwin/releases/tag/v1.0.0-rc.2)
