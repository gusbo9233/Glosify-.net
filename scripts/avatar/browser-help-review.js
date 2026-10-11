// Run with playwright-cli run-code --filename scripts/avatar/browser-help-review.js
// Uses the isolated local Avatar fixture and fake microphone; no paid provider calls.
async page => {
    if (!['localhost', '127.0.0.1'].includes(new URL(page.url()).hostname)) throw new Error('Use the isolated local preview.');
    page.setDefaultTimeout(15000);
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.reload();
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    const translate = page.getByRole('checkbox', { name: 'English translations' });
    const suggest = page.getByRole('checkbox', { name: 'Suggested answers' });
    await translate.uncheck(); await suggest.uncheck();
    // A focused checkbox must keep Space's native behavior, without opening the mic.
    await translate.focus(); await page.keyboard.press('Space');
    if (!await translate.isChecked()) throw new Error('Space did not toggle the checkbox');
    if (await page.locator('#avatar-status').textContent() !== 'Ready when you are') throw new Error('A setting opened the microphone');
    await suggest.check();
    await page.getByRole('combobox', { name: 'Conversation partner', exact: true }).selectOption('rain');
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    await page.getByRole('button', { name: 'Close conversation settings', exact: true }).click();
    await page.locator('#avatar-canvas').click({ position: { x: 50, y: 120 } });
    const before = await page.evaluate(() => ({ window: scrollY, main: document.querySelector('main').scrollTop }));
    await page.keyboard.down('Space');
    await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Listening');
    await page.keyboard.down('Space'); // An actual repeated keydown must not scroll or start a second turn.
    await page.waitForTimeout(650);
    const during = await page.evaluate(() => ({ window: scrollY, main: document.querySelector('main').scrollTop }));
    await page.keyboard.up('Space');
    if (JSON.stringify(before) !== JSON.stringify(during)) throw new Error('Space scrolled the page');
    await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Your turn');
    if (await page.locator('#avatar-translation').textContent() !== 'Hi! How are you?') throw new Error('Missing English reply');
    if (await page.locator('#avatar-suggestion-text').textContent() !== 'Hur har din dag varit?') throw new Error('Missing Swedish suggestion');
    if (await page.locator('#avatar-suggestion-translation').textContent() !== 'How has your day been?') throw new Error('Missing translated suggestion');
    await page.screenshot({ path: 'output/playwright/avatar-help-desktop.png', fullPage: true });
    await page.getByRole('button', { name: 'Open conversation settings', exact: true }).click();
    await translate.uncheck();
    if (await page.locator('#avatar-translation').isVisible() || await page.locator('#avatar-suggestion-translation').isVisible()) throw new Error('Disabled translation remained visible');
    if (!await page.locator('#avatar-suggestion').isVisible()) throw new Error('Translation toggle hid the suggestion');
    await translate.check();
    await page.reload();
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    if (!await translate.isChecked() || !await suggest.isChecked()) throw new Error('Help preferences were not remembered');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(500);
    await page.screenshot({ path: 'output/playwright/avatar-help-mobile-settings.png', fullPage: true });
    await page.getByRole('button', { name: 'Close conversation settings', exact: true }).click();
    const talk = page.getByRole('button', { name: 'Hold to record', exact: true });
    const box = await talk.boundingBox();
    if (box.width < 72 || box.height < 72) throw new Error('Mobile microphone target shrank');
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: box.x + box.width / 2, y: box.y + box.height / 2 }] });
    await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Listening');
    await page.waitForTimeout(650);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await cdp.detach();
    await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Your turn');
    const layout = await page.evaluate(() => {
        const help = document.getElementById('avatar-dialogue').getBoundingClientRect();
        const mic = document.getElementById('avatar-talk').getBoundingClientRect();
        return { fits: document.documentElement.scrollWidth <= innerWidth, gap: mic.top - help.bottom };
    });
    if (!layout.fits || layout.gap < 0) throw new Error('Mobile help overlaps the microphone or overflows');
    await page.screenshot({ path: 'output/playwright/avatar-help-mobile.png', fullPage: true });
    await page.setViewportSize({ width: 1440, height: 1100 });
    await page.getByRole('button', { name: 'Open conversation settings', exact: true }).click();
    if (errors.length) throw new Error(errors.join('\n'));
    console.log('PASS: Space capture without scrolling, native checkbox Space, reply translation, suggested answer, independent visibility, preference persistence, mobile touch hold/release and layout.');
}
