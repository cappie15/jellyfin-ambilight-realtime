const { chromium, request } = require('playwright');

const BASE_URL = process.argv[2];
const USERNAME = process.argv[3];
const PASSWORD = process.argv[4];

const AUTH_HEADER = 'MediaBrowser Client="CiSmoke", Device="CiSmoke", DeviceId="ci-smoke-1", Version="1.0.0"';

(async () => {
    const api = await request.newContext();
    const authResponse = await api.post(`${BASE_URL}/Users/AuthenticateByName`, {
        headers: { 'Content-Type': 'application/json', 'Authorization': AUTH_HEADER },
        data: { Username: USERNAME, Pw: PASSWORD },
    });
    if (!authResponse.ok()) {
        console.error('Auth failed:', authResponse.status(), await authResponse.text());
        process.exit(1);
    }
    const auth = await authResponse.json();
    const accessToken = auth.AccessToken;
    const userId = auth.User.Id;
    const serverId = auth.ServerId;
    await api.dispose();

    const browser = await chromium.launch();
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();

    // config.js polls WizardState to learn whether a calibration wizard is
    // currently armed and treats a 404 there as "nothing running" (see the
    // comment above loadWizardState in config.js) -- a benign, expected
    // network-level failure, not an application error, so it is allow-listed
    // here rather than making this smoke test flaky against normal operation.
    const isExpectedWizardStateProbe = (text, url) =>
        /^Failed to load resource: the server responded with a status of 404/.test(text)
        && typeof url === "string" && url.includes("/RealtimeAmbilight/Calibration/WizardState");

    const consoleErrors = [];
    page.on('console', msg => {
        if (msg.type() !== 'error') {
            return;
        }
        if (isExpectedWizardStateProbe(msg.text(), msg.location().url)) {
            return;
        }
        consoleErrors.push(msg.text());
    });
    page.on('pageerror', err => { consoleErrors.push(`pageerror: ${err.message}`); });

    await page.goto(`${BASE_URL}/web/index.html`);
    await page.evaluate(({ accessToken, userId, serverId, baseUrl }) => {
        const credentials = { Servers: [{ Id: serverId, AccessToken: accessToken, UserId: userId, ManualAddress: baseUrl, DateLastAccessed: Date.now() }] };
        window.localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));
    }, { accessToken, userId, serverId, baseUrl: BASE_URL });

    await page.goto(`${BASE_URL}/web/index.html`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(1500);
    await page.evaluate(() => { window.location.hash = '#!/configurationpage?name=Realtime%20Ambilight'; });
    await page.waitForTimeout(2500);

    const url = page.url();
    console.log('Loaded config page at', url);

    const rackCount = await page.locator('.raRack').count();
    const formCount = await page.locator('#realtimeAmbilightConfigurationForm').count();

    await browser.close();

    let failed = false;

    if (!url.includes('configurationpage')) {
        console.error('FAIL: navigation did not land on the configuration page (redirected to login?)');
        failed = true;
    }
    if (rackCount === 0) {
        console.error('FAIL: .raRack (status cards) not found on the page');
        failed = true;
    }
    if (formCount === 0) {
        console.error('FAIL: #realtimeAmbilightConfigurationForm not found on the page');
        failed = true;
    }
    if (consoleErrors.length > 0) {
        console.error(`FAIL: ${consoleErrors.length} browser console error(s):`);
        for (const e of consoleErrors) {
            console.error(' -', e);
        }
        failed = true;
    }

    if (failed) {
        process.exit(1);
    }

    console.log('OK: config page loaded with no console errors.');
})();
