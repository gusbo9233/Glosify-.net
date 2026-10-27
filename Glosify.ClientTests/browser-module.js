import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Load complete compiled modules with browser dependencies supplied by each test.
export async function loadBrowserModule(url, context, replacements = {}) {
    const modules = new Map();
    const load = async url => {
        const key = url.href;
        if (!modules.has(key)) {
            modules.set(key, readFile(url, 'utf8').then(source =>
                new vm.SourceTextModule(source, { context, identifier: key })));
        }
        return modules.get(key);
    };
    const entry = await load(url);
    await entry.link(async (name, parent) => {
        if (!Object.hasOwn(replacements, name)) return load(new URL(name, parent.identifier));
        if (!modules.has(name)) {
            const values = replacements[name];
            modules.set(name, new vm.SyntheticModule(Object.keys(values), function () {
                for (const [name, value] of Object.entries(values)) this.setExport(name, value);
            }, { context }));
        }
        return modules.get(name);
    });
    await entry.evaluate();
    return entry.namespace;
}
