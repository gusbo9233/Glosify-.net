// Regenerate bundled guest PDFs with the repository's existing Playwright dependency.
// Usage: node scripts/build-guest-books.mjs
import { chromium } from '../Glosify.LiveSubtitles.Extension/node_modules/playwright/index.mjs';
import { readFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const root = new URL('../', import.meta.url);
const content = JSON.parse(await readFile(new URL('Glosify/Services/Guest/starter-content.json', root), 'utf8'));
const catalog = await readFile(new URL('Glosify/Services/Language/QuizLanguageCatalog.cs', root), 'utf8');
const names = new Map([...catalog.matchAll(/new\("([^"]+)", "[^"]+", "[^"]+", "([^"]+)"/g)].map(match => [match[1], match[2]]));
const escape = text => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const browser = await chromium.launch({ headless: true, channel: 'chrome' });
try {
    const page = await browser.newPage({ viewport: { width: 794, height: 1123 } });
    await page.emulateMedia({ media: 'print' });
    await mkdir(new URL('Glosify/wwwroot/samples/', root), { recursive: true });
    for (const code of [...Object.keys(content), 'free']) {
        const general = code === 'free';
        const phrases = content[code] || content.en;
        const source = content[code === 'en' ? 'es' : 'en'];
        const name = general ? 'Freestyle' : names.get(code);
        if (!name) throw new Error(`Missing catalog language: ${code}`);
        const rows = general ? [
            ['Mercury', 'The closest planet to the Sun.'], ['Venus', 'The hottest planet.'],
            ['Earth', 'Our home planet.'], ['Mars', 'The Red Planet.'], ['Jupiter', 'The largest planet.']
        ] : phrases.slice(0, 5).map((phrase, index) => [phrase, source[index]]);
        const dialogue = general ? [
            ['Which planet is closest to the Sun?', 'Mercury.'], ['Which planet is the largest?', 'Jupiter.']
        ] : [3, 0, 5, 6, 1, 4].map(index => [phrases[index], source[index]]);
        await page.setContent(`<!doctype html><html lang="${general ? 'en' : code}"><meta charset="utf-8"><style>
          @page { size: A4; margin: 0; }
          * { box-sizing: border-box; }
          body { margin: 0; padding: 52px 60px; width: 794px; color: #20332d; background: #fff; font: 16px/1.5 Arial, sans-serif; }
          .brand { color: #526b61; font-size: 12px; letter-spacing: 2px; text-transform: uppercase; }
          h1 { font-size: 32px; line-height: 1.2; margin: 22px 0 10px; }
          .intro { color: #52645d; margin-bottom: 24px; }
          h2 { font-size: 18px; margin: 22px 0 10px; }
          .row { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; padding: 8px 0; border-bottom: 1px solid #e4eae6; break-inside: avoid; }
          .target { font-size: 18px; font-weight: 400; }
          .meaning { color: #52645d; font-size: 14px; align-self: center; }
          footer { margin-top: 26px; border-top: 2px solid #3a6653; padding-top: 12px; font-size: 12px; color: #52645d; }
        </style><div class="brand">GlobeGlotter · ${escape(name)} starter</div>
        <h1>${general ? 'Our solar system' : `First words in ${escape(name)}`}</h1>
        <p class="intro">${general ? 'Get to know five planets, then try the sample quiz.' : `Read five useful phrases, then practice a short greeting. Meanings are in ${code === 'en' ? 'Spanish' : 'English'}.`}</p>
        <h2>${general ? 'Five planets' : 'Five useful phrases'}</h2>
        ${rows.map(([target, meaning]) => `<div class="row"><div class="target" dir="auto">${escape(target)}</div><div class="meaning" dir="auto">${escape(meaning)}</div></div>`).join('')}
        <h2>${general ? 'Check your knowledge' : 'A first conversation'}</h2>
        ${dialogue.map(([target, meaning]) => `<div class="row"><div class="target" dir="auto">${escape(target)}</div><div class="meaning" dir="auto">${escape(meaning)}</div></div>`).join('')}
        <footer>Read · Listen · Practice<br>A bundled sample for exploring GlobeGlotter. 1 / 1</footer></html>`);
        await page.evaluate(() => document.fonts.ready);
        const height = await page.evaluate(() => document.documentElement.scrollHeight);
        if (height > 1123) throw new Error(`Sample overflows A4: ${code} (${height}px)`);
        await page.pdf({ path: fileURLToPath(new URL(`Glosify/wwwroot/samples/starter-${code}.pdf`, root)), format: 'A4', printBackground: true });
    }
    console.log(`Generated ${Object.keys(content).length + 1} starter PDFs.`);
} finally {
    await browser.close();
}
