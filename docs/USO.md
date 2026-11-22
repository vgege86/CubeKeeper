# Uso diario de CubeKeeper

Qué hace cada página del panel y cómo se usa. Instalación: [TRUENAS.md](TRUENAS.md) · Problemas:
[PROBLEMAS.md](PROBLEMAS.md).

En el móvil, el menú se pliega tras el botón **Menú** de arriba. Desde `https://cubekeeper.alucard86.fyi` se puede
instalar como app: en Chrome, **⋮ → Instalar aplicación** (o «Añadir a pantalla de inicio»); en el iPhone, Safari →
**Compartir → Añadir a pantalla de inicio**. Se abre sin la barra del navegador. (Hace falta entrar por https: por
`http://192.168.1.21:8765` solo se puede añadir como acceso directo.)

Hay dos tipos de usuario del panel:

- **Administrador:** todo.
- **Operador:** Panel, Jugadores, Familia, Juego y Mapa. Puede arrancar y parar el servidor, pero no cambiar ajustes,
  versiones, mundos, accesos ni usuarios.

---

## Panel

Estado del servidor (arrancar, parar, reiniciar), quién está jugando, versión de BDS y cómo conectarse (en PC con
la dirección y el puerto; en la Xbox, desde Amigos con MCXboxBroadcast
([XBOX.md](XBOX.md#opción-1-amigos-con-mcxboxbroadcast-recomendada)) o con BedrockConnect
([XBOX.md](XBOX.md#opción-2-destacados-con-bedrockconnect))).

### Recursos

Memoria y CPU que usa el servidor, con gráficas de la última hora, 6 h o 24 h (se miden cada 30 s y se pierden al
reiniciar la app). La memoria del contenedor se compara con su límite (4 GB en el YAML). Si pasa del 90 % un rato,
llega un aviso: reinicia el servidor, activa el reinicio programado o sube el límite de la app en TrueNAS. Si lo
alcanzara, el sistema cerraría el servidor de golpe.

### Reinicio programado

Reinicia el servidor cada día a la hora elegida (desactivado hasta que lo actives). Avisa por el chat a los minutos que
pongas (por defecto 5 y 1) y, si marcas «No reiniciar si hay alguien jugando», ese día se lo salta. Tampoco reinicia si
el servidor está parado o hay una copia o actualización en marcha. Elige una hora distinta de la del mapa nocturno
(04:15) y de la comprobación de versiones (05:00): por ejemplo 05:30.

### Versión de BDS

Con la versión fijada (al principio, la de `CUBEKEEPER_BDS_VERSION`), CubeKeeper instala exactamente esa y no
actualiza solo. Cuando Mojang publica una más nueva, te llega un aviso al móvil y en el Panel aparece el botón
**Instalar x.y.z**: hace una copia del mundo, instala, comprueba que arranca y, si no, vuelve sola a la anterior. Si
va bien, esa versión queda fijada desde el panel; no hace falta tocar la app de TrueNAS. **Volver a la versión
anterior** reinstala la de antes sin descargarla (y también queda fijada).

El Panel dice si la versión fijada viene de la variable o del panel. Si cambias la variable a otra distinta de la
fijada desde el panel, manda la del panel hasta que pulses **Usar la de la variable**.

Antes de instalar una versión nueva, mira que la Xbox ya tenga esa versión del juego: si el servidor va por delante,
la Xbox no puede entrar (error «Terracotta» o «versión desactualizada»). El mismo día que se actualice la Xbox con un
protocolo nuevo, actualiza también MCXboxBroadcast o BedrockConnect si los usas
([XBOX.md](XBOX.md#opción-1-amigos-con-mcxboxbroadcast-recomendada)).

Sin versión fijada, la comprueba cada día a las 05:00 y, si hay una nueva, avisa por el chat a los 5 y 1 minutos y
actualiza.

Las versiones nuevas de BDS suelen traer arreglos de seguridad: conviene no quedarse muy atrás.

## Jugadores

Quién está conectado (con su XUID), las últimas sesiones y el tiempo jugado. **Comprobar** pregunta al servidor la
lista real por si algo se ha desincronizado.

**Tiempo de juego:** gráfica de columnas por día de los últimos 7, 14 o 30 días, con un color por jugador (siempre el
mismo), y una tabla con el total, los días jugados y la media por día jugado. «Ver como tabla» da los mismos datos
día a día. Pasando el ratón por una columna se ve el detalle.

## Acceso

- **Abierto:** entra cualquiera con cuenta de Xbox.
- **Solo aprobados:** solo los de la lista. Para añadir a alguien, activa la **inscripción** unos minutos y que intente
  entrar: le expulsa con «Solicitud enviada» y aparece en Solicitudes para aprobarlo como visitante, miembro u operador.
- **Bloquear** expulsa al jugador cada vez que entra.
- **Desde el aviso del móvil:** si en Avisos está puesta la dirección del panel, el aviso de solicitud trae los botones
  **Aprobar (visitante)** y **Rechazar**. Valen 30 minutos y una sola vez. El móvil tiene que estar en casa o en la VPN
  (es él quien llama al panel). Si aprobaste como visitante y quieres darle más permisos, cámbialo en Acceso.

Los jugadores se identifican por su XUID, no por el nombre: cambiar de gamertag no les quita el acceso.

## Familia

Por jugador: franjas de horario permitidas y tiempo máximo al día. Avisa por el chat a los 5 y 1 minutos y luego
expulsa con el motivo. **+15 min** da tiempo extra solo para hoy. «Últimos 7 días» muestra cuánto ha jugado.

## Juego

Hora, clima, mensaje a todos, expulsar a alguien y reglas del juego (conservar inventario, ciclo de día, etc.). Los
cambios se aplican al momento, sin reiniciar.

## Mundos

Crear, cambiar, importar (`.mcworld`), descargar y borrar mundos. Cambiar o borrar hace antes una copia del mundo
activo.

## Realms

Para traer un mundo de un Realm:

1. **Conectar cuenta**: aparece un código. Ábrelo en `microsoft.com/link` e inicia sesión con la cuenta dueña del Realm.
   CubeKeeper nunca ve la contraseña.
2. Elige el Realm y la ranura (salen siempre las tres) → **Importar…** → **Descargar e importar**.

La sesión se guarda cifrada y se renueva sola cada día; si deja de funcionar, llega un aviso. **Desconectar** la borra.

Usa la API de Realms del propio juego, que no es pública: si Mojang la cambia, puede dejar de funcionar hasta que se
actualice CubeKeeper.

## Complementos

Instalar `.mcpack` y `.mcaddon` (comportamiento y recursos), activarlos y desactivarlos. Hace falta reiniciar el
servidor para que se apliquen. Los del Marketplace no se pueden copiar.

## Ajustes

Editor de `server.properties` con explicación de cada opción y validación. Guarda una copia del archivo antes de cada
cambio. Si algún ajuste se aparta de la configuración probada (puertos, NetherNet, visibilidad en LAN…), el panel lo
avisa y deja aplicar la recomendada con un botón.

## Copias

Copias del mundo sin parar la partida: cada hora si alguien ha jugado (24 horarias, 14 diarias y 8 semanales), antes de
cada cambio importante y a mano con **Hacer copia ahora**. Se guardan en `/data/backups` y en el segundo destino.
**Restaurar…** vuelve a una copia (haciendo antes otra de seguridad). **Descargar** baja el `.mcworld`.

- **Comprobación:** cada semana se abre una copia al azar para ver que se podría restaurar (que tiene el mundo completo
  y ningún archivo dañado). Si alguna está mal, llega un aviso. **Comprobar una ahora** lo hace en el momento.
- **Espacio en disco:** cuánto queda libre en `/data` y en el segundo destino, y cuánto ocupa cada cosa (copias,
  mundos, versiones, mapa…). Si queda menos del 10 % o de 5 GB, llega un aviso al móvil y sale en el Panel
  (configurable con `CUBEKEEPER_DISK_WARN_PERCENT` y `CUBEKEEPER_DISK_WARN_GB`).

## Mapa

Mapa del mundo con uNmINeD o bedrock-viz, que se instalan desde la propia página (desde su web oficial). Se genera
cada noche y con **Generar ahora**. Los operadores lo ven, pero no lo configuran.

## Consola

La salida del servidor en vivo y una barra para enviar comandos de BDS (`list`, `say`, `kick`…).

**Descargar diagnóstico** baja un `.zip` con el estado, las versiones, la configuración (sin contraseñas, tokens ni
secretos), el espacio en disco, la lista de copias, el registro reciente de CubeKeeper, la consola reciente,
`server.properties` y el último registro del mapa. Es lo que hay que pasar cuando algo falla, en lugar de buscar
registros en la shell.

## Avisos al móvil

Con la app **ntfy**:

1. **Avisos** → «Generar» un tema difícil de adivinar, escribe la dirección del panel y **Guardar**.
2. En la app ntfy, suscríbete a ese tema.
3. (Opcional) Si tienes BedrockConnect en casa, escribe su versión en la tarjeta **BedrockConnect**: te avisará cuando
   salga otra, junto con las versiones nuevas de BDS.
4. Elige qué avisos quieres: solicitudes de acceso, caídas, copias fallidas, actualizaciones, versiones nuevas de BDS,
   fallos de mantenimiento (mapa nocturno, segundo destino de copias, cuenta de Realms) y entradas y salidas.

## Auditoría

Quién hizo qué y cuándo: inicios de sesión (con IP), cambios de ajustes, accesos, mundos, copias, expulsiones por las
normas de familia, etc.

## Usuarios y Mi cuenta

**Usuarios** (administradores): crear usuarios, cambiar su rol o contraseña, desbloquearlos y quitarles la verificación
en dos pasos.

**Mi cuenta** (clic en tu nombre): cambiar tu contraseña, activar la verificación en dos pasos con una app de
autenticación (Google o Microsoft Authenticator; guarda los códigos de recuperación) y **Cerrar las demás sesiones**
si entraste desde un equipo que no es tuyo o perdiste el móvil. Un administrador puede hacer lo mismo con otro usuario
desde Usuarios → **Cerrar sesiones**. Dejan de valer en menos de un minuto.
