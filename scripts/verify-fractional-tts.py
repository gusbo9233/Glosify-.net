"""Production release smoke: no secrets/audio/user text are written to output."""
import decimal
import html
import http.cookiejar
import json
import os
import re
import subprocess
import sys
import urllib.request

app = sys.argv[1]
settings = {row['name']: row['value'] for row in json.loads(subprocess.check_output([
    'az', 'webapp', 'config', 'appsettings', 'list', '-g', 'glosify', '-n', 'glosify-app', '-o', 'json']))}
# Shared sign-in cookies are scoped to .globeglotter.app. The Azure hostname
# cannot accept them, even when login itself returns a successful response.
base = ('https://globeglotter.app'
        if settings.get('SharedAuth__Enabled', '').lower() == 'true'
        else 'https://glosify-app.azurewebsites.net')
if settings.get('Demo__Enabled', '').lower() != 'true':
    raise RuntimeError('Configure the existing deployment smoke account before release.')
email = settings.get('Demo__Email', 'demo@glosify.se')
bearer = None
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def request(path, data=None, token=None):
    headers = {'Content-Type': 'application/json'}
    if bearer:
        headers['Authorization'] = 'Bearer ' + bearer
    if token:
        headers['RequestVerificationToken'] = token
    body = None if data is None else json.dumps(data).encode()
    with opener.open(urllib.request.Request(base + path, data=body, headers=headers), timeout=45) as response:
        return response.headers, response.read()

request('/api/auth/login?useCookies=true', {'email': email, 'password': settings['Demo__Password']})
_, login = request('/api/auth/login', {'email': email, 'password': settings['Demo__Password']})
bearer = json.loads(login)['accessToken']
del settings, login
_, body = request('/api/tts/voices?lang=en-US')
catalog = json.loads(body)
if not catalog['configured'] or catalog['pricingUnit'] != 'characters':
    raise RuntimeError('TTS catalogue is not configured for character pricing.')
if catalog['maximumSegmentCredits'] != 4 or catalog['creditsPerRequest'] != 4:
    raise RuntimeError('TTS catalogue credit ceilings differ from the release contract.')
_, body = request('/account/usage')
token = html.unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', body.decode()).group(1))

def balance():
    value = subprocess.check_output(['dotnet', app, '--credit-ledger-check', 'balance'],
                                    env={**os.environ, 'CreditSmoke__Email': email}, text=True)
    return decimal.Decimal(value.strip())

before = balance()
audio = []
for _ in range(2):
    headers, body = request('/api/tts', {'text': 'Hello.', 'lang': 'en-US',
        'voice': 'JBFqnCBsd6RMkjVDRZzb', 'maxCredits': 4}, token)
    if headers.get_content_type() != 'audio/mpeg' or len(body) <= 1000:
        raise RuntimeError('TTS response has an unexpected content type or length.')
    audio.append(body)
if audio[0] != audio[1]:
    raise RuntimeError('Cached TTS playback differs from synthesis.')
expected = (decimal.Decimal(6) * decimal.Decimal('1928.29') / decimal.Decimal(1000000)
            / decimal.Decimal('0.1058')).quantize(decimal.Decimal('0.000001'), rounding=decimal.ROUND_CEILING)
if before - balance() != expected * 2:
    raise RuntimeError('Exact fractional settlement differs.')
_, body = request('/api/me')
if not isinstance(json.loads(body)['availableCredits'], int):
    raise RuntimeError('Balance API did not return integer credits.')
print('Real synthesis, cached playback, exact fractional settlement, and integer balance API passed.')
