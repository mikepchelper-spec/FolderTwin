# Comunicación de vulnerabilidades

La versión publicada es experimental. No hay una versión estable ni una rama histórica con mantenimiento garantizado. La revisión se centra en el candidato identificado en [ENTREGA.md](ENTREGA.md) y en el código actual; no se prometen plazos, recompensas ni cobertura de todas las versiones anteriores.

## Reportar en privado

Usa [Report a vulnerability en GitHub](https://github.com/mikepchelper-spec/FolderTwin/security/advisories/new). El reporte privado de vulnerabilidades está habilitado en este repositorio público y evita abrir una incidencia pública con detalles explotables. También puedes entrar por [Security](https://github.com/mikepchelper-spec/FolderTwin/security).

Describe versión o commit, límite de confianza afectado, impacto y reproducción mínima **con datos sintéticos**. No adjuntes respaldos, archivos personales, credenciales, configuraciones, logs completos ni rutas identificables. El canal privado tampoco requiere compartir esos datos. No investigues sobre dispositivos o datos ajenos.

Si GitHub no permite enviar el reporte, no publiques el exploit como alternativa. Puedes abrir una consulta pública que solo indique que el canal privado no está disponible, sin detalles técnicos sensibles. No hay un correo alternativo establecido.

## Diferenciar los casos

Un acceso fuera de las raíces autorizadas, ejecución de código inesperada o evasión de controles de lectura/escritura puede ser una vulnerabilidad y debe comunicarse en privado. Un fallo de copia o recuperación sin un componente de seguridad se recibe por [los formularios de incidencias](https://github.com/mikepchelper-spec/FolderTwin/issues/new/choose). Ante la duda sobre detalles explotables, utiliza el canal privado.

El mantenimiento reproduce en un entorno desechable, evalúa impacto, prepara una corrección y coordina su publicación mediante el reporte. Una sospecha de pérdida de datos puede requerir retirar el ZIP oficial mientras se investiga. No publiques información de un reporte privado sin revisar su divulgación. [Procedimiento de contención y entrega](MANTENIMIENTO.md).
