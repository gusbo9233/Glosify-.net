import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
const root = new URL('../../', import.meta.url);
const require = createRequire(new URL('Glosify.Translator.Extension/package.json', root));
const { chromium } = require('playwright');
const objects = [
 '<< /Type /Catalog /Pages 2 0 R >>',
 '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
 '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
 '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
];
const drawing = 'BT /F1 20 Tf 20 100 Td (Hello PDF) Tj ET';
objects.push(`<< /Length ${drawing.length} >>\nstream\n${drawing}\nendstream`);
let pdf = '%PDF-1.4\n';const offsets=[0];
for(let i=0;i<objects.length;i++){offsets.push(Buffer.byteLength(pdf));pdf+=`${i+1} 0 obj\n${objects[i]}\nendobj\n`;}
const xref=Buffer.byteLength(pdf);
pdf+=`xref\n0 ${objects.length+1}\n0000000000 65535 f \n`+offsets.slice(1).map(n=>`${String(n).padStart(10,'0')} 00000 n \n`).join('')+`trailer\n<< /Size ${objects.length+1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
const server=createServer(async(req,res)=>{
 if(req.url==='/tiny.pdf'){res.setHeader('Content-Type','application/pdf');res.end(pdf);return;}
 if(['/pdf.min.mjs','/pdf.worker.min.mjs'].includes(req.url)){
  res.setHeader('Content-Type','text/javascript');res.end(await fs.readFile(new URL('Glosify/wwwroot/lib/pdfjs'+req.url,root)));return;
 }
 res.setHeader('Content-Type','text/html');res.end('<!doctype html><title>PDF probe</title><canvas id="page"></canvas>');
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
let browser;
try{
 browser=await chromium.launch({headless:true,channel:"chromium"});
 const page=await browser.newPage();const errors=[];page.on('pageerror',error=>errors.push(error.message));
 await page.goto(`http://127.0.0.1:${server.address().port}/`);
 const result=await page.evaluate(async()=>{
  const pdfjs=await import('/pdf.min.mjs');pdfjs.GlobalWorkerOptions.workerSrc='/pdf.worker.min.mjs';
  const task=pdfjs.getDocument('/tiny.pdf');const document=await task.promise;
  const page=await document.getPage(1);const text=await page.getTextContent();const viewport=page.getViewport({scale:1});
  const canvas=globalThis.document.querySelector('canvas');canvas.width=viewport.width;canvas.height=viewport.height;
  const context=canvas.getContext('2d');await page.render({canvasContext:context,viewport}).promise;
  const pixels=context.getImageData(0,0,canvas.width,canvas.height).data;
  let ink=0;for(let i=0;i<pixels.length;i+=4)if(pixels[i]<100&&pixels[i+3]>0)ink++;
  await document.destroy();return{version:pdfjs.version,pages:document.numPages,text:text.items.map(item=>item.str).join(''),ink};
 });
 assert.equal(result.version,'4.10.38');assert.equal(result.pages,1);assert.equal(result.text,'Hello PDF');assert.ok(result.ink>20);
 await page.waitForTimeout(50);assert.deepEqual(errors,[]);
 console.log(JSON.stringify({...result,browser:browser.version(),pageErrors:errors},null,2));
}finally{await browser?.close();await new Promise(resolve=>server.close(resolve));}
