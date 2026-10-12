// Run in the isolated AvatarFixture host with simulated providers and a fake microphone.
// playwright-cli run-code --filename scripts/avatar/browser-retention-review.js
async page => {
    if (!['localhost', '127.0.0.1'].includes(new URL(page.url()).hostname)) throw new Error('Use the isolated local preview.');
    page.setDefaultTimeout(20000);
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    if (await page.locator('[data-assistant-close]').isVisible()) await page.locator('[data-assistant-close]').click();
    // Save fixture turns before test reloads so the CLI does not pause on the
    // browser's intentional beforeunload warning for unsaved conversations.
    if (!await page.locator('#avatar-save').isDisabled()) {
        await page.locator('#avatar-save').click();
        await page.waitForFunction(() => document.getElementById('avatar-save-status').textContent === 'Transcript saved');
    }
    await page.reload();
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    await page.locator('#avatar-autosave').uncheck();
    await page.waitForFunction(() => !document.getElementById('avatar-autosave').disabled);
    const dialog = page.locator('#avatar-unsaved-dialog');
    await page.locator('#avatar-new-conversation').click();
    await page.waitForFunction(() => document.getElementById('avatar-unsaved-dialog').open || !document.getElementById('avatar-new-conversation').disabled);
    if (await dialog.isVisible()) await dialog.locator('[value="discard"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-new-conversation').disabled);
    await page.locator('#avatar-quiz').selectOption('');
    await page.waitForFunction(() => !document.getElementById('avatar-quiz').disabled);
    await page.locator('#avatar-character').selectOption('rain');
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    async function speak() {
        const mic = page.locator('#avatar-talk'); await mic.scrollIntoViewIfNeeded();
        const bounds = await mic.boundingBox();
        await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2); await page.mouse.down();
        await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Listening');
        await page.waitForTimeout(400); await page.mouse.up();
        await page.waitForFunction(() => document.getElementById('avatar-status').textContent === 'Your turn');
        await page.waitForFunction(() => document.getElementById('avatar-save-status').textContent.includes('unsaved'));
    }
    const memoryId = () => page.evaluate(() => {
        const key = `glosify.avatar.memory.${document.body.dataset.speechUser}`;
        return JSON.parse(sessionStorage.getItem(key)).id;
    });
    await speak();
    const originalId = await memoryId();
    const originalText = await page.locator('#avatar-transcript').textContent();
    await page.locator('#avatar-character').selectOption('rowan');
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="cancel"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-character').disabled);
    if (await page.locator('#avatar-character').inputValue() !== 'rain' || await memoryId() !== originalId
        || await page.locator('#avatar-transcript').textContent() !== originalText) throw new Error('Cancel lost the selection or conversation');

    const saveUrl = `**/api/avatar/conversations/${originalId}/transcript`;
    await page.route(saveUrl, route => route.request().method() === 'POST'
        ? route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Simulated save failure' }) })
        : route.continue());
    await page.locator('#avatar-character').selectOption('rowan');
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="save"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-character').disabled);
    if (await page.locator('#avatar-character').inputValue() !== 'rain' || await memoryId() !== originalId
        || await page.locator('#avatar-transcript').textContent() !== originalText) throw new Error('Failed save discarded the conversation');
    await page.unroute(saveUrl);

    await page.locator('#avatar-character').selectOption('rowan');
    await dialog.waitFor({ state: 'visible' });
    const savedResponse = page.waitForResponse(response => response.request().method() === 'POST'
        && response.url().endsWith(`/api/avatar/conversations/${originalId}/transcript`));
    await dialog.locator('[value="save"]').click();
    const saved = await (await savedResponse).json();
    await page.waitForFunction(() => !document.getElementById('avatar-talk').disabled);
    if (await page.locator('#avatar-character').inputValue() !== 'rowan'
        || await page.locator('#avatar-transcript li').count() !== 0) throw new Error('Successful partner switch kept old display rows');
    const reader = await page.context().newPage(); await reader.goto(new URL(`/Transcripts/${saved.transcriptId}`, page.url()).href);
    if (!await reader.locator('.transcript-reader').textContent().then(text => text.includes('You:') && text.includes('Rain:')))
        throw new Error('Save before switching lost spoken turns');
    await reader.close();

    await speak();
    const quizSelect = page.locator('#avatar-quiz');
    const quizId = await quizSelect.locator('option').evaluateAll(options => options.find(option => option.value)?.value);
    if (!quizId) throw new Error('Seed an owned practice quiz in the isolated fixture');
    const beforeQuiz = await memoryId();
    await quizSelect.selectOption(quizId);
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="cancel"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-quiz').disabled);
    if (await quizSelect.inputValue() !== '' || await memoryId() !== beforeQuiz) throw new Error('Quiz Cancel lost previous selection');
    await quizSelect.selectOption(quizId);
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="discard"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-quiz').disabled);
    if (await quizSelect.inputValue() !== quizId || await page.locator('#avatar-transcript li').count() !== 0) throw new Error('Quiz discard kept old rows');
    await speak();
    const beforeNew = await memoryId();
    await page.locator('#avatar-new-conversation').click();
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="cancel"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-new-conversation').disabled);
    if (await memoryId() !== beforeNew || await page.locator('#avatar-transcript li').count() === 0) throw new Error('New conversation Cancel lost turns');
    await page.locator('#avatar-new-conversation').click();
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="save"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-new-conversation').disabled);
    if (await memoryId() || await page.locator('#avatar-transcript li').count() !== 0) throw new Error('New conversation Save kept old rows');
    await speak();
    const nextId = await memoryId();
    if (await page.locator('[data-sidebar-toggle]').getAttribute('aria-expanded') !== 'true') await page.locator('[data-sidebar-toggle]').click();
    await page.locator('.app-nav a[href="/Languages"]').click();
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="cancel"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-character').disabled);
    if (!new URL(page.url()).pathname.startsWith('/Avatar') || await memoryId() !== nextId) throw new Error('Cancel navigated away');
    await page.locator('#avatar-ask').click();
    await page.waitForFunction(() => document.querySelector('[data-assistant-material-selector]')?.value.startsWith('avatar:'));
    await page.locator('[data-assistant-close]').click();
    await page.waitForFunction(() => !document.querySelector('[data-assistant-panel]').classList.contains('is-open'));
    await page.locator('#avatar-new-conversation').click();
    await dialog.waitFor({ state: 'visible' }); await dialog.locator('[value="discard"]').click();
    await page.waitForFunction(() => !document.getElementById('avatar-new-conversation').disabled);
    if (await page.locator('#avatar-transcript li').count() !== 0 || await memoryId()) throw new Error('Explicit discard did not start a fresh conversation');
    await page.locator('[data-assistant-toggle]').click();
    await page.waitForFunction(() => !document.querySelector('[data-assistant-panel]')?.hidden);
    if ((await page.locator('[data-assistant-material-selector]').inputValue()).startsWith('avatar:')) throw new Error('Assistant retained a forgotten live source');
    await page.locator('[data-assistant-close]').click();
    await page.waitForFunction(() => !document.querySelector('[data-assistant-panel]').classList.contains('is-open'));
    await speak();
    await page.locator('#avatar-ask').click();
    await page.waitForFunction(() => document.querySelector('[data-assistant-material-selector]')?.value.startsWith('avatar:'));
    const originalChat = await page.evaluate(() => sessionStorage.getItem('glosify.assistant.activeChatId'));
    await page.locator('[data-assistant-close]').click();
    await page.locator('#avatar-save').click();
    await page.waitForFunction(() => document.getElementById('avatar-save-status').textContent === 'Transcript saved');
    await page.reload();
    await page.waitForFunction(() => !document.getElementById('avatar-ask').disabled);
    await page.locator('#avatar-ask').click();
    await page.waitForFunction(() => document.querySelector('[data-assistant-material-selector]')?.value.startsWith('avatar:'));
    if (await page.evaluate(() => sessionStorage.getItem('glosify.assistant.activeChatId')) !== originalChat)
        throw new Error('Reloading created a duplicate assistant chat for the same conversation');
    await page.locator('[data-assistant-close]').click();
    if (errors.length) throw new Error(errors.join('\n'));
    return 'PASS: unsaved transition Cancel, failed Save retention, Save before partner switch, cleared display, quiz Cancel/Discard, New conversation Cancel/Save/Discard, navigation Cancel, and clearing an unsubmitted assistant source, and reusing its chat after reload.';
}
