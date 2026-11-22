// Pruebas del panel en el navegador. Arranca el panel publicado (como en el contenedor) con FakeBedrockServer y un
// Microsoft simulado. Antes: dotnet publish src/CubeKeeper.Web -c Release -o artifacts/web
//                        dotnet build tests/FakeBedrockServer -c Release
const { defineConfig } = require('@playwright/test');
const fs = require('fs');
const os = require('os');
const path = require('path');

const root = path.resolve(__dirname, '..');
const panelDll = process.env.PANEL_DLL || path.join(root, 'artifacts', 'web', 'CubeKeeper.Web.dll');
const fakeBds = process.env.FAKE_BDS_DLL || path.join(root, 'tests', 'FakeBedrockServer', 'bin', 'Release', 'net11.0', 'FakeBedrockServer.dll');
const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'cubekeeper-e2e-'));
fs.mkdirSync(path.join(dataDir, 'server'), { recursive: true });
fs.writeFileSync(path.join(dataDir, 'server', 'server.properties'), 'server-port=19232\nmax-players=10\n');
const ms = 'http://127.0.0.1:5300';

module.exports = defineConfig({
  testDir: './tests',
  workers: 1,
  fullyParallel: false,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: 'http://localhost:5229',
    viewport: { width: 1200, height: 900 },
    trace: process.env.CI ? 'retain-on-failure' : 'off',
    // En local se puede usar un Chromium ya instalado (PW_CHROMIUM_PATH); en el CI, el que instala Playwright.
    launchOptions: process.env.PW_CHROMIUM_PATH ? { executablePath: process.env.PW_CHROMIUM_PATH } : {},
  },
  webServer: [
    {
      command: 'node fake-microsoft.js',
      url: `${ms}/health`,
      reuseExistingServer: false,
    },
    {
      command: `dotnet "${panelDll}"`,
      // Como en el contenedor: el panel arranca desde su carpeta (ahí busca wwwroot).
      cwd: path.dirname(panelDll),
      url: 'http://localhost:5229/health',
      reuseExistingServer: false,
      timeout: 120_000,
      env: {
        CUBEKEEPER_DATA: dataDir,
        CUBEKEEPER_PORT: '5229',
        CUBEKEEPER_ADMIN_USER: 'admin',
        CUBEKEEPER_ADMIN_PASSWORD: 'contraseña-de-pruebas',
        CUBEKEEPER_KEY_SECRET: 'secreto-de-pruebas-e2e-123456',
        CUBEKEEPER_AUTOSTART: 'false',
        CubeKeeper__Bds__LaunchCommand: 'dotnet',
        CubeKeeper__Bds__LaunchArguments: `"${fakeBds}"`,
        CubeKeeper__Bds__RestartDelaySeconds: '0',
        CubeKeeper__Realms__DeviceCodeUrl: `${ms}/connect`,
        CubeKeeper__Realms__TokenUrl: `${ms}/token`,
        CubeKeeper__Realms__UserAuthUrl: `${ms}/user`,
        CubeKeeper__Realms__DeviceAuthUrl: `${ms}/device`,
        CubeKeeper__Realms__TitleAuthUrl: `${ms}/title`,
        CubeKeeper__Realms__XstsUrl: `${ms}/xsts`,
        CubeKeeper__Realms__Host: ms,
        CubeKeeper__BedrockConnect__TagsUrl: `${ms}/hub/tags`,
      },
    },
  ],
});
