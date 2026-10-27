import js from '@eslint/js';
import globals from 'globals';
import ts from 'typescript-eslint';

export default [
    { ignores: ['node_modules/**'] },
    js.configs.recommended,
    ...ts.configs.recommended.map(config => ({ ...config, files: ['**/*.ts'] })),
    { languageOptions: { globals: { ...globals.browser, ...globals.node } } },
];
