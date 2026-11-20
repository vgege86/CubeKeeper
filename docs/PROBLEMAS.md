# Si algo falla

Instalación: [TRUENAS.md](TRUENAS.md) · Uso: [USO.md](USO.md).

Lo primero, casi siempre: **Apps → cubekeeper → Logs** en TrueNAS, y la **Consola** del panel si es el servidor de juego.

---

## La app no arranca: CUBEKEEPER_KEY_SECRET

En los registros sale uno de estos mensajes:

- **«falta CUBEKEEPER_KEY_SECRET»** o **«no es el mismo con el que se cifraron»**: pon en la variable el mismo valor
  que tenía (búscalo en tu gestor de contraseñas). La app no arranca a propósito: si siguiera, crearía claves nuevas y
  perderías la sesión de Realms y el token de ntfy sin darte cuenta.
- **«demasiado corto»** o **«tiene el valor de ejemplo»**: genera uno con `openssl rand -base64 32`.

Si el secreto se ha perdido del todo, borra las claves y quita la variable o pon una nueva:

```bash
sudo rm -r /mnt/vms/cubekeeper/keys
```

Después habrá que volver a entrar en el panel, guardar otra vez el token de ntfy (si tenía) y reconectar Realms. Los
mundos, copias, usuarios y jugadores no se pierden.

## No puedo entrar al panel

- La pantalla de login dice por qué no hay usuario (por ejemplo, contraseña demasiado corta). Corrígelo en
  **Apps → cubekeeper → Edit** y reinicia.
- **Contraseña olvidada, cuenta bloqueada o móvil de la verificación en dos pasos perdido:** añade
  `CUBEKEEPER_ADMIN_RESET: "true"` junto a `CUBEKEEPER_ADMIN_PASSWORD` con la nueva, reinicia, entra y quita las dos
  variables. También quita la verificación en dos pasos de ese usuario: vuelve a activarla desde Mi cuenta.
- Otro administrador puede desbloquear a un usuario o quitarle la verificación en dos pasos desde **Usuarios**.
- **403 al abrir el panel:** entras desde una red que no está en `CUBEKEEPER_ALLOWED_NETWORKS` o en la Access List de
  NPM (por ejemplo, sin la VPN).
- **La página carga pero los botones no hacen nada:** falta *Websockets Support* en el proxy de NPM.
- **La contraseña del YAML no funciona:** si lleva `$`, escríbelo doble (`$$`).

## TrueNAS no descarga la imagen

En GitHub → *Packages → cubekeeper → Package settings*, la visibilidad debe ser **Public**.

## El servidor de juego no arranca

- `error while loading shared libraries: libXXX.so`: falta una librería en la imagen. Dime cuál y la añado.
- Puerto ocupado: comprueba que la app de itzg está parada (usan los mismos puertos).
- «Se ha caído varias veces seguidas y ya no se reintenta»: mira la consola; tras arreglarlo, **Arrancar**.

## No puedo conectarme desde el juego

- Dirección `192.168.1.21`, puerto `19132`, en **Servidores → Añadir servidor**.
- **Xbox:** «Partidas LAN» muestra el servidor pero da el error «Boat» al unirse, y la Xbox no deja añadir servidores
  por IP. Ver [Xbox: entrar con BedrockConnect](TRUENAS.md#7b-xbox-entrar-con-bedrockconnect).
- Con el acceso en **Solo aprobados**, el juego dice que no estás invitado: apruébate en **Acceso** (con la inscripción).

## Copias

- **Aviso «Las copias no llegan al segundo destino»** o **Copias → Destinos** dice que `/backups` no está montado:
  comprueba que el dataset `DATOS/backups/cubekeeper` existe y es de 568, y que el pool DATOS está importado. Las
  copias se siguen guardando en `/data/backups` mientras tanto.
- Si Docker creó `/mnt/DATOS/backups/cubekeeper` como root (porque no existía), corrige el propietario:

  ```bash
  sudo chown -R 568:568 /mnt/DATOS/backups/cubekeeper
  ```

## Mapa

- **Código 134 con `GetJpegEncoder` en el registro:** uNmINeD falla al guardar en JPEG. Añade `--imageformat png` al
  comando en los ajustes del mapa (ya viene así por defecto en las instalaciones nuevas).
- **«Permission denied»** al generar: el dataset `vms/cubekeeper` no puede estar montado con `exec=off`.
- **«Unknown command»** o similar: mira en `unmined-cli --help` (o en su web) el comando de tu versión y cámbialo en
  los ajustes del mapa.
- bedrock-viz puede no reconocer bloques muy nuevos.

## Realms

- **«no tiene perfil de Xbox»**, **«condiciones»**, **«familia»**: el mensaje dice qué arreglar en la cuenta de
  Microsoft; después, vuelve a conectar.
- **«Realms ha denegado la descarga»**: solo el dueño del Realm puede descargar sus mundos.
- **«Realms ha rechazado la petición (403/426)» al listar**: Realms puede exigir una versión reciente del juego.
  CubeKeeper manda la versión de BDS instalada; se puede forzar otra con `CUBEKEEPER_REALMS_CLIENT_VERSION` (por
  ejemplo, la de tu Xbox).
- **«puede que esa ranura no tenga ningún mundo»**: esa ranura del Realm está vacía.
- **No conecta con login.live.com o xboxlive.com**: el contenedor necesita salida a Internet por HTTPS. Si AdGuard
  bloquea algún dominio de Xbox o `pocket.realms.minecraft.net`, añádelo a la lista blanca.
- **Aviso «La cuenta de Realms ha dejado de funcionar»**: Microsoft ha caducado o revocado la sesión. Vuelve a
  conectarla en Realms cuando la necesites.

## Avisos al móvil

- **Avisos → Enviar aviso de prueba** dice qué falla (tema vacío, servidor de ntfy inaccesible, token incorrecto).
- Si no llega un tipo concreto, comprueba que está marcado en la lista de Avisos.
