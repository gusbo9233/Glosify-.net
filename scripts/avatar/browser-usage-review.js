// Run with playwright-cli run-code --filename scripts/avatar/browser-usage-review.js
// Requires the isolated local admin preview with three sample calls: listening, reply and speech.
async page => {
    if (!['localhost', '127.0.0.1'].includes(new URL(page.url()).hostname)) throw new Error('Use the isolated preview.');
    page.setDefaultTimeout(15000);
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.getByRole('tabpanel', { name: 'Admin', exact: true }).getByRole('link', { name: 'Avatar usage', exact: true }).click();
    await page.waitForURL('**/Admin/AvatarUsage');
    if (await page.locator('[data-avatar-usage-call]').count() !== 3) throw new Error('Expected three sample calls');
    await page.getByRole('textbox', { name: 'Provider', exact: true }).fill('openai');
    await page.getByRole('button', { name: 'Filter', exact: true }).click();
    if (await page.locator('[data-avatar-usage-call]').count() !== 1) throw new Error('Provider filter failed');
    if (await page.locator('#avatar-usage-tokens').textContent() !== '300') throw new Error('Reply tokens are wrong');
    if (await page.locator('#avatar-usage-credits').textContent() !== '0.024') throw new Error('Credit total is wrong');
    await page.locator('[data-avatar-usage-call] a').click();
    if (await page.getByRole('textbox', { name: 'Session ID', exact: true }).inputValue() !== 'd1111111-2222-3333-4444-555555555555') throw new Error('Session link failed');
    if (await page.getByRole('textbox', { name: 'Provider', exact: true }).inputValue() !== 'openai') throw new Error('Session link dropped the provider filter');
    await page.getByRole('link', { name: 'Reset', exact: true }).click();
    await page.getByRole('combobox', { name: 'Operation', exact: true }).selectOption('avatar.speech');
    await page.getByRole('button', { name: 'Filter', exact: true }).click();
    if (await page.locator('[data-avatar-usage-call]').count() !== 1) throw new Error('Operation filter failed');
    if (await page.locator('#avatar-usage-characters').textContent() !== '180') throw new Error('Character count is wrong');
    await page.getByRole('textbox', { name: 'Admin email or user ID', exact: true }).fill('nobody@example.test');
    await page.getByRole('button', { name: 'Filter', exact: true }).click();
    if (await page.locator('[data-avatar-usage-call]').count() !== 0) throw new Error('Admin filter failed');
    if (await page.locator('#avatar-usage-credits').textContent() !== '0') throw new Error('Empty total is wrong');
    await page.getByRole('link', { name: 'Reset', exact: true }).click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(350);
    if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth && document.querySelector('main').scrollWidth <= document.querySelector('main').clientWidth)) throw new Error('Mobile page overflows');
    await page.screenshot({ path: 'output/playwright/avatar-usage-mobile.png' });
    await page.getByRole('region', { name: 'Recent Avatar calls', exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: 'output/playwright/avatar-usage-mobile-calls.png' });
    await page.setViewportSize({ width: 1440, height: 1100 });
    await page.getByRole('heading', { name: 'Avatar usage', exact: true }).scrollIntoViewIfNeeded();
    if (errors.length) throw new Error(errors.join('\n'));
    return { passed: true, checks: ['admin settings navigation', 'provider filter', 'session link preserves filters', 'operation filter', 'empty state', 'mobile layout'] };
}
