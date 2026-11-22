// @ts-check
const { test, expect } = require('@playwright/test');
const { ADMIN, OPERATOR, login, collectErrors, interactive } = require('./helpers');

test.describe.configure({ mode: 'serial' });

test('el login rechaza una contraseña mala y entra con la buena', async ({ page }) => {
  await page.goto('/login');
  await page.fill('input[autocomplete=username]', ADMIN.user);
  await page.fill('input[type=password]', 'no-es-esta-contraseña');
  await page.click('button[type=submit]');
  await expect(page.locator('.alert-error')).toBeVisible();
  await login(page, ADMIN);
  await expect(page.locator('h1')).toHaveText('Servidor');
});

test('las respuestas llevan las cabeceras de seguridad', async ({ request }) => {
  const res = await request.get('/login');
  const h = res.headers();
  expect(h['content-security-policy']).toContain("script-src 'self'");
  expect(h['content-security-policy']).toContain("frame-ancestors 'self'");
  expect(h['x-frame-options']).toBe('SAMEORIGIN');
  expect(h['x-content-type-options']).toBe('nosniff');
  expect(h['referrer-policy']).toBe('same-origin');
});

test('las páginas cargan sin errores en el navegador ni avisos de claves sin cifrar', async ({ page }) => {
  const errors = collectErrors(page);
  await login(page, ADMIN);
  await expect(page.getByText('sin cifrar en /data/keys')).toHaveCount(0);
  for (const path of ['/', '/jugadores', '/familia', '/juego', '/mapa', '/acceso', '/mundos', '/realms', '/complementos',
    '/ajustes', '/copias', '/consola', '/avisos', '/auditoria', '/usuarios', '/cuenta']) {
    const res = await page.goto(path);
    expect(res?.status(), path).toBe(200);
    await expect(page.locator('h1').first(), path).toBeVisible();
  }
  expect(errors).toEqual([]);
});

test('arranca el servidor, ve a un jugador y lo para', async ({ page }) => {
  await login(page, ADMIN);
  await interactive(page);
  await page.click('button:has-text("Arrancar")');
  await expect(page.locator('.badge-ok').first()).toBeVisible({ timeout: 30_000 });

  await page.goto('/consola');
  await interactive(page);
  await page.fill('.command-bar input', 'join Alucard 2535400000000001');
  await page.click('.command-bar button');
  await page.goto('/jugadores');
  await expect(page.getByText('Alucard').first()).toBeVisible();

  await page.goto('/');
  await interactive(page);
  await page.click('button:has-text("Parar")');
  await expect(page.locator('.badge-ok')).toHaveCount(0, { timeout: 30_000 });
});

test('un operador no ve ni abre las páginas de administración', async ({ page, browser }) => {
  await login(page, ADMIN);
  await page.goto('/usuarios');
  await interactive(page);
  await page.fill('.form-row input >> nth=0', OPERATOR.user);
  await page.fill('.form-row input[type=password]', OPERATOR.password);
  await page.click('button:has-text("Añadir")');
  await expect(page.locator('.alert-ok')).toContainText('creado como operador');

  const op = await (await browser.newContext()).newPage();
  await login(op, OPERATOR);
  const nav = await op.locator('nav a').allTextContents();
  expect(nav.map(s => s.trim())).toEqual(expect.arrayContaining(['Panel', 'Jugadores', 'Mapa']));
  for (const admin of ['Ajustes', 'Usuarios', 'Realms', 'Consola']) {
    expect(nav.map(s => s.trim())).not.toContain(admin);
  }
  await op.goto('/ajustes');
  await expect(op).not.toHaveURL(/\/ajustes$/);
  const download = await op.request.get('/mundos/descargar/x', { maxRedirects: 0 });
  expect(download.status()).not.toBe(200);
});

test('Realms: conectar con código, ver los Realms, importar una ranura y desconectar', async ({ page }) => {
  await login(page, ADMIN);
  await page.goto('/realms');
  await interactive(page);
  await page.click('button:has-text("Conectar cuenta")');
  await expect(page.locator('.device-code')).toHaveText('K7PQ2XWM');
  await expect(page.getByText('Conectada como')).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText('Papa86').first()).toBeVisible();

  const casa = page.locator('tbody tr', { hasText: 'Casa' });
  await expect(casa.locator('.badge')).toHaveText('tuyo');
  await expect(casa.locator('option')).toHaveCount(3);
  await expect(page.locator('tbody tr', { hasText: 'De un amigo' })).toContainText('No eres el dueño');

  await casa.locator('select').selectOption('1');
  await casa.locator('button:has-text("Importar…")').click();
  await expect(page.locator('h2', { hasText: 'Importar' })).toHaveText('Importar «Casa», ranura 1');
  await page.fill('section.card:has(h2:has-text("Importar")) input:not([type=checkbox])', 'Casa desde Realm');
  await page.click('button:has-text("Descargar e importar")');
  await expect(page.locator('.alert-ok')).toContainText('Casa desde Realm', { timeout: 30_000 });

  await page.goto('/mundos');
  await expect(page.locator('tbody strong', { hasText: 'Casa desde Realm' })).toBeVisible();

  await page.goto('/realms');
  await interactive(page);
  await page.click('button:has-text("Desconectar…")');
  await page.click('button:has-text("Sí, desconectar")');
  await expect(page.locator('button:has-text("Conectar cuenta")')).toBeVisible();

  await page.goto('/auditoria');
  await expect(page.getByText('Importó un mundo de Realms')).toBeVisible();
});

test('la página de avisos ofrece los fallos de mantenimiento', async ({ page }) => {
  await login(page, ADMIN);
  await page.goto('/avisos');
  await expect(page.getByText('Fallos de mantenimiento')).toBeVisible();
});

test('en el móvil el menú se pliega, se abre y se cierra al elegir una página', async ({ browser }) => {
  const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const page = await ctx.newPage();
  await login(page, ADMIN);
  await expect(page.locator('#main-nav')).toBeHidden();
  await page.click('.nav-burger');
  await expect(page.locator('#main-nav')).toBeVisible();
  await page.click('#main-nav a:has-text("Copias")');
  await expect(page).toHaveURL(/\/copias$/);
  await expect(page.locator('#main-nav')).toBeHidden();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
  expect(overflow).toBe(false);
  await ctx.close();
});

test('Copias muestra el espacio en disco y comprueba una copia', async ({ page }) => {
  await login(page, ADMIN);
  await page.goto('/copias');
  await interactive(page);
  await expect(page.getByRole('heading', { name: 'Espacio en disco' })).toBeVisible();
  await expect(page.getByText('Datos (/data)')).toBeVisible();
  await page.click('button:has-text("Hacer copia ahora")');
  await expect(page.locator('.alert-ok')).toBeVisible({ timeout: 30_000 });
  await page.click('button:has-text("Comprobar una ahora")');
  await expect(page.locator('.alert')).toContainText('opia', { timeout: 30_000 });
});

test('se puede descargar el diagnóstico y no lleva secretos', async ({ page }) => {
  await login(page, ADMIN);
  const res = await page.request.get('/diagnostico/descargar');
  expect(res.status()).toBe(200);
  expect(res.headers()['content-type']).toContain('application/zip');
  const body = await res.body();
  expect(body.subarray(0, 2).toString()).toBe('PK');
  // Los nombres de archivo del zip van sin comprimir: deben estar.
  expect(body.includes(Buffer.from('resumen.txt'))).toBe(true);
});

test('el panel se puede instalar como app (manifiesto e iconos)', async ({ request }) => {
  const manifest = await request.get('/manifest.webmanifest');
  expect(manifest.status()).toBe(200);
  expect(manifest.headers()['content-type']).toContain('manifest+json');
  const json = await manifest.json();
  expect(json.display).toBe('standalone');
  for (const icon of json.icons) {
    expect((await request.get('/' + icon.src)).status(), icon.src).toBe(200);
  }
});

test('el Panel guarda el reinicio programado y muestra los recursos', async ({ page }) => {
  await login(page, ADMIN);
  await interactive(page);
  await expect(page.getByRole('heading', { name: 'Recursos' })).toBeVisible();
  const card = page.locator('section.card:has(h2:has-text("Reinicio programado"))');
  await card.locator('input[type=checkbox]').first().check();
  await card.locator('input[type=time]').fill('04:45');
  await card.locator('button:has-text("Guardar")').click();
  await expect(page.locator('.alert-ok')).toContainText('04:45');
  await expect(card).toContainText('Próximo');
});

test('Jugadores muestra el tiempo de juego por día', async ({ page }) => {
  await login(page, ADMIN);
  await page.goto('/jugadores');
  await interactive(page);
  const card = page.locator('section.card:has(h2:has-text("Tiempo de juego"))');
  await expect(card).toBeVisible();
  // En la prueba de arrancar el servidor entró «Alucard»: tiene tiempo hoy.
  await expect(card.locator('.legend')).toContainText('Alucard');
  await card.locator('button:has-text("30 días")').click();
  await expect(card.locator('.stack-col')).toHaveCount(30);
});

test('un login con la protección caducada vuelve a cargar con aviso (app del móvil restaurada)', async ({ browser }) => {
  const ctx = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const page = await ctx.newPage();
  await page.goto('/login?ReturnUrl=%2Fjugadores');
  await page.fill('input[autocomplete=username]', ADMIN.user);
  await page.fill('input[type=password]', ADMIN.password);
  await ctx.clearCookies(); // el móvil cerró la app y borró las cookies de sesión
  await page.click('button[type=submit]');
  await expect(page.locator('.alert-warn')).toContainText('había caducado');
  await page.fill('input[autocomplete=username]', ADMIN.user);
  await page.fill('input[type=password]', ADMIN.password);
  await page.click('button[type=submit]');
  await expect(page).toHaveURL(/\/jugadores$/);
  expect((await page.request.get('/login?caducado=loquesea')).status()).toBe(200);
  await ctx.close();
});

test('Avisos vigila la versión de BedrockConnect', async ({ page }) => {
  await login(page, ADMIN);
  await page.goto('/avisos');
  await interactive(page);
  const card = page.locator('section.card:has(h2:has-text("BedrockConnect"))');
  await card.locator('input').fill('1.72.1');
  await card.locator('button:has-text("Guardar")').click();
  await expect(page.locator('.alert-ok')).toContainText('1.72.1');
  await card.locator('button:has-text("Comprobar ahora")').click();
  await expect(page.locator('.alert-ok')).toContainText('1.73.0');
  await expect(card).toContainText('Última publicada: 1.73.0');
});

test('Mi cuenta cierra las demás sesiones y esta sigue abierta', async ({ page }) => {
  await login(page, ADMIN);
  await page.goto('/cuenta');
  await page.click('button:has-text("Cerrar las demás sesiones")');
  await expect(page.locator('.alert-ok')).toContainText('demás sesiones');
  await page.goto('/');
  await expect(page).toHaveURL('/');
});

test('un enlace de aviso inventado no aprueba a nadie ni pide login', async ({ request }) => {
  const res = await request.post('/acceso/aviso/inventado/aprobar', { maxRedirects: 0 });
  expect(res.status()).toBe(409);
  expect(await res.text()).toContain('caducado');
  // Una acción que no existe no hace nada (redirige al login como cualquier otra página).
  expect((await request.post('/acceso/aviso/x/borrar-todo', { maxRedirects: 0 })).status()).not.toBe(200);
});

test('abrir el panel desde fuera (app de Android, enlace) mantiene la sesión y el login funciona a la primera', async ({ browser }) => {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  // Sin sesión: el login abierto desde fuera entra al primer intento (antes caducaba siempre).
  await page.goto('/login');                       // deja una cookie antifalsificación antigua
  await page.goto('http://127.0.0.1:5300/lanzador');
  await page.click('#login');
  await page.fill('input[autocomplete=username]', ADMIN.user);
  await page.fill('input[type=password]', ADMIN.password);
  await page.check('input[type=checkbox]');
  await page.click('button[type=submit]');
  await expect(page).toHaveURL('/');
  // Con «Recordarme», abrir el panel desde fuera no vuelve a pedir el login.
  await page.goto('http://127.0.0.1:5300/lanzador');
  await page.click('#go');
  await expect(page).toHaveURL(/\/jugadores$/);
  // Un formulario de otra web no puede usar la sesión (cerrarla, en este caso).
  await page.goto('http://127.0.0.1:5300/lanzador');
  await page.click('#evil button');
  await page.goto('/jugadores');
  await expect(page).toHaveURL(/\/jugadores$/);
  await ctx.close();
});
