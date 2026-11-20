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
