// Renders the VEDAR SVG branding to PNG with Chromium (Playwright). Usage: node render.cjs <brandingDir>
// If original artwork exists in <brandingDir>/original (vedar-logo.png, vedar-wordmark.png), it is used instead.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
const dir = path.resolve(process.argv[2] ?? '.');
const out = path.join(dir, 'generated');
fs.mkdirSync(out, { recursive: true });
const browser = await chromium.launch();
const page = await browser.newPage();

async function render(svgFile, width, height, file, background = 'transparent') {
  let svg = fs.readFileSync(path.join(dir, svgFile), 'utf8');
  // Inline the nested logo for the icon (file:// image references are not loaded from data URLs).
  if (svg.includes('href="vedar-logo.svg"')) {
    const logo = fs.readFileSync(path.join(dir, 'vedar-logo.svg'), 'utf8');
    const dataUri = 'data:image/svg+xml;base64,' + Buffer.from(logo).toString('base64');
    svg = svg.replace('href="vedar-logo.svg"', `href="${dataUri}"`);
  }
  await page.setViewportSize({ width, height });
  await page.setContent(`<html><body style="margin:0;background:${background}">
    <div style="width:${width}px;height:${height}px">${svg.replace('<svg ', `<svg width="${width}" height="${height}" `)}</div></body></html>`);
  await page.waitForTimeout(150);
  await page.screenshot({ path: path.join(out, file), omitBackground: background === 'transparent', clip: { x: 0, y: 0, width, height } });
  console.log('rendered', file);
}

await render('vedar-logo.svg', 1024, 1024, 'vedar-logo-1024.png');
await render('vedar-icon.svg', 1024, 1024, 'vedar-icon-1024.png');
await render('vedar-wordmark.svg', 1550, 300, 'vedar-wordmark-1550.png');
await browser.close();
})();
