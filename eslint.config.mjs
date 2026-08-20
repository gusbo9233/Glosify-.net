import { createRequire } from 'node:module';

const require = createRequire(new URL('./Glosify.LiveSubtitles.Extension/package.json', import.meta.url));
const js = require('@eslint/js');
const globals = require('globals');

export default [
  {
    ignores: [
      '**/node_modules/**',
      '**/artifacts/**',
      '**/coverage/**',
      '**/dist/**',
      '**/*.min.js',
    ],
  },
  js.configs.recommended,
  {
    files: ['**/*.js', '**/*.mjs'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: {
        ...globals.browser,
        ...globals.node,
        chrome: 'readonly',
      },
    },
    rules: {
      'no-console': 'off',
    },
  },
];
