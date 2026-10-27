import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const root=new URL('../../', import.meta.url).pathname;
const read=p=>fs.readFileSync(root+p,'utf8');
const results=[];
const record=(name,evidence)=>results.push({name,evidence});
// Execute the repository's callback adapter with the extra parameters flagged by the analyzer.
const tts=read('Glosify/wwwroot/js/tts.js');
const safeCall=tts.slice(tts.indexOf('function safeCall('),tts.indexOf('function ensureCurrent('));
const sandbox={console,args:null};vm.runInNewContext(safeCall+'; safeCall((...values)=>args=values, "playing", 17);',sandbox);
assert.deepEqual([...sandbox.args],['playing',17]);record('TTS extra arguments','The actual safeCall implementation forwards both arguments through arguments/apply.');
// Load the actual queue code, substituting only the PDF dependency and DOM fixture.
const source=read('Glosify/wwwroot/js/books-library.js').replace("const pdfjs = await import('/lib/pdfjs/pdf.min.mjs');",'const pdfjs = globalThis.pdfStub;');
const classes=[];const canvas={dataset:{pdfUrl:'/owned-book'},parentElement:{clientWidth:112},classList:{add:x=>classes.push(x)},getContext:()=>({})};
const box={document:{querySelector:()=>null,querySelectorAll:()=>[canvas]},window:{devicePixelRatio:1},pdfStub:{GlobalWorkerOptions:{},getDocument:()=>({promise:Promise.resolve({getPage:async()=>({getViewport:()=>({width:112,height:150}),render:()=>({promise:Promise.resolve()})}),destroy:async()=>{throw new Error('probe: PDF worker teardown failed');}})})}};
let unhandled;const listener=reason=>{unhandled=reason;};process.once('unhandledRejection',listener);
await vm.runInNewContext('(async()=>{'+source+'})()',box);
await new Promise(resolve=>setImmediate(resolve));
process.removeListener('unhandledRejection',listener);
assert.equal(unhandled,undefined);assert.ok(classes.includes('is-rendered'));
record('books-library floating promise','Current module handles document.destroy() rejection and retains the rendered thumbnail. Queue continuation and render-error preservation are covered by Glosify.ClientTests/books-library.test.js.');
// A source-level accessibility check checks the actual elements, not analyzer line numbers.
for(const [path,marker] of [['Glosify/Views/Quiz/Settings.cshtml','name="WordRangeStart"'],['Glosify/Views/Quiz/Settings.cshtml','name="WordRangeEnd"'],['Glosify/Views/Quiz/Index.cshtml','data-json-import-generation-prompt']]){
 const text=read(path);const offset=text.indexOf(marker);const element=text.slice(text.lastIndexOf('<',offset),text.indexOf('>',offset)+1);
 assert.equal(/\b(?:aria-label|aria-labelledby)=/.test(element),true);record('Named field',path+' '+marker+' has an explicit ARIA naming attribute; rendered associations are covered by .NET view tests.');
}
const css=read('Glosify/wwwroot/css/site.css');const rule=css.match(/\.glass-card > \.auth-form:has\(input\[name="provider"\]\) \.btn-primary-pill:hover\s*\{([^}]+)\}/)[1];
assert.equal((rule.match(/background:/g)||[]).length,1);assert.ok(rule.includes('background: var(--surface-bright)'));record('CSS background cleanup','One background declaration preserves the previously effective --surface-bright value.');
// Test the constrained dynamic regex with hostile-looking path data.
const learning=await import('data:text/javascript;base64,'+Buffer.from(read('Glosify/wwwroot/js/learning-updates.js')).toString('base64'));
assert.equal(learning.isRemovedItemRedirect({ok:true,redirected:true,url:'https://test.local/Quiz'},'https://test.local/Quiz/Details/123'),true);
assert.equal(learning.isRemovedItemRedirect({ok:true,redirected:true,url:'https://evil.test/Quiz'},'https://test.local/Quiz/Details/123'),false);
assert.equal(learning.isRemovedItemRedirect({ok:true,redirected:true,url:'https://test.local/((a+)+)'},'https://test.local/((a+)+)/Details/123'),false);
record('Learning redirect regex','Actual helper accepts the same-origin Quiz library and rejects external/unrecognized regex-like resources.');
fs.writeFileSync(new URL('source-probe-results.json', import.meta.url),JSON.stringify(results,null,2)+'\n');console.log(JSON.stringify(results,null,2));
