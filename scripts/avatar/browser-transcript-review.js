// Use the isolated AvatarFixture host with simulated providers and a fake microphone.
// Run with playwright-cli run-code --filename scripts/avatar/browser-transcript-review.js
async page => {
    if (!["localhost", "127.0.0.1"].includes(new URL(page.url()).hostname)) throw new Error("Use the isolated local preview.");
    page.setDefaultTimeout(20000);
    await page.reload();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    async function speak() {
        const mic = page.locator('#avatar-talk');
        await mic.scrollIntoViewIfNeeded(); const b = await mic.boundingBox();
        await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2); await page.mouse.down();
        await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Listening');
        await page.waitForTimeout(400); await page.mouse.up();
        await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Your turn');
    }
    await speak();
    await page.waitForFunction(() => !document.getElementById('avatar-save').disabled);
    await page.locator('#avatar-save').click();
    await page.waitForFunction(() => document.getElementById('avatar-save-status').textContent === 'Transcript saved');
    const link = await page.locator('#avatar-saved-link').getAttribute('href');
    if (!link?.startsWith('/Transcripts/')) throw new Error('Missing saved transcript link');
    await page.locator('#avatar-ask').click();
    await page.waitForFunction(() => document.querySelector('[data-assistant-material-selector]')?.value.startsWith('avatar:'));
    const label = await page.locator('[data-assistant-material-selector] option:checked').textContent();
    if (label !== 'Current avatar conversation') throw new Error('Wrong assistant context: ' + label);
    await page.keyboard.press('Escape');
    await page.locator('#avatar-make-quiz').click();
    await page.waitForFunction(() => [...document.querySelectorAll('textarea')].some(t => t.value === 'Make a quiz based on this avatar conversation.'));
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Expand conversation chat', exact: true }).first().click();
    await page.locator('#avatar-chat-clear').click();
    if (await page.locator('#avatar-ask').isDisabled()) throw new Error('Clearing display lost conversation');
    const transcript = await page.context().newPage(); await transcript.goto(new URL(link, page.url()).href);
    if (!await transcript.locator('.transcript-reader').textContent().then(t => t.includes('You:') && t.includes('Rain:')))
        throw new Error('Speaker labels missing from saved transcript');
    await transcript.close();
    await page.locator('#avatar-autosave').check();
    await page.waitForFunction(() => !document.getElementById('avatar-autosave').disabled);
    await page.reload();
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    if (!await page.locator('#avatar-autosave').isChecked()) throw new Error('Automatic-save preference did not persist');
    await speak();
    await page.waitForFunction(() => document.getElementById('avatar-save-status').textContent === 'Transcript saved');
    if (await page.locator('#avatar-saved-link').getAttribute('href') !== link) throw new Error('Resume created a duplicate transcript');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: 'output/playwright/avatar-transcript-mobile.png', fullPage: true });
    const fits = await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
    if (!fits) throw new Error('Mobile overflow');
    await page.setViewportSize({ width: 1440, height: 1100 });
    await page.screenshot({ path: 'output/playwright/avatar-transcript-desktop.png', fullPage: true });
    if (errors.length) throw new Error(errors.join('\n'));
    console.log('PASS: spoken exchange, manual save, assistant context, quiz prompt, clear display, speaker labels, automatic saving, reconnect, and mobile width.');
}
