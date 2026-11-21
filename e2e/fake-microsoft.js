// Imita login.live.com, Xbox Live y Realms lo justo para probar la página de Realms.
const http = require('http');

let polls = 0;

// .mcworld mínimo (zip sin comprimir) con level.dat y levelname.txt.
function zip(files) {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc32 = buf => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const locals = [], centrals = [];
  let offset = 0;
  for (const [name, text] of Object.entries(files)) {
    const data = Buffer.from(text), fname = Buffer.from(name), crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(fname.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20); central.writeUInt32LE(data.length, 24); central.writeUInt16LE(fname.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, fname, data); centrals.push(central, fname);
    offset += 30 + fname.length + data.length;
  }
  const cd = Buffer.concat(centrals), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10);
  end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}

const world = zip({ 'level.dat': 'x', 'levelname.txt': 'Casa Realm', 'db/CURRENT': 'y' });
const port = Number(process.env.FAKE_MICROSOFT_PORT || 5300);

http.createServer((req, res) => {
  const send = (obj, code = 200, raw) => {
    const data = raw ?? Buffer.from(JSON.stringify(obj));
    res.writeHead(code, { 'Content-Length': data.length, 'Content-Type': raw ? 'application/octet-stream' : 'application/json' });
    res.end(data);
  };
  req.resume();
  req.on('end', () => {
    const path = req.url.split('?')[0];
    if (req.method === 'GET' && path === '/health') return send({ ok: true });
    if (req.method === 'POST') {
      if (path === '/connect') { polls = 0; return send({ user_code: 'K7PQ2XWM', device_code: 'dc', verification_uri: 'https://www.microsoft.com/link', interval: 1, expires_in: 900 }); }
      if (path === '/token') return ++polls < 3 ? send({ error: 'authorization_pending' }, 400) : send({ access_token: 'at', refresh_token: 'rt', expires_in: 3600 });
      if (path === '/xsts') return send({ Token: 'xsts', NotAfter: '2099-01-01T00:00:00Z', DisplayClaims: { xui: [{ uhs: 'u', xid: '1', gtg: 'Papa86' }] } });
      return send({ Token: 't', DisplayClaims: { xui: [{ uhs: 'u' }] } });
    }
    if (path === '/hub/tags') return send({ results: [{ name: 'latest' }, { name: '1.73.0' }, { name: '1.72.1' }] });
    if (path === '/worlds') return send({ servers: [
      { id: 42, name: 'Casa', owner: '', ownerUUID: '1', state: 'OPEN', activeSlot: 3, expired: false },
      { id: 7, name: 'De un amigo', owner: 'Otro', ownerUUID: '9', state: 'CLOSED', activeSlot: 1, expired: false },
    ] });
    if (path.startsWith('/archive/download/world/42/')) return send({ downloadUrl: `http://127.0.0.1:${port}/dl.mcworld`, token: 'dl' });
    if (path === '/dl.mcworld') return send(null, 200, world);
    send({}, 404);
  });
}).listen(port, '127.0.0.1');
