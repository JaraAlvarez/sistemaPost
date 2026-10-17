# Pantallas de administración faltantes — Propuesta

## Usuarios: detalle, roles, PIN y contraseña

> Rama `pantalla-usuarios`. Origen: en Administración → Usuarios solo había Nuevo, Desbloquear, Inactivar y Activar; con el instalador
> premium el Propietario se crea sin código de caja y no tenía cómo ponerse un PIN.

### Lo que ya existía en el servidor (se reutiliza)

| Endpoint | Permiso | Uso en la pantalla |
|---|---|---|
| `PUT identity/users/{id}` | `identity.user.manage` | Editar nombre y correo (se conserva el empleado vinculado) |
| `PUT identity/users/{id}/roles` | `identity.role.manage` | Roles del usuario (no a sí mismo, RN-SEC-05 sin escalar permisos) |
| `POST identity/users/{id}/reset-pin` | `identity.user.manage` | Código de caja + PIN nuevo de otro usuario |
| `POST identity/users/{id}/reset-password` | `identity.user.manage` | Contraseña temporal; obliga a cambiarla y cierra sus sesiones |
| `POST auth/change-pin` | sesión | Cambiar el propio PIN (pedía tener ya código de caja) |
| `GET identity/roles` | `identity.permission.view` | Catálogo de roles con su descripción |

### Decisiones

| # | Decisión | Motivo |
|---|---|---|
| DU-01 | Diálogo de **detalle del usuario** desde la tabla: datos (nombre, correo), roles globales como casillas con su descripción, código de caja y estado del PIN. Cada bloque se guarda con su propio endpoint y solo aparece si la sesión tiene el permiso que pide el servidor | Mismo permiso en la interfaz y en el servidor |
| DU-02 | Los roles asignados **por sucursal** se muestran como "solo en una sucursal" y se reenvían sin cambios al guardar | `PUT roles` reemplaza todas las asignaciones; la pantalla no los gestiona todavía |
| DU-03 | `POST auth/change-pin` acepta además `posCode` y `currentPassword`: si el usuario **no tiene PIN**, lo define confirmando su contraseña y, si tampoco tiene código de caja, eligiéndolo (3 a 6 dígitos, único). Con PIN ya existente se exige el PIN actual y el código no se cambia por esta vía | El Propietario premium (y cualquier usuario sin PIN) puede ponerse uno sin depender de otro administrador; la contraseña evita que una sesión olvidada abierta defina un PIN |
| DU-04 | Nuevo `GET auth/pin`: código de caja, si tiene PIN y la longitud configurada (`security.pin_length`) | La pantalla muestra la regla exacta sin pedir permiso de parámetros |
| DU-05 | `reset-pin` sobre uno mismo se rechaza (`IDENTITY.USE_CHANGE_PIN`), igual que ya hacía `reset-password` | El propio PIN se cambia conociendo el actual o la contraseña |
| DU-06 | Restablecer contraseña o PIN de otro exige que quien lo hace tenga **todos los permisos efectivos** del usuario destino (`IDENTITY.CREDENTIAL_ESCALATION`) | Un gerente con `identity.user.manage` no puede apoderarse de la cuenta del Propietario |
| DU-07 | Nunca queda la empresa **sin Propietario activo** (`IDENTITY.LAST_OWNER`): ni al quitarle el rol ni al inactivarlo, aunque queden Administradores | Complementa RN-SEC-04 (Propietario o Administrador) |
| DU-08 | Auditoría: la que ya hace el servidor (`PIN_RESET`, `PASSWORD_RESET`, `PIN_CHANGED`, cambios de la entidad Usuario). Definir el PIN por primera vez se audita como `PIN_CHANGED` con el texto "definió su código de caja y su PIN" | Sin acciones nuevas en el catálogo |
| DU-09 | Menú del usuario: «Cambiar PIN de caja» junto a «Cambiar contraseña», con la página `/cambiar-pin` | Coherente con la página de contraseña |

Sin migraciones: el modelo ya tiene `pos_code`, `pin_hash` y `must_change_password`.

### Pruebas
- Unitarias (dominio): elección del código de caja propio y regla del último Propietario.
- Integración: PIN propio por primera vez (con contraseña, código único), cambio con PIN actual, `reset-pin` propio rechazado,
  reset de credenciales sin escalar, último Propietario.
