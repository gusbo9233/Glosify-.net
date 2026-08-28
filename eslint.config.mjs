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
  {
    // Existing large browser scripts are linted with the recommended rules,
    // while these narrowly scoped exceptions baseline known cleanup work.
    files: [
      'Glosify/wwwroot/js/assistant.js',
      'Glosify/wwwroot/js/book-reader.js',
    ],
    rules: {
      'no-unused-vars': 'off',
    },
  },
  {
    files: [
      'Glosify/wwwroot/js/quiz-json-import.js',
    ],
    rules: {
      'no-useless-assignment': 'off',
    },
  },
];
