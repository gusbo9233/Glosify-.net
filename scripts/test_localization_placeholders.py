"""Placeholder validation accepts formatting and safely rejects malformed output."""
import importlib.util
from pathlib import Path
import unittest

MODULE = Path(__file__).resolve().parents[1] / 'tools/localization/translate_ui.py'
SPEC = importlib.util.spec_from_file_location('translate_ui', MODULE)
translate_ui = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(translate_ui)


class PlaceholderTests(unittest.TestCase):
    def test_formats_duplicates_and_unicode_digits_are_preserved(self):
        value = 'From {0} to {1:N2}; {0}; {2,-10}; {٣}; {{blank}}'
        self.assertEqual(['{0}', '{0}', '{1:N2}', '{2,-10}', '{٣}'],
                         translate_ui.placeholders(value))

    def test_nested_and_malformed_braces_keep_existing_matching_semantics(self):
        self.assertEqual(['{0 nested {1}', '{2}'],
                         translate_ui.placeholders('{bad {0 nested {1} } {2} {3'))
        self.assertEqual([], translate_ui.placeholders('{blank} { 0} {} {'))

    def test_long_unclosed_model_output_has_no_placeholders(self):
        self.assertEqual([], translate_ui.placeholders('{' + '1' * 64000))
        self.assertEqual([], translate_ui.placeholders('{1' * 32000))


if __name__ == '__main__':
    unittest.main()
