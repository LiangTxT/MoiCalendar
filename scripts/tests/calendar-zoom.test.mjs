import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarZoom.js', import.meta.url), 'utf8');
function setup(saved) {
    let height = 52, next = 0, cancels = 0;
    const handlers = new Map(), frames = new Map(), storage = new Map(saved ? [['moicalendar.timeGridHourHeight.v1',String(saved)]] : []);
    const surface = {classList:{contains:()=>false}};
    const grid = {get clientHeight(){return height * 24;},getBoundingClientRect:()=>({top:120-timeline.scrollTop})};
    const timeline = {scrollTop:300,style:{setProperty:(name,value)=>{height=parseFloat(value);}},
        querySelector:()=>grid,closest:()=>surface,getBoundingClientRect:()=>({top:100}),
        addEventListener:(name,handler)=>handlers.set(name,handler),removeEventListener:name=>handlers.delete(name)};
    const window = {moicalendarInteraction:{cancelActiveInteraction:()=>cancels++}};
    vm.runInNewContext(source,{window,Math,Number,localStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v)},
        requestAnimationFrame:fn=>{frames.set(++next,fn);return next;},cancelAnimationFrame:id=>frames.delete(id)});
    window.moicalendarZoom.attach(timeline);
    const fire = (name,gap=100,count=2,cancelable=true) => {
        const event={cancelable,touches:Array.from({length:count},(_,i)=>({clientX:100,clientY:300+(i-.5)*gap})),preventDefault(){this.prevented=true;}};
        handlers.get(name)?.(event);return event;
    };
    const flush=()=>{const list=[...frames.values()];frames.clear();list.forEach(fn=>fn());};
    return {timeline,window,fire,flush,storage,handlers,height:()=>height,cancels:()=>cancels};
}
test('双指缩放只改变时间高度，并保持中心时刻不跳动',()=>{
    const f=setup();
    f.fire('touchstart');
    f.fire('touchmove',200);f.flush();
    assert.equal(f.height(),104);
    assert.equal(f.timeline.scrollTop,780);
    assert.equal(f.cancels(),1);
    f.fire('touchend',100,0);
    assert.equal([...f.storage.values()][0],'104');
});
test('滚动惯性期间不可取消的触摸不调用 preventDefault，缩放仍可结束并继续单指滚动',()=>{
    const f=setup();
    assert.equal(f.fire('touchstart',100,2,false).prevented,undefined);
    assert.equal(f.fire('touchmove',150,2,false).prevented,undefined);
    f.flush();
    assert.equal(f.height(),78);
    f.fire('touchend',100,0);
    assert.equal(f.fire('touchmove',100,1).prevented,undefined);
    assert.equal(f.handlers.size,4);
});
test('缩放上限下限，持久化恢复与不可用缓存安全默认',()=>{
    const f=setup(80);assert.equal(f.height(),80);
    f.fire('touchstart');f.fire('touchmove',10000);f.flush();assert.equal(f.height(),208);
    f.fire('touchmove',1);f.flush();assert.equal(f.height(),32);
    assert.equal(setup('bad').height(),52);
});
test('单指保留滚动，三指和取消结束缩放，卸载清理监听和待绘制帧',()=>{
    const f=setup();
    assert.equal(f.fire('touchstart',100,1).prevented,undefined);
    assert.equal(f.fire('touchmove',100,1).prevented,undefined);
    f.fire('touchstart');f.fire('touchmove',150);
    f.fire('touchstart',100,3);f.flush();
    assert.equal(f.height(),78);
    assert.equal(f.fire('touchstart').prevented,undefined);
    f.fire('touchcancel',100,0);
    f.fire('touchstart');
    f.window.moicalendarZoom.dispose(f.timeline);
    assert.equal(f.handlers.size,0);
});
