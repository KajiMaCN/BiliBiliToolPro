import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const source = readFileSync(new URL('../../../src/Ray.BiliBiliTool.Web/wwwroot/password-login.js', import.meta.url), 'utf8');
let sequence = 0;
async function setup(t, { ready = true, loaded = true, callback = true } = {}) {
    const originalTimeout = globalThis.setTimeout;
    const originalClear = globalThis.clearTimeout;
    const timers = new Map();
    const instances = [];
    const scripts = [];
    globalThis.setTimeout = (fn, delay) => { const id = ++sequence; timers.set(id, { fn, delay }); return id; };
    globalThis.clearTimeout = id => timers.delete(id);
    const sdk = (config, receive) => {
        const events = {};
        const widget = {
            config, events, calls: 0, destroys: 0, receive,
            onReady(fn) { events.ready = fn; if (ready) queueMicrotask(fn); },
            onSuccess(fn) { events.success = fn; }, onClose(fn) { events.close = fn; }, onError(fn) { events.error = fn; },
            verify() { this.calls++; }, destroy() { this.destroys++; },
            getValidate() { return { geetest_challenge: config.challenge, geetest_validate: 'synthetic', geetest_seccode: 'synthetic|jordan' }; },
        };
        instances.push(widget);
        if (callback) receive(widget);
    };
    globalThis.window = loaded ? { initGeetest: sdk } : {};
    globalThis.document = { createElement: () => ({ remove() {} }), head: { appendChild(script) { scripts.push(script); } } };
    const module = await import('data:text/javascript;base64,' + Buffer.from(source + `\n// test ${++sequence}`).toString('base64'));
    t.after(() => {
        for (const id of ['a', 'b']) module.destroy(id);
        globalThis.setTimeout = originalTimeout;
        globalThis.clearTimeout = originalClear;
        delete globalThis.window; delete globalThis.document;
    });
    return { module, instances, scripts, sdk, flush: () => new Promise(resolve => setImmediate(resolve)),
        expire(delay) { const entry = [...timers].find(([, timer]) => timer.delay === delay); assert.ok(entry); timers.delete(entry[0]); entry[1].fn(); } };
}
const configuration = challenge => ({ gt: 'synthetic', challenge });

test('verification waits for readiness and successful proof is single use', async t => {
    const s = await setup(t, { ready: false });
    const initializing = s.module.initialize('a', configuration('fresh'));
    await s.flush();
    await assert.rejects(s.module.verify('a'), /not ready/);
    s.instances[0].events.ready(); await initializing;
    const verified = s.module.verify('a');
    await assert.rejects(s.module.verify('a'), /already pending/);
    s.instances[0].events.success();
    assert.equal((await verified).challenge, 'fresh');
    s.instances[0].events.ready();
    await assert.rejects(s.module.verify('a'), /not ready/);
});

test('closing verification requires a fresh instance', async t => {
    const s = await setup(t);
    await s.module.initialize('a', configuration('old'));
    const pending = s.module.verify('a'); s.instances[0].events.close();
    assert.deepEqual(await pending, { cancelled: true });
    await assert.rejects(s.module.verify('a'), /not ready/);
    await s.module.initialize('a', configuration('new'));
    assert.equal(s.instances[0].destroys, 1);
    const next = s.module.verify('a'); s.instances[1].events.success();
    assert.equal((await next).challenge, 'new');
});

test('SDK failure invalidates the instance and cleanup destroys it only once', async t => {
    const s = await setup(t);
    await s.module.initialize('a', configuration('old'));
    const failed = assert.rejects(s.module.verify('a'), /verification failed/);
    s.instances[0].events.error(); await failed;
    s.module.destroy('a'); s.module.destroy('a');
    assert.equal(s.instances[0].destroys, 1);
    await s.module.initialize('a', configuration('new'));
    const pending = s.module.verify('a'); s.instances[1].events.success();
    assert.equal((await pending).challenge, 'new');
});

test('initialization timeout rejects and late readiness cannot revive the instance', async t => {
    const s = await setup(t, { ready: false });
    const rejected = assert.rejects(s.module.initialize('a', configuration('old')), /initialization failed/);
    await s.flush(); s.expire(20000); await rejected;
    s.instances[0].events.ready();
    await assert.rejects(s.module.verify('a'), /not ready/);
    s.module.destroy('a'); assert.equal(s.instances[0].destroys, 1);
});

test('late SDK creation after cancellation is immediately destroyed', async t => {
    const s = await setup(t, { callback: false });
    const pending = s.module.initialize('a', configuration('old'));
    await s.flush(); s.module.destroy('a'); await pending;
    const widget = s.instances[0]; widget.receive(widget);
    assert.equal(widget.destroys, 1);
    await assert.rejects(s.module.verify('a'), /not ready/);
});

test('replacing an active instance cancels its pending result and ignores its callbacks', async t => {
    const s = await setup(t);
    await s.module.initialize('a', configuration('old'));
    const old = s.module.verify('a');
    await s.module.initialize('a', configuration('new'));
    assert.deepEqual(await old, { cancelled: true });
    const fresh = s.module.verify('a'); s.instances[0].events.success();
    s.instances[1].events.success(); assert.equal((await fresh).challenge, 'new');
});

test('verification timeout cancels and ignores late success', async t => {
    const s = await setup(t);
    await s.module.initialize('a', configuration('old'));
    const pending = s.module.verify('a'); s.expire(90000);
    assert.deepEqual(await pending, { cancelled: true });
    s.instances[0].events.success();
    await assert.rejects(s.module.verify('a'), /not ready/);
});

test('independent dialogs receive their own proof', async t => {
    const s = await setup(t);
    await Promise.all([s.module.initialize('a', configuration('first')), s.module.initialize('b', configuration('second'))]);
    const a = s.module.verify('a'), b = s.module.verify('b');
    s.instances[1].events.success(); s.instances[0].events.success();
    assert.equal((await a).challenge, 'first'); assert.equal((await b).challenge, 'second');
});

test('missing SDK proof fails verification', async t => {
    const s = await setup(t);
    await s.module.initialize('a', configuration('old'));
    s.instances[0].getValidate = () => null;
    const rejected = assert.rejects(s.module.verify('a'), /verification failed/);
    s.instances[0].events.success(); await rejected;
});

test('synchronous SDK verification errors fail and destroy the instance', async t => {
    const s = await setup(t);
    await s.module.initialize('a', configuration('old'));
    s.instances[0].verify = () => { throw Error('synthetic'); };
    await assert.rejects(s.module.verify('a'), /verification failed/);
    assert.equal(s.instances[0].destroys, 1);
});

test('SDK download failure can retry', async t => {
    const s = await setup(t, { loaded: false });
    const rejected = assert.rejects(s.module.initialize('a', configuration('old')), /load failed/);
    s.scripts[0].onerror(); await rejected;
    const next = s.module.initialize('a', configuration('new'));
    window.initGeetest = s.sdk; s.scripts[1].onload(); await next;
    const pending = s.module.verify('a'); s.instances[0].events.success();
    assert.equal((await pending).challenge, 'new');
});

test('SDK download timeout rejects and permits a new download', async t => {
    const s = await setup(t, { loaded: false });
    const rejected = assert.rejects(s.module.initialize('a', configuration('old')), /load failed/);
    const lateLoad = s.scripts[0].onload;
    s.expire(20000); await rejected;
    const next = s.module.initialize('a', configuration('new'));
    window.initGeetest = s.sdk; lateLoad(); s.scripts[1].onload(); await next;
    assert.equal(s.instances.length, 1);
});
