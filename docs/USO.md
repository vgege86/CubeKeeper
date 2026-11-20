# Uso diario de CubeKeeper

Qué hace cada página del panel y cómo se usa. Instalación: [TRUENAS.md](TRUENAS.md) · Problemas:
[PROBLEMAS.md](PROBLEMAS.md).

Hay dos tipos de usuario del panel:

- **Administrador:** todo.
- **Operador:** Panel, Jugadores, Familia, Juego y Mapa. Puede arrancar y parar el servidor, pero no cambiar ajustes,
  versiones, mundos, accesos ni usuarios.

---

## Panel

Estado del servidor (arrancar, parar, reiniciar), quién está jugando, versión de BDS y cómo conectarse (en PC con
la dirección y el puerto; en la Xbox, con BedrockConnect: [TRUENAS.md](TRUENAS.md#7b-xbox-entrar-con-bedrockconnect)).

### Versión de BDS

Con `CUBEKEEPER_BDS_VERSION` fijada, CubeKeeper instala exactamente esa versión y no actualiza solo; te avisa si Mojang
publica una más nueva. Para cambiar: pon la nueva en la variable, reinicia la app y pulsa **Instalar**. Antes se hace
una copia del mundo, y si la nueva no arranca vuelve sola a la anterior. **Volver a la versión anterior** reinstala la
de antes sin descargarla.

Sin versión fijada, la comprueba cada día a las 05:00 y, si hay una nueva, avisa por el chat a los 5 y 1 minutos y
actualiza.

Las versiones nuevas de BDS suelen traer arreglos de seguridad: conviene no quedarse muy atrás.

## Jugadores

Quién está conectado (con su XUID), las últimas sesiones y el tiempo jugado. **Comprobar** pregunta al servidor la
lista real por si algo se ha desincronizado.

## Acceso

- **Abierto:** entra cualquiera con cuenta de Xbox.
- **Solo aprobados:** solo los de la lista. Para añadir a alguien, activa la **inscripción** unos minutos y que intente
  entrar: le expulsa con «Solicitud enviada» y aparece en Solicitudes para aprobarlo como visitante, miembro u operador.
- **Bloquear** expulsa al jugador cada vez que entra.

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

## Mapa

Mapa del mundo con uNmINeD o bedrock-viz, que se instalan desde la propia página (desde su web oficial). Se genera
cada noche y con **Generar ahora**. Los operadores lo ven, pero no lo configuran.

## Consola

La salida del servidor en vivo y una barra para enviar comandos de BDS (`list`, `say`, `kick`…).

## Avisos al móvil

Con la app **ntfy**:

1. **Avisos** → «Generar» un tema difícil de adivinar, escribe la dirección del panel y **Guardar**.
2. En la app ntfy, suscríbete a ese tema.
3. Elige qué avisos quieres: solicitudes de acceso, caídas, copias fallidas, actualizaciones, versiones nuevas de BDS,
   fallos de mantenimiento (mapa nocturno, segundo destino de copias, cuenta de Realms) y entradas y salidas.

## Auditoría

Quién hizo qué y cuándo: inicios de sesión (con IP), cambios de ajustes, accesos, mundos, copias, expulsiones por las
normas de familia, etc.

## Usuarios y Mi cuenta

**Usuarios** (administradores): crear usuarios, cambiar su rol o contraseña, desbloquearlos y quitarles la verificación
en dos pasos.

**Mi cuenta** (clic en tu nombre): cambiar tu contraseña y activar la verificación en dos pasos con una app de
autenticación (Google o Microsoft Authenticator). Guarda los códigos de recuperación.
