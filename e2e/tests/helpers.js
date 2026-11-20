const { expect } = require('@playwright/test');

const ADMIN = { user: 'admin', password: 'contraseña-de-pruebas' };
const OPERATOR = { user: 'papa', password: 'contraseña-de-papa' };

async function login(page, { user, password }) {
  await page.goto('/login');
  await page.fill('input[autocomplete=username]', user);
  await page.fill('input[type=password]', password);
  await page.click('button[type=submit]');
  await expect(page).toHaveURL('/');
}

/** Errores de consola del navegador (incluidas violaciones de la política de contenido). */
function collectErrors(page) {
  const errors = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(e.message));
  return errors;
}

/** Espera a que la página interactiva haya conectado su circuito de Blazor. */
async function interactive(page) {
  await page.waitForFunction(() => window.Blazor !== undefined);
  await page.waitForTimeout(500);
}

module.exports = { ADMIN, OPERATOR, login, collectErrors, interactive };
