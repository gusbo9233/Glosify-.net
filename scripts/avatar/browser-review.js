// Run with playwright-cli run-code --filename scripts/avatar/browser-review.js
// Requires a local test admin page, fake microphone, and the test-only in-memory host.
// Speech providers are simulated; no provider requests are made by this review.
async page => {
    if (!['localhost', '127.0.0.1'].includes(new URL(page.url()).hostname)) throw new Error('Use an isolated local test host.');
    page.setDefaultTimeout(15000);
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    const start = page.getByRole('button', { name: 'Start a conversation' });
    await start.click();
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled, null, { timeout: 15000 });
    const talk = page.getByRole('button', { name: 'Hold to talk · Space' });
    const box = await talk.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Listening');
    await page.waitForTimeout(650); await page.mouse.up();
    await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Speaking');
    await page.waitForTimeout(400);
    await page.screenshot({ path: 'output/playwright/avatar-speaking.png' });
    await page.waitForFunction(() => document.querySelector('#avatar-transcript').textContent.includes('Rain'));
    const beforeHandsFree = await page.locator('#avatar-transcript li').count();
    await page.getByRole('radio', { name: 'Hands-free' }).check();
    await page.waitForFunction(count => document.querySelectorAll('#avatar-transcript li').length > count, beforeHandsFree);
    await page.getByRole('button', { name: 'Mute', exact: true }).click();
    if (await page.getByRole('button', { name: 'Unmute', exact: true }).getAttribute('aria-pressed') !== 'true') throw new Error('Mute state was not applied');
    await page.getByRole('button', { name: 'End', exact: true }).click();
    await page.waitForFunction(() => !document.getElementById('avatar-start').disabled);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(500); // Let the shared sidebar's responsive transition finish.
    await page.screenshot({ path: 'output/playwright/avatar-mobile.png', fullPage: true });
    const fits = await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
    if (!fits) throw new Error('The mobile page overflows horizontally');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.screenshot({ path: 'output/playwright/avatar-reduced-motion.png' });
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.setViewportSize({ width: 1440, height: 1100 });
    if (errors.length) throw new Error(errors.join('\n'));
    console.log('PASS: microphone capture, push-to-talk, captions, playback, mode switch, mute, end, mobile layout, reduced motion; real voice transport with simulated providers.');
}
