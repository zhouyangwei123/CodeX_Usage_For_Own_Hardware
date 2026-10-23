// Offline raster check of the demo's actual drawing code; no browser is launched.
const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('assert');
const root=path.resolve(__dirname,'../..'),out=path.join(root,'artifacts/oled-usage-20261023/checks');
fs.mkdirSync(out,{recursive:true});
const html=fs.readFileSync(path.join(root,'demos/oled-quota-128x64.html'),'utf8');
assert(html.includes('<option value="account" selected>'),'Whole account must be the default demo');
function surface(){
 const canvas={pixels:new Uint8Array(128*64)};
 const ctx={fillStyle:'#000',fillRect(x,y,w,h){assert(x>=0&&y>=0&&x+w<=128&&y+h<=64,'Rectangle outside screen');for(let py=y;py<y+h;py++)for(let px=x;px<x+w;px++)canvas.pixels[py*128+px]=this.fillStyle==='#fff'?255:0;},drawImage(other){canvas.pixels.set(other.pixels);}};
 canvas.getContext=()=>ctx;return canvas;
}
const screen=surface(),actual=surface(),nodes={};
for(const match of html.matchAll(/id="([^"]+)"/g))nodes['#'+match[1]]={value:'',checked:false,listeners:{},addEventListener(event,callback){this.listeners[event]=callback;}};
nodes['#screen']=screen;nodes['#actual']=actual;
nodes['#mode'].value='account';nodes['#footer-rows'].value='1';nodes['#quota'].value='78';
const sandbox=vm.createContext({window:{},document:{querySelector(selector){assert(nodes[selector],selector);return nodes[selector];}}});
vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1],sandbox);
for(const [input,expected] of [[0,1],[1,1],[1.1,2],[5,5],[5.1,10],[999,1000],[1000,1000],[1001,2000],[999999,1e6],[1000001,2e6]])assert.equal(vm.runInContext('niceCeiling('+input+')',sandbox),expected,'1/2/5 ceiling');
vm.runInContext('scaleTops.local=0',sandbox);
for(const [peak,expected] of [[19999,20000],[20001,50000],[19999,50000],[16001,50000],[16000,20000]])assert.equal(vm.runInContext('scaleTop("local",'+peak+')',sandbox),expected,'Scale hysteresis');
const checks=[];
for(let sample=0;sample<6;sample++)for(const mode of ['local','account','both'])for(const footer of ['1','2'])for(const quota of ['0','78','100'])for(const stale of [false,true]){
 vm.runInContext('step='+sample,sandbox);
 nodes['#mode'].value=mode;nodes['#footer-rows'].value=footer;nodes['#quota'].value=quota;nodes['#stale'].checked=stale;
 nodes['#mode'].listeners.change();
 const frame=sandbox.window.oledFrame;assert.equal(frame.overflow,0,JSON.stringify(frame));
 if(mode!=='both'&&footer==='1')assert(frame.footer[1].length<=21&&frame.footer[1].includes('API 6.80'),'API spacing and footer bounds');
 assert.deepEqual(screen.pixels,actual.pixels,'Original-size preview differs');
 const bar=screen.pixels.slice(11*128,12*128).filter(v=>v).length;
 if(quota==='0')assert.equal(bar,2);if(quota==='100')assert.equal(bar,124);
 assert(screen.pixels.slice(4*128,9*128).some((v,i)=>i%128>=82&&v),'Network header missing');
 assert.equal(frame.chartHeight,footer==='1'?35:27,'Chart lost vertical space');
 if(stale){assert.equal(frame.charts.length,0);assert.equal(frame.localCurrent,null);assert(frame.footer.join(' ').includes('--'));}
 else{
  assert.equal(frame.charts.length,mode==='both'?2:1);
  assert.equal(frame.input+frame.output,frame.localCurrent,'Input/output sum differs from current total');
  if(mode!=='account')assert(frame.footer.join(' ').includes('↑')&&frame.footer.join(' ').includes('↓'),'Input/output arrows missing');
  for(const chart of frame.charts){
   assert(chart.upper>=chart.peak&&chart.current<=chart.upper);
   const normalized=chart.upper/10**Math.floor(Math.log10(chart.upper));assert([1,2,5].some(n=>Math.abs(n-normalized)<1e-9),'Nonstandard scale');
   assert.equal(chart.current,chart.key==='local'?frame.localCurrent:frame.accountCurrent,'Footer/curve current differs');
   const p=chart.plot,h=p.bottom-p.top;
   assert(chart.point[0]===p.right&&chart.point[1]>=p.top&&chart.point[1]<=p.bottom);
   assert(Math.abs((p.bottom-chart.point[1])*chart.upper/h-chart.current)<=chart.upper/h/2+1e-6,'Last-point scale mapping');
   assert.equal(chart.ticks.length,mode==='both'?2:3,'Tick-label fit in small chart');
  }
 }
 checks.push({sample,...frame});
}
function save(name,footer,mode='local'){
 nodes['#mode'].value=mode;nodes['#footer-rows'].value=footer;nodes['#quota'].value='78';nodes['#stale'].checked=false;nodes['#footer-rows'].listeners.change();
 fs.writeFileSync(path.join(out,name+'.pgm'),Buffer.concat([Buffer.from('P5\n128 64\n255\n'),Buffer.from(screen.pixels)]));
}
vm.runInContext('step=0;scaleTops.local=0;scaleTops.account=0',sandbox);
save('oled-one-footer','1');save('oled-two-footers','2');save('oled-dual-chart','1','both');save('oled-account-chart','1','account');
save('oled-one-footer','1');const before=Buffer.from(screen.pixels);nodes['#advance'].listeners.click();assert(!before.equals(Buffer.from(screen.pixels)),'Activity control did not update trace');
const report={passed:true,pixelStatesChecked:checks.length,zeroPixelOverflow:true,quotaEndpointsCorrect:true,originalSizeMatches:true,interactiveHandlerChangesCurve:true,ceilingAndHysteresisPassed:true,currentAndInputOutputMatch:true,browserRetested:false,checks};
fs.writeFileSync(path.join(out,'oled-layout-pixels.json'),JSON.stringify(report,null,2)+'\n');
console.log('PASS '+checks.length+' offline raster states, nice scales/hysteresis, ticks, current totals, arrows, quota and sample event handler');
