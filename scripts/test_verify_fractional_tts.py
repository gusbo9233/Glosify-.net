"""Run the release smoke against fake services, including optimized Python."""
import io
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from email.message import Message
from unittest.mock import patch


SCRIPT = Path(__file__).with_name('verify-fractional-tts.py')
SUCCESS = ('Real synthesis, cached playback, exact fractional settlement, '
           'and integer balance API passed.')
PRIVATE_VALUES = ('fixture-password', 'fixture-bearer', 'fixture-antiforgery',
                  'fixture-audio', 'fixture-user@example.invalid', 'Hello.')


def run_fake_smoke(scenario):
    """Execute the actual CLI script without allowing network or CLI fallbacks."""
    shared_auth = scenario != 'success_azure_hostname'
    settings = {
        'Demo__Enabled': 'true',
        'Demo__Email': PRIVATE_VALUES[4],
        'Demo__Password': PRIVATE_VALUES[0],
        'SharedAuth__Enabled': str(shared_auth).lower(),
    }
    catalogue = {'configured': True, 'pricingUnit': 'characters',
                 'maximumSegmentCredits': 4, 'creditsPerRequest': 4}
    if scenario == 'unconfigured':
        catalogue['configured'] = False
    elif scenario == 'wrong_pricing_unit':
        catalogue['pricingUnit'] = 'seconds'
    elif scenario == 'wrong_segment_ceiling':
        catalogue['maximumSegmentCredits'] = 5
    elif scenario == 'wrong_request_ceiling':
        catalogue['creditsPerRequest'] = 5

    audio = PRIVATE_VALUES[3].encode() * 100
    second_audio = audio + b'changed' if scenario == 'cache_mismatch' else audio
    audio_type = 'text/plain' if scenario == 'wrong_audio_type' else 'audio/mpeg'
    if scenario == 'short_audio':
        audio = second_audio = audio[:1000]
    credits = '10' if scenario == 'noninteger_balance' else 10
    replies = iter([
        ('/api/auth/login?useCookies=true', 'application/json', b'{}'),
        ('/api/auth/login', 'application/json',
         json.dumps({'accessToken': PRIVATE_VALUES[1]}).encode()),
        ('/api/tts/voices?lang=en-US', 'application/json', json.dumps(catalogue).encode()),
        ('/account/usage', 'text/html',
         ('<input name="__RequestVerificationToken" value="' + PRIVATE_VALUES[2] + '">').encode()),
        ('/api/tts', audio_type, audio),
        ('/api/tts', 'audio/mpeg', second_audio),
        ('/api/me', 'application/json', json.dumps({'availableCredits': credits}).encode()),
    ])
    # Two six-character requests at 0.109355 credits each, rounded up per request.
    balances = iter(['10', '10' if scenario == 'wrong_settlement' else '9.781290'])
    base = 'https://globeglotter.app' if shared_auth else 'https://glosify-app.azurewebsites.net'
    checks = unittest.TestCase()

    def fake_open(request, timeout):
        path, content_type, body = next(replies)
        checks.assertEqual(request.full_url, base + path)
        checks.assertEqual(timeout, 45)
        response = io.BytesIO(body)
        response.headers = Message()
        response.headers['Content-Type'] = content_type
        return response

    def fake_command(command, **kwargs):
        if command[0] == 'az':
            checks.assertEqual(command, ['az', 'webapp', 'config', 'appsettings', 'list',
                                         '-g', 'glosify', '-n', 'glosify-app', '-o', 'json'])
            return json.dumps([{'name': name, 'value': value}
                               for name, value in settings.items()]).encode()
        checks.assertEqual(command, ['dotnet', 'fixture-app.dll', '--credit-ledger-check', 'balance'])
        checks.assertEqual(kwargs['env']['CreditSmoke__Email'], PRIVATE_VALUES[4])
        checks.assertTrue(kwargs['text'])
        return next(balances)

    with patch('subprocess.check_output', side_effect=fake_command), \
            patch('subprocess.Popen', side_effect=RuntimeError('Live subprocess forbidden')), \
            patch('socket.socket', side_effect=RuntimeError('Live network forbidden')), \
            patch('urllib.request.build_opener') as opener, \
            patch.object(sys, 'argv', [str(SCRIPT), 'fixture-app.dll']):
        opener.return_value.open.side_effect = fake_open
        runpy.run_path(str(SCRIPT), run_name='__main__')
    checks.assertEqual(list(replies), [])
    checks.assertEqual(list(balances), [])


class VerifyFractionalTtsTests(unittest.TestCase):
    def run_scenario(self, scenario, optimized):
        command = [sys.executable]
        if optimized:
            command.append('-O')
        command.extend([str(Path(__file__).resolve()), '--smoke-scenario', scenario])
        # The non-optimized case must stay non-optimized even if the suite uses -O.
        environment = dict(os.environ)
        environment.pop('PYTHONOPTIMIZE', None)
        result = subprocess.run(command, env=environment, capture_output=True,
                                text=True, timeout=15, check=False)
        for private in PRIVATE_VALUES:
            self.assertNotIn(private, result.stdout + result.stderr)
        return result

    def check_failures(self, scenarios, diagnostic):
        for optimized in (False, True):
            for scenario in scenarios:
                with self.subTest(scenario=scenario, optimized=optimized):
                    result = self.run_scenario(scenario, optimized)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn('RuntimeError: ' + diagnostic, result.stderr)
                    self.assertNotIn(SUCCESS, result.stdout)

    def test_rejects_invalid_catalogue(self):
        self.check_failures(('unconfigured', 'wrong_pricing_unit'),
                            'TTS catalogue is not configured for character pricing.')

    def test_rejects_invalid_credit_ceilings(self):
        self.check_failures(('wrong_segment_ceiling', 'wrong_request_ceiling'),
                            'TTS catalogue credit ceilings differ from the release contract.')

    def test_rejects_invalid_audio(self):
        self.check_failures(('wrong_audio_type', 'short_audio'),
                            'TTS response has an unexpected content type or length.')

    def test_rejects_cache_mismatch(self):
        self.check_failures(('cache_mismatch',), 'Cached TTS playback differs from synthesis.')

    def test_rejects_incorrect_settlement(self):
        self.check_failures(('wrong_settlement',), 'Exact fractional settlement differs.')

    def test_rejects_noninteger_balance(self):
        self.check_failures(('noninteger_balance',), 'Balance API did not return integer credits.')

    def test_valid_smoke_passes_with_both_auth_hostnames(self):
        for optimized in (False, True):
            for scenario in ('success', 'success_azure_hostname'):
                with self.subTest(scenario=scenario, optimized=optimized):
                    result = self.run_scenario(scenario, optimized)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(result.stderr, '')
                    self.assertEqual(result.stdout.strip(), SUCCESS)


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--smoke-scenario':
        run_fake_smoke(sys.argv[2])
    else:
        unittest.main()
