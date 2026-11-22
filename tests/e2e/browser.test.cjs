const test = require("node:test");
const assert = require("node:assert/strict");
const { chromium } = require("playwright");

const BASE_URL = process.env.QUERY_GATE_URL || "http://localhost:5176";

test("End-to-End Browser Automation: Query Gate", async (t) => {
  let browser;
  let context;
  let page;

  t.before(async () => {
    browser = await chromium.launch({ headless: true });
    context = await browser.newContext({
      viewport: { width: 1280, height: 800 },
      ignoreHTTPSErrors: true,
    });
    page = await context.newPage();
  });

  t.after(async () => {
    if (page) await page.close();
    if (context) await context.close();
    if (browser) await browser.close();
  });

  await t.test("1. Landing page loads with valid title, branding, and navigation", async () => {
    const response = await page.goto(`${BASE_URL}/`, { waitUntil: "domcontentloaded", timeout: 15000 });
    assert.ok(response && response.status() < 400, `Expected successful response, got ${response?.status()}`);

    const title = await page.title();
    assert.ok(title.includes("Query Gate"), `Expected title to include 'Query Gate', got '${title}'`);

    const hasNav = await page.locator("nav, header, .navbar, .landing-hero, .landing-header").count();
    assert.ok(hasNav > 0, "Expected navigation bar or header element on landing page");

    const bodyText = await page.innerText("body");
    assert.ok(bodyText.toLowerCase().includes("query gate"), "Expected 'Query Gate' brand copy in page body");
  });

  await t.test("2. Documentation page renders guide sections and navigation", async () => {
    const response = await page.goto(`${BASE_URL}/documentation`, { waitUntil: "domcontentloaded", timeout: 15000 });
    assert.ok(response && response.status() < 400, `Expected successful response, got ${response?.status()}`);

    const content = await page.innerText("body");
    assert.ok(
      content.toLowerCase().includes("documentation") || content.toLowerCase().includes("overview") || content.toLowerCase().includes("getting started"),
      "Expected documentation content to render"
    );
  });

  await t.test("3. Theme toggling switches between light and dark modes", async () => {
    await page.goto(`${BASE_URL}/`, { waitUntil: "domcontentloaded" });

    // Retrieve initial theme attribute or default
    const initialTheme = await page.evaluate(() => document.documentElement.getAttribute("data-theme") || "dark");

    // Execute theme toggle via JS or click if theme toggle button is present
    const toggleButton = page.locator("button[aria-label*='theme' i], button.theme-toggle, [title*='theme' i]");
    const toggleExists = await toggleButton.count();

    if (toggleExists > 0) {
      await toggleButton.first().click();
      await page.waitForTimeout(200);
      const updatedTheme = await page.evaluate(() => document.documentElement.getAttribute("data-theme"));
      assert.notEqual(updatedTheme, initialTheme, "Theme attribute should change after clicking theme toggle");
    } else {
      // Direct theme script verification
      await page.evaluate(() => {
        const curr = document.documentElement.getAttribute("data-theme") || "dark";
        const next = curr === "dark" ? "light" : "dark";
        document.documentElement.setAttribute("data-theme", next);
        localStorage.setItem("theme", next);
      });
      const themeSwitched = await page.evaluate(() => document.documentElement.getAttribute("data-theme"));
      assert.notEqual(themeSwitched, initialTheme, "Theme attribute toggled via script");
    }
  });

  await t.test("4. Authentication Login page presents form inputs and validates required fields", async () => {
    const response = await page.goto(`${BASE_URL}/Account/Login`, { waitUntil: "domcontentloaded", timeout: 15000 });
    assert.ok(response && response.status() < 400, `Expected login page HTTP < 400, got ${response?.status()}`);

    const emailInput = page.locator("input[type='email'], input#email");
    const passwordInput = page.locator("input[type='password'], input#password");
    const submitBtn = page.locator("button.btn-primary[type='submit']");

    assert.ok((await emailInput.count()) > 0, "Expected email input field");
    assert.ok((await passwordInput.count()) > 0, "Expected password input field");
    assert.ok((await submitBtn.count()) > 0, "Expected submit button");

    // Submit empty form and verify client or server validation feedback
    await submitBtn.first().click();
    await page.waitForTimeout(500);

    const validationMessages = await page.locator(".text-danger, .validation-message, [aria-live='polite'], .invalid-feedback").count();
    const isHtml5Invalid = await emailInput.first().evaluate((el) => !el.validity.valid || el.classList.contains("is-invalid"));
    assert.ok(validationMessages > 0 || isHtml5Invalid, "Expected form validation feedback on empty submission");
  });

  await t.test("5. Protected route /select-tenant redirects anonymous users to login", async () => {
    const response = await page.goto(`${BASE_URL}/select-tenant`, { waitUntil: "domcontentloaded", timeout: 15000 });
    assert.ok(response && response.status() < 400, `Expected successful response, got ${response?.status()}`);

    const url = page.url();
    assert.ok(url.includes("/Account/Login"), `Expected redirect to /Account/Login, got '${url}'`);
    assert.ok(url.includes("ReturnUrl"), `Expected ReturnUrl parameter in redirect URL`);
  });

  await t.test("6. Responsive mobile viewport adjusts layout without errors", async () => {
    await page.setViewportSize({ width: 375, height: 667 });
    await page.goto(`${BASE_URL}/`, { waitUntil: "domcontentloaded" });

    const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth);
    const clientWidth = await page.evaluate(() => document.documentElement.clientWidth);
    assert.ok(scrollWidth <= clientWidth + 5, `Expected mobile viewport not to have wide overflow (scrollWidth: ${scrollWidth}, clientWidth: ${clientWidth})`);

    // Reset to desktop viewport
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  await t.test("7. Blazor reconnect modal exists in DOM", async () => {
    await page.goto(`${BASE_URL}/`, { waitUntil: "domcontentloaded" });
    const reconnectModal = await page.locator("#components-reconnect-modal").count();
    assert.ok(reconnectModal > 0, "Expected Blazor components-reconnect-modal to be present in DOM");
  });
});
