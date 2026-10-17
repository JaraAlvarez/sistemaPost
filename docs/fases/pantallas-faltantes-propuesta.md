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
| `GET identity/roles` | `identity.permission.view` **o** `identity.role.manage` (PU10) | Catálogo de roles con su descripción |

### Decisiones

| # | Decisión | Motivo |
|---|---|---|
| DU-01 | Diálogo de **detalle del usuario** desde la tabla: datos (nombre, correo), roles globales como casillas con su descripción, código de caja y estado del PIN. Cada bloque se guarda con su propio endpoint y solo aparece si la sesión tiene el permiso que pide el servidor | Mismo permiso en la interfaz y en el servidor |
| DU-02 | Los roles asignados **por sucursal** se muestran como "solo en una sucursal" y se reenvían sin cambios al guardar | `PUT roles` reemplaza todas las asignaciones; la pantalla no los gestiona todavía |
| DU-03 | `POST auth/change-pin` acepta además `posCode` y `currentPassword`: si el usuario **no tiene PIN**, lo define confirmando su contraseña y, si tampoco tiene código de caja, eligiéndolo (3 a 6 dígitos, único). Con PIN ya existente se exige el PIN actual y el código no se cambia por esta vía | El Propietario premium (y cualquier usuario sin PIN) puede ponerse uno sin depender de otro administrador; la contraseña evita que una sesión olvidada abierta defina un PIN |
| DU-04 | Nuevo `GET auth/credentials`: código de caja, si tiene PIN y las longitudes configuradas (`security.pin_length`, `security.password_min_length`). Si falla, la pantalla usa las de fábrica (PU8) | La pantalla muestra la regla exacta sin pedir permiso de parámetros |
| DU-05 | `reset-pin` sobre uno mismo se rechaza (`IDENTITY.USE_CHANGE_PIN`), igual que ya hacía `reset-password` | El propio PIN se cambia conociendo el actual o la contraseña |
| DU-06 | **Jerarquía** (`IDENTITY.TARGET_OUTRANKS`): solo se gestiona a quien no tiene más poder que uno. El poder del destino son los permisos de sus **roles** más sus excepciones GRANT, en cada alcance, **sin restar sus DENY** (PU1). Se aplica a editar (incluido el correo), roles, excepciones, activar/inactivar, desbloquear, `reset-pin` y `reset-password`. En excepciones, quitar un DENY cuenta como conceder ese permiso | Un DENY es reversible: si contara, un Administrador pondría DENY al Propietario, le restablecería la contraseña y quitaría el DENY |
| DU-07 | Nunca queda la empresa **sin Propietario activo** (`IDENTITY.LAST_OWNER`): ni al quitarle el rol ni al inactivarlo, aunque queden Administradores. Antes de contar Propietarios o Administradores se toma `pg_advisory_xact_lock` por empresa dentro de la transacción (PU4) | Complementa RN-SEC-04; dos Propietarios que se degradan a la vez no dejan la empresa sin ninguno |
| DU-08 | Auditoría: la que ya hace el servidor (`PIN_RESET`, `PASSWORD_RESET`, `PIN_CHANGED`, cambios de la entidad Usuario). Definir el PIN por primera vez se audita como `PIN_CHANGED` con el texto "definió su código de caja y su PIN" | Sin acciones nuevas en el catálogo |
| DU-09 | Menú del usuario: «Cambiar PIN de caja» junto a «Cambiar contraseña», con la página `/cambiar-pin` | Coherente con la página de contraseña |
| DU-10 | La contraseña o el PIN actuales en `change-password` y `change-pin` pasan por el mismo contador, bloqueo y auditoría que el ingreso (`LOGIN_FAILED`, `USER_LOCKED`); el intento fallido se confirma y se responde `IDENTITY.CURRENT_*_INVALID` o `IDENTITY.CREDENTIAL_LOCKED` (no 401: la sesión sigue). Ambos endpoints usan el límite de peticiones `auth-login` (PU5). Un cambio exitoso reinicia los intentos fallidos. `/cambiar-clave` muestra la longitud mínima y el historial reales (`GET auth/credentials`); el asistente web de instalación usa el mínimo de fábrica (8), porque la empresa aún no tiene parámetros | Sin esto, una sesión abierta permite adivinar la contraseña o el PIN sin límite |
| DU-11 | `reset-pin` y `change-pin` cierran las sesiones de **caja** abiertas del usuario (salvo la actual) (PU6) | El PIN anterior deja de servir de inmediato |
| DU-12 | El PIN tampoco puede ser igual al código de caja ni uno de una lista corta de PINs comunes (1212, 2580, 1122…) (PU7) | Son los primeros que se prueban |
| DU-13 | `ResetPinCommand` y `ResetPasswordCommand` se permiten con la licencia restringida (R10) (PU11) | La tienda debe poder recuperar el acceso |
| DU-14 | La contraseña temporal sugerida usa `RandomNumberGenerator` y respeta la longitud mínima de la empresa (PU9) | Imprevisible y válida a la primera |
| DU-15 | Editar o borrar un rol personalizado exige tener **todos** sus permisos actuales y poder gestionar a cada usuario que lo tiene (UR1) | Quitarle permisos al rol de un superior, o borrarlo, lo degradaría |
| DU-16 | Para el alcance "todas las sucursales", los permisos propios son los que se tienen en **cada** sucursal: se restan los que niega un DENY por sucursal de quien actúa. Vale para la jerarquía y para conceder roles o excepciones (UR2) | Un DENY en una sucursal no se esquiva concediendo o gestionando en global |
| DU-17 | Cerrar la sesión de otro aplica la jerarquía; con autorización de supervisor, se compara con el supervisor que autorizó (UR3) | Un supervisor no cierra la sesión del Propietario |
| DU-18 | `GET auth/credentials` funciona con la contraseña temporal pendiente (UR4) y `SetUserOverrides` toma el mismo candado cuando el objetivo es Propietario o Administrador (UR5) | La pantalla de contraseña temporal muestra la longitud real; los cambios que cuentan administradores se serializan |

### Nota de versión: roles personalizados antiguos y jerarquía

Los roles **de sistema** reciben los permisos nuevos de cada versión; las **copias personalizadas** (p. ej. un "Gerente" clonado de
Supervisor de caja antes de la versión con báscula) no. Con la jerarquía, quien tiene esa copia ya no puede gestionar a los Supervisores
de caja, que sí tienen los permisos nuevos. El error `IDENTITY.TARGET_OUTRANKS` lista los permisos que faltan (sus descripciones del
catálogo; quien gestiona usuarios ya ve los roles de cada uno), y la pantalla Usuarios agrega la causa y la salida: pedir al
Propietario que agregue esos permisos al rol personalizado.

Sin migraciones: el modelo ya tiene `pos_code`, `pin_hash` y `must_change_password`.

### Pruebas
- Unitarias (dominio): elección del código de caja propio, regla del último Propietario, PINs comunes e iguales al código.
- Integración: PIN propio por primera vez (con contraseña, código único), cambio con PIN actual, `reset-pin` propio rechazado,
  jerarquía (gerente frente al Propietario en todas las acciones), cadena DENY + reset rechazada, quitar DENY como concesión,
  Propietario que degrada a otro, degradación simultánea de dos Propietarios (concurrencia), bloqueo al fallar la contraseña o el
  PIN actuales y cierre de sesiones de caja.
