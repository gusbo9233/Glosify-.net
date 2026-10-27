// Web runtime bundles are built by the same pipeline as the MVC assets.
process.argv.push('--production');
await import('../../Glosify/Frontend/build.mjs');
