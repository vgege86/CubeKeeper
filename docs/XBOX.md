# Xbox: cómo entrar a CubeKeeper

La Xbox (y también PlayStation y Switch) no deja añadir servidores por dirección IP, y unirse desde «Partidas LAN»
falla (error «Boat»: con NetherNet, la consola no manda su identidad por LAN y el servidor, con `online-mode=true`, la
rechaza). En PC (también ROG Ally), móvil y tablet no hace falta nada de esto: **Servidores → Añadir servidor**,
dirección `192.168.1.21`, puerto `19132`.

Hay dos formas de que la Xbox llegue a tu servidor. Las dos son apps aparte en TrueNAS; ninguna sustituye a
CubeKeeper ni toca el mundo: solo sirven para que la consola encuentre el servidor. La partida va siempre de la Xbox
al NAS directamente, cada jugador entra con su cuenta y el Acceso, la Familia y las estadísticas funcionan igual.

Instalación de CubeKeeper: [TRUENAS.md](TRUENAS.md) · Uso: [USO.md](USO.md) · Problemas: [PROBLEMAS.md](PROBLEMAS.md).

## Cuál elegir

| | Opción 1: MCXboxBroadcast (Amigos) | Opción 2: BedrockConnect (destacados) |
|---|---|---|
| Estado (noviembre de 2026) | ✅ **Funciona** con la Xbox en 26.62 (comprobado en el NAS) | ❌ Dejó de funcionar con la 26.62: la Xbox entra en el servidor real |
| Cómo se entra | **Jugar → Amigos** → «CubeKeeper» | **Servidores** → un destacado (The Hive…) → menú → tu servidor |
| Qué necesita | Una cuenta de Microsoft **secundaria** que los jugadores agregan como amiga | Reglas en AdGuard que desvían los destacados; BedrockConnect público o en casa |
| Afecta a la red de casa | No | Sí: mientras las reglas estén activas, esos destacados no se pueden jugar desde casa |
| Fuera de casa (Xbox) | Le llega por Amigos esté donde esté (la partida tiene que poder llegar al NAS, ver abajo) | No: solo con el DNS de casa |
| Al actualizarse el juego | Actualizar la app (Update) | Actualizar la imagen de BedrockConnect (si es el de casa) y esperar que Mojang no cambie los destacados |
| Riesgo | El proyecto avisa de que la cuenta secundaria podría ser sancionada (imita a un cliente del juego) | Depende de un truco de DNS que Mojang puede romper (ya lo ha hecho) |

**Recomendada: la opción 1.** BedrockConnect queda documentado por si vuelve a funcionar o para otras consolas.

En las dos, el juego de la consola tiene que tener **la misma versión** que el servidor (en el Panel se ve la del
servidor). Si no, sale «Terracotta» o «versión desactualizada»: actualiza Minecraft en la Xbox
(**Mis juegos y aplicaciones → Administrar → Actualizaciones**) antes de instalar una versión nueva de BDS.

---

## Opción 1: Amigos con MCXboxBroadcast (recomendada)

[MCXboxBroadcast](https://github.com/MCXboxBroadcast/Broadcaster) es una app pequeña que inicia sesión con una
cuenta de Microsoft **secundaria** y se queda siempre «jugando» en CubeKeeper. Quien tenga esa cuenta como amiga ve
el servidor en la pestaña **Amigos** del juego y entra desde ahí, como a la partida de un amigo. Sin DNS, sin
AdGuard y sin BedrockConnect. No sustituye a CubeKeeper: solo anuncia el servidor.

Cómo funciona: la Xbox pide unirse a través de Xbox Live, MCXboxBroadcast pasa esa petición a CubeKeeper
(TCP `19132`) y la partida va directamente de la Xbox al NAS. Cada jugador entra con su propia cuenta, así que el
Acceso, la Familia y las estadísticas funcionan igual. Con el acceso en **Abierto**, cualquiera que agregue la
cuenta podría entrar: mejor **Solo aprobados**.

Hace falta que el juego de la Xbox tenga la **misma versión** que el servidor (en el Panel se ve la del servidor).

1. **Cuenta secundaria.** Crea una cuenta de Microsoft nueva (gratis, en `signup.live.com`) y entra una vez en
   `xbox.com` para que tenga gamertag. No uses la tuya ni la de los niños: el proyecto avisa de que la cuenta
   podría ser sancionada porque imita a un cliente del juego. Si la cuenta de los niños es infantil, en
   `family.microsoft.com` tiene que poder agregar amigos y unirse a partidas multijugador.
2. **Dataset** `vms/mcxboxbroadcast` con
   [`deploy/mcxboxbroadcast/config.yml`](../deploy/mcxboxbroadcast/config.yml) dentro. Ya trae la IP del NAS y el
   puerto; el nombre que sale en Amigos se cambia en `host-name` y `world-name`. Desde la shell:

   ```bash
   sudo mkdir -p /mnt/vms/mcxboxbroadcast
   ```

   ```bash
   sudo curl -fsSL -o /mnt/vms/mcxboxbroadcast/config.yml https://raw.githubusercontent.com/vgege86/CubeKeeper/main/deploy/mcxboxbroadcast/config.yml
   ```

   La carpeta tiene que ser del usuario `app` de la imagen (1001), no de 568:

   ```bash
   sudo chown -R 1001:1001 /mnt/vms/mcxboxbroadcast
   ```

3. **Apps → Discover Apps → ⋮ → Install via YAML**, nombre `mcxboxbroadcast`, con
   [`deploy/mcxboxbroadcast/docker-compose.yml`](../deploy/mcxboxbroadcast/docker-compose.yml). No hay nada que
   cambiar.
4. **Iniciar sesión.** En el registro de la app sale un código:

   ```bash
   sudo docker logs -f ix-mcxboxbroadcast-mcxboxbroadcast-1
   ```

   `To sign in, use a web browser to open the page https://www.microsoft.com/link and enter the code XXXXXXXX`.
   Ábrelo en el móvil e inicia sesión con la **cuenta secundaria**. La sesión se guarda en el dataset (carpeta
   `cache`): no hay que repetirlo al reiniciar.
5. **Agregar como amigo.** En la Xbox (o en la app de Xbox del móvil), con la cuenta de cada jugador, busca el
   gamertag de la cuenta secundaria y agrégalo. La app acepta sola las solicitudes y le manda una invitación.
6. **Jugar:** Minecraft → **Jugar → Amigos** → «CubeKeeper» en *Partidas de amigos*. Si es la primera vez y el
   acceso está en **Solo aprobados**, apruébalo en **Acceso** como siempre.

Con esto ya no hacen falta las reglas de AdGuard ni BedrockConnect (puedes desactivarlas con `!` o borrarlas).

### Otros dispositivos

En PC (también ROG Ally y otras portátiles con Windows), móvil y tablet se puede añadir el
servidor por IP (`192.168.1.21`, puerto `19132`) y no hace falta nada de esto; pero si la cuenta del jugador tiene
la secundaria como amiga, también lo verán en Amigos. PlayStation y Switch funcionan como la Xbox.

### Desde fuera de casa

- **PC, ROG Ally, móvil o tablet:** con la VPN de WireGuard conectada, igual que en casa (`192.168.1.21:19132`).
  Es lo recomendado: no se abre nada en el router.
- **Xbox fuera de casa** (por ejemplo, en casa de un amigo): no puede usar WireGuard. La petición le llega igual por
  Amigos, pero la partida tiene que llegar al NAS desde Internet. Sin tocar el router puede funcionar o no, según
  la red de los dos lados (sin probar). Si hace falta, se abriría en el router solo el puerto UDP de la partida
  (`server-udp-ports`, 19140) hacia `192.168.1.21`, con el acceso en **Solo aprobados**. El panel nunca se abre a
  Internet. Pruébalo antes en casa y, si lo necesitas, lo vemos.

### Si no funciona

- **No aparece en Amigos:** en el registro de la app tiene que salir que la sesión se ha creado y actualizado. Si
  el servidor está parado, desaparece (se puede dejar visible con `config-fallback: true`). Comprueba que el
  jugador y la cuenta secundaria son amigos en los dos sentidos.
- **Aparece pero da error al unirse:** mira el número del error. Si el juego dice que la versión no coincide,
  actualiza la Xbox o instala en el Panel la que tenga la Xbox. Si es *InitialConnection-81* o *-84*,
  MCXboxBroadcast no llega a CubeKeeper: revisa `remote-address` (`192.168.1.21`) y que el servidor esté en marcha.
- **Tras actualizarse el juego deja de funcionar:** **Apps → mcxboxbroadcast → Update** (o Edit → Save) para bajar
  la última imagen. El proyecto publica soporte para cada versión nueva en pocos días.
- **Se reinicia sin parar con «Unable to access jarfile»:** el YAML lleva `user: "568:568"`; quítalo (la imagen
  tiene que ir con su usuario `app`, 1001).
- **Se reinicia sin parar con «Permission denied»** o no guarda la sesión: la carpeta no es de 1001
  (`sudo chown -R 1001:1001 /mnt/vms/mcxboxbroadcast`).
- **Pide iniciar sesión otra vez:** Microsoft caducó la sesión; repite el paso 4.

---

## Opción 2: destacados con BedrockConnect

[BedrockConnect](https://github.com/Pugmatt/BedrockConnect) es un servidor que solo muestra un menú. Con reglas en
AdGuard se desvían los servidores destacados del juego hacia él: al pulsar uno, en lugar del servidor real sale el
menú, y desde ahí se entra al tuyo. Se puede usar el público o montarlo en casa.

Se desvían **todos** los destacados que consulta Minecraft (salen en el registro de AdGuard al abrir la pestaña
Servidores). Así, si uno deja de funcionar, se prueba con otro sin tocar nada:

| Servidor destacado | Dominio |
|---|---|
| The Hive | `nethernet.playhive.com` y `hivebedrock.network` (con `||` cubre `geo.`, `fr.`, `sg.`…) |
| Lifeboat | `mco.lbsg.net` |
| Mineville / InPvP | `play.inpvp.net` |
| Galaxite | `play.galaxite.net` |
| Enchanted | `play.enchanted.gg` |
| MegaSMP | `play.megasmp.gg` |
| CubeCraft | `mco.cubecraft.net` (BedrockConnect no lo da como compatible) |

> **Noviembre de 2026: con la Xbox en 26.62 ha dejado de funcionar con todos.** AdGuard marca los dominios como
> «Reescrito», pero la Xbox entra en el servidor real y en el registro de BedrockConnect no aparece nada: el juego
> ya no usa esa respuesta para conectarse ([issue #712](https://github.com/Pugmatt/BedrockConnect/issues/712)).
> Antes funcionaba con The Hive (26.60). Si te pasa, usa [MCXboxBroadcast](#opción-1-amigos-con-mcxboxbroadcast-recomendada).

1. En **AdGuard → Filtros → Reglas de filtrado personalizadas**, pega este bloque y guarda:

   ```
   ! CubeKeeper: destacados de Minecraft → BedrockConnect (menú para entrar al servidor de casa desde la Xbox)
   ||hivebedrock.network^$dnsrewrite=104.238.130.180
   ||nethernet.playhive.com^$dnsrewrite=104.238.130.180
   ||mco.lbsg.net^$dnsrewrite=104.238.130.180
   ||play.inpvp.net^$dnsrewrite=104.238.130.180
   ||play.galaxite.net^$dnsrewrite=104.238.130.180
   ||play.enchanted.gg^$dnsrewrite=104.238.130.180
   ||play.megasmp.gg^$dnsrewrite=104.238.130.180
   ||mco.cubecraft.net^$dnsrewrite=104.238.130.180
   ```

   `104.238.130.180` es el BedrockConnect público, que mantiene el autor del proyecto (es la dirección que da su
   [README](https://github.com/Pugmatt/BedrockConnect) para Xbox). Si montas el tuyo en casa (abajo), cambia esa IP
   en todas las líneas. Las reglas van todas juntas, en un solo sitio. Si ya tenías reescrituras en
   **Filtros → Reescrituras DNS** para estos dominios, bórralas para que no haya dos.
2. En la Xbox, DNS en **automático** (para que use AdGuard). Si cambiaste algo, desenchúfala de la corriente 30
   segundos: la consola recuerda las direcciones y no hay forma de borrarlas.
3. **Servidores → The Hive → Unirse.** Sale el menú de BedrockConnect: **Connect to a Server**, dirección
   `192.168.1.21`, puerto `19132`, marca **Add to server list** y **Submit**.
4. Las siguientes veces: The Hive → Unirse → elige tu servidor en la lista. Si The Hive deja de llevar al menú,
   prueba con los otros de la tabla (en el BedrockConnect público, tu lista guardada es la misma entres por donde
   entres).

Mientras estas reglas estén activas, esos servidores destacados no se pueden jugar desde casa (llevan siempre al
menú). Para jugar en uno, pon `!` delante de su línea para desactivarla. El servidor de BedrockConnect es de terceros:
solo hace de menú; la partida va directamente de la Xbox a tu NAS.

Si al unirte entras en el servidor real: en el registro de consultas de AdGuard, filtra por la Xbox y comprueba que
su dominio sale como «Reescrito». Si no aparece, la Xbox pregunta a otro DNS (por ejemplo, el de IPv6 del operador);
si aparece reescrito, desenchufa la Xbox 30 segundos, vuelve a cargar la lista de Servidores o prueba con otro de la
tabla. Si Mojang o los servidores cambian de nombre, en ese mismo registro se ve qué dominios consulta Minecraft al
abrir la pestaña Servidores: añade el nuevo con la misma forma.

### BedrockConnect en casa (opcional)

Para no depender del servidor público: BedrockConnect como app aparte en el NAS, con su propia IP en la red de casa
(macvlan), porque el puerto 19132 del NAS ya lo usa el servidor de juego. No hace falta otra tarjeta de red: va sobre
la misma que tiene la `192.168.1.21`.

Necesitas: una IP libre de la red que el router no reparta por DHCP (por ejemplo `192.168.1.22`), el nombre de la
tarjeta del NAS que tiene la `192.168.1.21` (**Network → Interfaces**: `enp…`, `eno1`; si está dentro de un puente
como `br0`, usa el puente) y la puerta de enlace (normalmente `192.168.1.1`).

1. **Dataset** `vms/bedrockconnect` (preset Apps) con una carpeta `players` que sea **de root**: el contenedor
   funciona como root pero sin capacidades, así que no puede escribir en carpetas de otro usuario.

   ```bash
   sudo mkdir -p /mnt/vms/bedrockconnect/players
   ```

   ```bash
   sudo chown -R root:root /mnt/vms/bedrockconnect/players
   ```

2. Copia [`deploy/bedrockconnect/custom_servers.json`](../deploy/bedrockconnect/custom_servers.json) a
   `/mnt/vms/bedrockconnect/custom_servers.json`. Hace que «CubeKeeper» salga en el menú para todos, sin escribir la
   dirección.
3. **Apps → Discover Apps → ⋮ → Install via YAML**, nombre `bedrockconnect`, con
   [`deploy/bedrockconnect/docker-compose.yml`](../deploy/bedrockconnect/docker-compose.yml). Cambia las tres líneas
   marcadas con `CAMBIA` (IP, tarjeta y puerta de enlace).
4. En AdGuard, en las reglas personalizadas del paso anterior, cambia `104.238.130.180` por la IP que le hayas dado
   (en todas las líneas).
5. Xbox: **Servidores → The Hive → Unirse** → «CubeKeeper».

Las listas de servidores que guardó la Xbox en el BedrockConnect público no pasan al tuyo (cada instancia tiene las
suyas), pero con `custom_servers.json` el tuyo ya sale sin guardarlo.

**Cuando se actualice el juego.** Si la Xbox se actualiza a una versión con protocolo nuevo, al entrar en The Hive
dirá que el servidor está desactualizado o no conectará. Mira la última versión en
[Docker Hub](https://hub.docker.com/r/pugmatt/bedrock-connect/tags), cámbiala en la línea `image:` de la app
(**Apps → bedrockconnect → Edit**) y guarda. Las listas de los jugadores se conservan (están en el dataset). Ese
mismo día suele hacer falta también la versión nueva de BDS: CubeKeeper avisa al móvil, y se cambia con
`CUBEKEEPER_BDS_VERSION` y **Instalar**.

**Si no funciona (BedrockConnect en casa):**

- **La Xbox entra en el menú y la echa enseguida;** en el registro de la app (`sudo docker logs --tail 100
  ix-bedrockconnect-bedrockconnect-1`) sale `An error occurred saving to player file` y `Permission denied`: la
  carpeta `players` no es de root. `sudo chown -R root:root /mnt/vms/bedrockconnect/players` (no hace falta
  reiniciar).
- **Se para y arranca sola sin parar:** el YAML de la guía ya no lleva `user: "568:568"` ni el `/tmp` en memoria
  (`tmpfs`), que la hacían cerrarse al arrancar. Si los añadiste, quítalos.
- **TrueNAS rechaza la red** `macvlan`: comprueba el nombre de la tarjeta y que la IP no esté en uso.
- **La Xbox no llega a BedrockConnect** (en el registro de la app no aparece ninguna línea al intentar entrar):
  comprueba en el registro de consultas de AdGuard que el destacado se reescribe a la IP de tu BedrockConnect, y
  desenchufa la Xbox 30 segundos. Para ver si esa IP responde, abre `http://IP:19132/` desde el móvil en el wifi de
  casa: un error «404» quiere decir que sí llega.
- **Sale el menú pero no conecta a CubeKeeper**: la Xbox se conecta al NAS directamente, como con el público; revisa
  que en `custom_servers.json` esté `192.168.1.21` y el puerto `19132`.
- Desde el propio NAS no se puede hacer ping a esa IP (es normal con macvlan); desde otro equipo de casa sí.
