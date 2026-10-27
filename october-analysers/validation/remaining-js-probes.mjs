import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const root = new URL('../../', import.meta.url);
const read = p => fs.readFileSync(new URL(p, root), 'utf8');
const require = createRequire(new URL('Glosify.LiveSubtitles.Extension/package.json', root));
const espree = require('espree');
const source = process.argv[2] ? fs.readFileSync(process.argv[2], 'utf8') : read('Glosify/wwwroot/lib/pdfjs/pdf.min.mjs');
const originalRegexes = JSON.parse(read('october-analysers/validation/remaining-js-before.json')).pdfRegexes;
const ast = espree.parse(source, { ecmaVersion: 'latest', sourceType: 'module', range: true, loc: true });
function declaration(name) {
  let node;
  function find(n) {
    if (!n || typeof n !== 'object' || node) return;
    if (n.id?.name === name) { node = n; return; }
    for (const v of Object.values(n)) { if (Array.isArray(v)) v.forEach(find); else if (v && typeof v === 'object') find(v); }
  }
  find(ast);
  assert.ok(node, name);
  return source.slice(...node.range);
}
function evaluate(name, context = {}) {
  return vm.runInNewContext(declaration(name) + ';' + name, context);
}
function measure(fn, text) {
  const start = performance.now(); fn(text); return +(performance.now() - start).toFixed(3);
}
const result = {};
const reader = read('Glosify/wwwroot/js/book-reader.js');
const boundary = reader.indexOf('const decorateTextLayer =');
const fallback = vm.runInNewContext(reader.slice(0, boundary) + '; fallbackSentenceSegments;', {document:{querySelector:()=>null},window:{},Intl:{}});
result.bookFallback = [10000,100000,1000000].map(length => ({length, ms:measure(fallback,'a'.repeat(length))}));
assert.equal(fallback('Hello! World.').length,2);
const chatContext = {globalThis:{},Date};
vm.runInNewContext(read('Glosify.LiveSubtitles.Extension/lib/chat-buffer.js'),chatContext);
const ChatBuffer = chatContext.globalThis.GlosifySubtitleChat.ChatBuffer;
assert.ok(ChatBuffer);
result.boundedChat = [800,8000,80000].map(length=>({inputLength:length,ms:measure(text=>{
 const c=new ChatBuffer();c.apply({stream:'translation',delta:text,replace:true,sequence:1});assert.ok(c.translation.length<=800);
},'.'.repeat(length)+'x')}));
const regexes=[];
function walk(n) {
 if(!n||typeof n!=='object')return;
 if(n.regex)regexes.push(n);
 for(const [k,v] of Object.entries(n)){if(k==='loc'||k==='range')continue;if(Array.isArray(v))v.forEach(walk);else if(v&&typeof v==='object')walk(v);}
}
walk(ast);
const targets=[13924,13958,80564,159235,237911];
result.pdfRegexes=targets.map(offset=>{
 const n=regexes.find(n=>n.regex.pattern===originalRegexes.find(r=>r.offset===offset).pattern);assert.ok(n,String(offset));
 const rx=new RegExp(n.regex.pattern,n.regex.flags);
 const input=offset===80564 ? 'Mozilla/5.0 rv:1'+'rv:1'.repeat(2000)+'x' : offset===159235?'\t'.repeat(8000)+'x':offset===237911?'a'.repeat(8000)+'!a':'a'.repeat(8000);
 return{offset,pattern:rx.source,inputLength:input.length,ms:measure(t=>rx.test(t),input)};
});
result.pdfCancel=[];
for(const name of ['PDFFetchStreamReader','PDFFetchStreamRangeReader']){
 const Class=evaluate(name);
 const object=Object.create(Class.prototype);
 const expected=new Error(name+' interrupted response body');
 object._reader=new ReadableStream({start(controller){controller.error(expected);}}).getReader();
 object._abortController=new AbortController();
 const errors=[];const listener=e=>errors.push(e);process.on('unhandledRejection',listener);
 object.cancel(new Error('document teardown'));
 await new Promise(resolve=>setTimeout(resolve,10));
 process.removeListener('unhandledRejection',listener);
 result.pdfCancel.push({name,unhandled:errors.map(e=>e.message),aborted:object._abortController.signal.aborted});
 assert.deepEqual(errors,process.argv[2] ? [expected] : []);
}
const headers = evaluate('createHeaders',{Headers});
assert.equal(headers(true,{'X-Test':'present'}).get('X-Test'),'present');
assert.equal(headers(false,{'X-Test':'present'}).get('X-Test'),null);
const disposition=evaluate('getFilenameFromContentDispositionHeader',{unescape,TextDecoder});
assert.equal(disposition('attachment; filename="x.pdf"'),'x.pdf');
assert.equal(disposition("attachment; filename*0=first; filename*0=second; filename*1=tail"),'first');
const filename=evaluate('getPdfFilenameFromUrl',{isDataScheme:t=>/^data:/i.test(t),warn:()=>{}});
assert.equal(filename('https://example.com/a.pdf?download=x.pdf'),'a.pdf');
result.pdfHeaderAndFilename='passed';
console.log(JSON.stringify(result,null,2));
