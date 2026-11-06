# Soporte de la vista previa

FolderTwin se publica para pruebas con datos sintéticos y carpetas desechables. No es una v1 definitiva ni una recomendación para respaldos personales. No existe un servicio de recuperación de datos ni un plazo de respuesta garantizado.

Consulta primero [GUIA.md](GUIA.md), [INSTALACION.md](INSTALACION.md), [COMPATIBILIDAD.md](COMPATIBILIDAD.md) y [ENTREGA.md](ENTREGA.md). El informe identifica el paquete aceptado; una compilación posterior no hereda automáticamente sus resultados. [PILOTO.md](PILOTO.md) describe las pruebas externas que siguen pendientes.

## Dónde consultar

- [Errores y problemas de recuperación](https://github.com/mikepchelper-spec/FolderTwin/issues/new/choose): elige el formulario correspondiente. Para una pregunta general puedes abrir una incidencia en blanco desde esa pantalla, sin datos privados; no hace falta inventar una reproducción.
- [Vulnerabilidades](SECURITY.md): utiliza el reporte privado de GitHub. No publiques detalles de explotación.
- [Contribuciones](CONTRIBUTING.md): propuestas de código o documentación.

No hay otro correo, chat, servicio externo ni soporte comercial establecido por esta guía.

## Evidencia mínima y privacidad

Indica versión o commit, modo, compilación de Windows y tipo de volumen. Describe la acción, resultado esperado y observado, y pasos mínimos sobre datos inventados. Copia solo el mensaje imprescindible después de quitar información privada. Si no tienes una reproducción sintética, indícalo sin adjuntar tus archivos.

**No adjuntes respaldos, catálogos, configuraciones exportadas, bases de datos, credenciales, rutas absolutas, nombres de usuario, capturas de carpetas personales ni logs completos.** Los nombres de archivo también pueden ser privados. Una casilla del formulario no elimina datos sensibles: revisa el contenido antes de enviarlo.

Ante un hash inesperado, datos cambiados fuera de la salida prevista o un respaldo parcial presentado como completo, detén nuevas ejecuciones del trabajo afectado, deshabilita su horario cuando sea seguro y conserva su estado. No purgues, no repares metadatos a mano y no sobrescribas el origen. Reproduce solo con un fixture nuevo. Si la incidencia involucra datos personales, no los uses para seguir ensayando ni los publiques; este proyecto no ofrece una intervención sobre ellos.
