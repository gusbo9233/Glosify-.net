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

base = 'https://glosify-app.azurewebsites.net'
app = sys.argv[1]
settings = {row['name']: row['value'] for row in json.loads(subprocess.check_output([
    'az', 'webapp', 'config', 'appsettings', 'list', '-g', 'glosify', '-n', 'glosify-app', '-o', 'json']))}
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
assert catalog['configured'] and catalog['pricingUnit'] == 'characters'
assert catalog['maximumSegmentCredits'] == catalog['creditsPerRequest'] == 4
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
    assert headers.get_content_type() == 'audio/mpeg' and len(body) > 1000
    audio.append(body)
assert audio[0] == audio[1]
expected = (decimal.Decimal(6) * decimal.Decimal('1928.29') / decimal.Decimal(1000000)
            / decimal.Decimal('0.1058')).quantize(decimal.Decimal('0.000001'), rounding=decimal.ROUND_CEILING)
assert before - balance() == expected * 2, 'Exact fractional settlement differs'
_, body = request('/api/me')
assert isinstance(json.loads(body)['availableCredits'], int)
print('Real synthesis, cached playback, exact fractional settlement, and integer balance API passed.')
