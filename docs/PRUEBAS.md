# Pruebas en el NAS

Comprobaciones de cada función con el servidor real, agrupadas por la fase en que se añadieron. Lo que se puede
probar sin el NAS ya lo prueban las pruebas automáticas (unitarias y de navegador) en cada cambio.

Instalación: [TRUENAS.md](TRUENAS.md) · Uso: [USO.md](USO.md) · Problemas: [PROBLEMAS.md](PROBLEMAS.md).

---

## Seguridad y avisos de mantenimiento

Antes de actualizar, añade `CUBEKEEPER_KEY_SECRET` a la app ([TRUENAS.md](TRUENAS.md#4-generar-el-secreto-de-las-claves)).

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Actualizar la app **sin** `CUBEKEEPER_KEY_SECRET` y entrar | Sigues con la sesión abierta; en Panel sale el aviso «se guardan sin cifrar en /data/keys» |
| 2 | Añadir `CUBEKEEPER_KEY_SECRET` y reiniciar | En los registros, «Cifradas N claves…»; sigues con la sesión abierta, el aviso desaparece, ntfy sigue enviando (Enviar aviso de prueba) y Realms sigue conectada |
| 3 | `sudo grep -c masterKey /mnt/vms/cubekeeper/keys/*.xml` | `0` en todos los archivos |
| 4 | Cambiar el secreto por otro y reiniciar | La app no arranca; en los registros, «no es el mismo con el que se cifraron…». Vuelve a poner el bueno y arranca |
| 5 | En el navegador, herramientas de desarrollo → Red → cualquier página | Cabeceras `Content-Security-Policy`, `X-Frame-Options: SAMEORIGIN`, `X-Content-Type-Options: nosniff` |
| 6 | Recorrer todas las páginas, incluido el mapa | Todo funciona igual; en la consola del navegador no hay errores de «Content Security Policy» |
| 7 | **Avisos** | Aparece «Fallos de mantenimiento» y está marcado |
| 8 | Cambiar temporalmente `CUBEKEEPER_BACKUP_SECONDARY` a `/backups/no-existe`, reiniciar y esperar a la siguiente copia programada (con alguien jugando) | Aviso «Las copias no llegan al segundo destino», una sola vez. Vuelve a poner `/backups` |
| 9 | Cambiar el comando del mapa por uno que falle y esperar a la hora del mapa nocturno | Aviso «Falló el mapa nocturno» |

## Fase 1A: despliegue y servidor

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Abrir `http://192.168.1.21:8765/health` | `{"status":"Healthy",…}` |
| 2 | TrueNAS → Apps → cubekeeper | Estado *Running* y sano (tras unos 2 minutos) |
| 3 | Login con contraseña mala 5 veces | Bloqueo de 15 minutos |
| 4 | Panel → Instalar 1.26.65.1 → Arrancar | Estado *En marcha*, versión en marcha 1.26.65.1 |
| 5 | Consola: `list` | `There are 0/10 players online:` |
| 6 | ROG Ally: Servidores → Añadir `192.168.1.21` : `19132` y entrar | Entras al mundo. La consola muestra `Player connected: …, xuid: …` |
| 7 | Consola: `say Hola` | El mensaje aparece en el chat del juego |
| 8 | Xbox: Servidores → The Hive → Unirse (con la reescritura de AdGuard, [TRUENAS.md](TRUENAS.md#7b-xbox-entrar-con-bedrockconnect)) | ✅ Comprobado: menú de BedrockConnect y, desde ahí, entras a tu servidor |
| 9 | Xbox: «Partidas LAN» | Aparece el servidor (unirse probablemente siga dando «Boat») |
| 10 | Panel → Parar, con alguien dentro | La consola muestra `Quit correctly`; el mundo se guarda |
| 11 | Simular una caída: `sudo pkill -9 bedrock_server` en la shell del NAS | «Se ha cerrado inesperadamente» y, a los 5 s, vuelve a arrancar solo |
| 12 | Reiniciar la app desde TrueNAS con el servidor en marcha | Para BDS limpiamente y, al volver, lo arranca solo |
| 13 | Abrir el panel desde una IP fuera de las permitidas (o sin VPN) | 403 de NPM o de CubeKeeper |

Si **no arranca**: [PROBLEMAS.md](PROBLEMAS.md#el-servidor-de-juego-no-arranca).

## Fase 1B: jugadores y ajustes

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Entrar al servidor desde el Ally y abrir **Jugadores** | Apareces en «Conectados ahora» con tu XUID |
| 2 | **Panel** | En «Jugadores» pone «1 conectado · tu gamertag» |
| 3 | Salir del juego | Desapareces de «Conectados ahora»; en «Últimas sesiones» consta la salida («salió») y la duración |
| 4 | Entrar, y **Parar** el servidor desde el panel | La sesión se cierra como «servidor parado» |
| 5 | Pulsar **Comprobar** en Jugadores con alguien dentro | En la consola aparece `list` y la lista no cambia |
| 6 | **Ajustes** → cambiar «Dificultad» a `hard` → Guardar | Mensaje «Guardado: difficulty» y botón «Reiniciar ahora» |
| 7 | Reiniciar desde ese botón y entrar al juego | La dificultad es Difícil |
| 8 | Ajustes → «Jugadores máximos» = 0 → Guardar | Error en el campo; no se guarda nada |
| 9 | Abrir `/mnt/vms/cubekeeper/server/` en la shell | Hay un `server.properties.AAAAMMDD-HHMMSS.bak` por cada guardado, y los comentarios siguen en `server.properties` |
| 10 | Reiniciar la app de TrueNAS con alguien dentro | Al volver, su sesión anterior está cerrada y empieza una nueva |

Al actualizar, la base de datos se migra sola (se añaden las tablas de jugadores); los usuarios del panel se conservan.

## Fase 1C: copias y actualizaciones

El segundo destino se prepara en la instalación ([TRUENAS.md](TRUENAS.md#1-preparar-los-datasets)).

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Copias** → «Destinos» | `/data/backups` y `/backups (montado)` |
| 2 | Entrar al juego, **Hacer copia ahora** | «Copia creada»; en la consola `save hold`, `save query`… y `Changes to the level are resumed`. Sigues jugando sin cortes |
| 3 | Mirar `/mnt/vms/cubekeeper/backups` y `/mnt/DATOS/backups/cubekeeper` | ✅ Comprobado (tras corregir permisos del dataset): el mismo `.mcworld` en los dos |
| 4 | **Descargar** una copia y abrirla con doble clic en el Ally | Minecraft la importa como mundo |
| 5 | Romper algo en el juego, **Restaurar…** la copia anterior | El servidor se reinicia, el mundo vuelve a como estaba y aparece una copia «Antes de restaurar» |
| 6 | Jugar más de una hora | Aparece una copia «Programada» cada hora; si nadie juega, no se hacen |
| 7 | **Panel** → Versión de BDS | «Automática: No (versión fijada)» |
| 8 | (Opcional) Cambiar `CUBEKEEPER_BDS_VERSION` a otra versión, reiniciar la app y pulsar **Instalar** | Copia «Antes de actualizar», instala y arranca; aparece «Volver a la versión anterior» |
| 9 | (Opcional) **Volver a la versión anterior…** | Copia previa e instala la anterior sin volver a descargarla |

## Fase 2A: acceso y juego

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Entrar desde el Ally con el modo **Abierto** y abrir **Acceso** | Apareces en «Otros jugadores» con tu XUID |
| 2 | **Aprobar** tu usuario y cambiar el permiso a **Operador** | En `allowlist.json` y `permissions.json` aparece tu XUID; en el juego puedes usar comandos |
| 3 | Cambiar a **Solo aprobados** | En la consola, `allowlist on`; en server.properties, `allow-list=true`. Tú sigues dentro |
| 4 | Que alguien no aprobado intente entrar (otra cuenta o un amigo) | El juego le dice que no está invitado. **Mira la consola**: ¿aparece alguna línea con su nombre? Cuéntamelo |
| 5 | **Activar inscripción** 10 min y que vuelva a intentarlo | Entra un instante, le expulsa con «Solicitud enviada…» y aparece en «Solicitudes» |
| 6 | **Aprobar** la solicitud como visitante y que vuelva a entrar | Entra y puede mirar pero no romper bloques |
| 7 | **Bloquear** a ese jugador | Le expulsa al momento y cada vez que intenta volver |
| 8 | **Juego** → Hora «Noche», Clima «Lluvia», Mensaje «hola» | Se ve en el juego |
| 9 | **Juego** → «Leer del servidor» | Los interruptores muestran los valores reales. Si sale un aviso de formato, cuéntamelo |
| 10 | Activar «Conservar el inventario» y morir | No pierdes los objetos |

## Fase 2B: mundos y complementos

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Mundos**: está tu mundo como «activo» con su tamaño | — |
| 2 | **Descargar** el mundo activo con alguien jugando | Baja un `.mcworld` sin cortar la partida; aparece también como copia «Manual» |
| 3 | **Crear un mundo nuevo** (nombre, semilla vacía, Creativo, Pacífico) | Copia del mundo anterior, reinicio y entras en un mundo nuevo en creativo |
| 4 | **Usar este** con el mundo anterior | Vuelves al mundo de antes, tal cual |
| 5 | **Importar** un `.mcworld` (por ejemplo, el descargado en el paso 2, con otro nombre) | Aparece en la lista; con «Usarlo ahora», el servidor arranca con él |
| 6 | **Borrar…** un mundo que no esté activo | Desaparece y queda una copia «Antes de un cambio de mundo» en Copias |
| 7 | **Complementos**: instalar un `.mcpack` de comportamiento (por ejemplo, uno de mcpedl) y **Reiniciar ahora** | Aparece activado y funciona en el juego |
| 8 | Instalar un `.mcaddon` (comportamiento + recursos) | Salen los dos paquetes; para ver texturas, activa «Exigir paquetes de recursos» en Ajustes |
| 9 | **Desactivar** y reiniciar | El complemento deja de funcionar |

Los complementos con scripts pueden necesitar activar funciones experimentales («API beta») en el mundo, y eso se hace
desde Minecraft al crear o editar el mundo, no desde CubeKeeper.

## Fase 3A: familia y auditoría

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Familia** → Editar a un jugador → «Tiempo máximo al día» 30 min, Guardar | En «Ahora» pone cuánto le queda |
| 2 | Que juegue hasta que falten 5 min | Le llega un mensaje privado «Te quedan 5 minutos…» y otro al quedar 1 |
| 3 | Al acabarse | Le expulsa con «Se acabó el tiempo de juego de hoy (30 min). Puedes volver mañana…» y no puede volver a entrar hoy |
| 4 | **+15 min** | Puede volver a entrar 15 minutos más |
| 5 | Añadir una franja «Entre semana 17:00–20:30» y probar fuera de ese horario | Le expulsa con «Ahora no es horario de juego. Puedes volver hoy/mañana a las 17:00» |
| 6 | «Últimos 7 días» | El tiempo jugado de cada día |
| 7 | **Auditoría** | Están tus acciones, los inicios de sesión (con la IP) y las expulsiones de «CubeKeeper» por las normas |

## Fase 3B: usuarios y verificación en dos pasos

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Usuarios** → Añadir «papa» como Operador | Aparece en la lista |
| 2 | Entrar como «papa» (otro navegador o ventana privada) | Solo ve Panel, Jugadores, Familia y Juego; en Panel no puede instalar ni cambiar de versión |
| 3 | Como «papa», abrir `/ajustes` a mano | Página «Sin permiso» |
| 4 | **Mi cuenta** (clic en tu nombre arriba a la derecha) → escanear el QR con Google/Microsoft Authenticator → escribir el código → Activar | Muestra 10 códigos de recuperación: guárdalos |
| 5 | Salir y volver a entrar | Tras la contraseña pide el código de la app |
| 6 | Entrar con un código de recuperación | Funciona una vez; «Te quedan 9» |
| 7 | Como administrador, cambiar el rol de «papa» a Administrador | En menos de un minuto, en su sesión aparecen las secciones de administrador |
| 8 | Intentar quitarte a ti mismo el rol de administrador siendo el único | «Tiene que quedar al menos un administrador» |

Si pierdes el móvil y los códigos de recuperación: otro administrador puede quitarte la verificación en dos pasos desde
Usuarios. Si eres el único administrador, usa `CUBEKEEPER_ADMIN_RESET` ([PROBLEMAS.md](PROBLEMAS.md#no-puedo-entrar-al-panel)): restablece la contraseña, desbloquea
la cuenta y quita la verificación en dos pasos. Después vuelve a activarla desde Mi cuenta.

## Fase 3C: avisos al móvil (ntfy)

1. Instala la app **ntfy** en el móvil.
2. En CubeKeeper, **Avisos** → pulsa «Generar» para crear un tema difícil de adivinar, escribe la dirección del panel
   (`https://cubekeeper.alucard86.fyi`) y **Guardar**. No hace falta tocar la app de TrueNAS.
3. En la app ntfy, suscríbete al tema que aparece debajo.

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Avisos** → Enviar aviso de prueba | Llega «CubeKeeper: prueba» al móvil; al tocarlo se abre el panel |
| 2 | Activar la inscripción y que alguien no aprobado intente entrar | Aviso «Solicitud de acceso: …» |
| 3 | `sudo pkill -9 bedrock_server` en el NAS | Aviso «El servidor se ha caído» |
| 4 | Esperar a que Mojang publique una versión más nueva que la fijada (o comprobarlo a las 6 h de arrancar) | Aviso «BDS x.y.z disponible», una sola vez |
| 5 | Activar «Entradas y salidas» y entrar al juego | Aviso «… ha entrado» |

## Fase 3D: mapa del mundo

1. Descarga **uNmINeD CLI para Linux x64** desde su web oficial (unmined.net → Downloads), un `.tar.gz`. Es gratuito
   para uso personal; por su licencia no puede ir dentro de la imagen.
2. **Mapa** → uNmINeD → «Subir e instalar» con ese archivo (o pega el enlace de descarga y «Descargar»).
3. **Generar ahora**. La primera vez puede tardar varios minutos; mientras, puedes jugar.

Instala los motores solo desde su web oficial: es un programa que se ejecuta dentro del contenedor.

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Generar ahora | ✅ Comprobado con uNmINeD y `--imageformat png`: en el registro se ve la copia, el comando y la salida del programa; al terminar aparece el mapa |
| 2 | Si falla con «Unknown command» o similar | Mira en `unmined-cli --help` (o en su web) el comando de tu versión y cámbialo en Ajustes del mapa |
| 3 | Entrar como operador | Ve el mapa, pero no los botones de generar ni instalar |
| 4 | Al día siguiente | «Última generación» es de esta madrugada (a la hora configurada) |
| 5 | (Opcional) bedrock-viz: subir su ejecutable de Linux, elegirlo, Guardar y Generar | Mapa con bedrock-viz; puede no reconocer bloques muy nuevos de 1.26 |

Si falla: [PROBLEMAS.md](PROBLEMAS.md#mapa).

## Realms

1. **Realms** → **Conectar cuenta**. Aparece un código de 8 letras.
2. En el móvil o el ordenador, abre `microsoft.com/link`, escribe el código e inicia sesión con la **cuenta dueña del
   Realm** (la de Xbox). Acepta el permiso. CubeKeeper nunca ve la contraseña.
3. En unos segundos la página dice «Conectada como …» y lista tus Realms.
4. Elige la ranura (mundo) del Realm → **Importar…** → nombre (opcional) → **Descargar e importar**. Marca «Usarlo
   ahora» si quieres jugarlo ya: antes se hace una copia del mundo activo.

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Conectar cuenta y usar el código | «Conectada como» con tu gamertag y la lista de Realms |
| 2 | Importar una ranura sin «Usarlo ahora» | Aparece en **Mundos**; el servidor sigue con el mundo que tenía |
| 3 | En Mundos, «Usar este» con el importado y entrar desde la Xbox | ✅ Comprobado: es el mundo del Realm |
| 4 | Reiniciar la app y volver a Realms → Actualizar lista | Sigue conectada sin pedir código (renueva la sesión sola) |
| 5 | Desconectar… | Vuelve a «Conectar cuenta»; en Auditoría quedan conectar, importar y desconectar |

Si algo falla: [PROBLEMAS.md](PROBLEMAS.md#realms).

Los complementos del Marketplace que tenga el mundo del Realm no se pueden copiar al servidor: el mundo se abre, pero
sin ellos.
