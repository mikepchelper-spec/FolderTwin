# Aceptación de la candidata local de interfaz

Entrega local del 6 de noviembre de 2026, America/Bogota. Esta candidata usa Windows 11 x64 y NTFS local con datos sintéticos. La distribución pública sigue siendo RC2.

| Identidad | Valor |
|---|---|
| Código del paquete | `7c568d6e62a85b1051319f43ee15f81ed06703f1` |
| Entrada local | `C:\FolderTwin-Producto\FolderTwin.exe` |
| Acceso directo nuevo | FolderTwin candidata |
| ZIP local | `C:\FolderTwin-Distribucion\FolderTwin-Producto-7c568d6.zip` |
| Tamaño ZIP | 77.348.211 bytes |
| SHA-256 ZIP | `2cec09da5c65d8aad665ef2bce89b944834cd990aea7f49a64c2d00ea2657275` |
| SHA-256 manifiesto | `683fd7a50254879a8626be110ad7483da3592645471041ebb6887dc4404e5e52` |
| SHA-256 fuentes | `55ef5b9e92019e6ccf9292eaf2924949ef831f414ac488b67aafde0669dc7493` |
| SHA-256 motor | `d88d3b9d4f35b07aa80c1dbc7d90c41f0aa462f3d27f6fce75b92e7168cb2a05` |
| Runtime incluido | .NET y Windows Desktop 10.0.14 |
| Contenido | 500 archivos con hash y SHA256SUMS.txt |

Este informe se generó después de aceptar el paquete y queda fuera de su contenido congelado. La copia local anterior de RC2 y su acceso directo se conservaron.

## Resultados de software

Dos árboles limpios extraídos del mismo archivo de Git se compilaron con SDK 10.0.403. Produjeron el mismo manifiesto. Test-Portable comprobó identidad, fuentes, inventario exacto, todos los hashes, licencias, guías y enlaces del contenido. El motor coincide byte por byte con el paquete aceptado de RC2. Los cambios afectan la interfaz, sus pruebas y documentación.

Se ejecutaron desde el paquete con su runtime incluido:

- `FolderTwin.exe --product-smoke` terminó con exit 0. Incluye primer uso, navegación y usabilidad nuevas, instalación, retirada, los tres modos, trabajadores reales, pausa, cancelación, cierre y reapertura, espejo, retención, importación, recuperación e incidencias.
- `CopyLab.exe --smoke` terminó con exit 0. Reabrió FTVERS01 y FTVERS02, verificó y recuperó sin origen ni índice previo. Conservó también la recuperación de demostraciones anteriores.

La comprobación de usabilidad parte de un registro vacío, usa el asistente para los tres modos y compara bytes conocidos y carpeta vacía tras recuperar. Comprueba 85 controles con ayuda, ShowOnDisabled, F1, apertura por clic, preparación de diagnóstico revisable, exclusión de nombres, IDs y rutas, lectura de guía y soporte, acciones fijas y foco en ambos temas. La preparación del resumen se prueba hasta habilitar Copiar. El arnés evita cambiar el portapapeles habitual.

Los casos de ocupado, origen ausente, destino ausente y raíz sustituida usan fixtures nuevos. Se conserva la fecha del respaldo anterior al verificar con el origen ausente y el reintento termina al devolver las raíces correctas. El mensaje de capacidad insuficiente se prueba con un resultado sintético 0x80070070. El disco habitual conserva su espacio. La evidencia física de volumen desechable corresponde al laboratorio VHDX de RC2.

La apertura de la carpeta recuperada se intercepta en el arnés para comprobar la identidad de la salida y la ruta resuelta desde el handle, conservando el Explorador habitual del usuario. El botón público usa el Explorador de Windows.

La ventana compacta se comprobó a 600 × 500 y el recorrido de diseño también a 760 × 620 y 1040 × 740. Se corrigió la pérdida de altura de detalles que reveló una primera prueba fallida. Las revisiones del espejo deshabilitan Aplicar hasta aceptar los requisitos adicionales y el motor conserva su comprobación independiente ante una invocación directa.

Los selectores, el registro y la programación del arnés se inyectan. Los horarios del equipo habitual y el piloto C a D permanecieron fuera de este trabajo.

## Observación de escritorio

Se intentó capturar una sesión nueva del paquete con `--product-synthetic-preview empty`. El árbol accesible mostró Trabajos, Crear mi primer respaldo, Ayuda, acciones y sus explicaciones. La captura de imagen mostró únicamente el marco y un área blanca. Se volvió a seleccionar la misma ventana y se repitió la captura con el mismo resultado.

No se atribuye una observación visual aprobada a esas imágenes. Se cerró únicamente la sesión sintética y se confirmó la salida del proceso creado. La aceptación descrita arriba se basa en los recorridos WPF automatizados.

## Pendientes externos y siguiente uso

El piloto de 7–14 días sigue en pausa por indicación del usuario. Siguen pendientes USB y HDD externos físicos, reinicio, suspensión y sesión, otra cuenta o equipo, nube real y accesibilidad completa con lector, DPI y observación visual fiable. El procedimiento y la evidencia necesaria están en PILOTO y en la matriz de interfaz.

Abre **FolderTwin candidata**, usa **Crear mi primer respaldo** con carpetas desechables y consulta **ⓘ Ayuda y versión** o F1. El cierre de software de esta candidata permite revisar la experiencia completa. La autorización de uso con datos reales requiere las validaciones externas.

[Matriz de requisitos](INTERFAZ-PRODUCTO.md) · [Guía](GUIA.md) · [Piloto](PILOTO.md)
