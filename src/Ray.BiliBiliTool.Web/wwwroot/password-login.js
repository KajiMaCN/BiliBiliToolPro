const widgets = new Map();
let loader;

function loadSdk() {
    if (window.initGeetest) return Promise.resolve();
    if (loader) return loader;
    loader = new Promise((resolve, reject) => {
        const script = document.createElement("script");
        let finished = false;
        const timeout = setTimeout(() => finish(new Error("captcha load failed")), 20000);
        function finish(error) {
            if (finished) return;
            finished = true;
            clearTimeout(timeout);
            script.onload = script.onerror = null;
            if (error) { script.remove(); loader = null; reject(error); }
            else resolve();
        }
        script.src = "https://static.geetest.com/static/js/gt.0.5.0.js";
        script.async = true;
        script.onload = () => finish(window.initGeetest ? null : new Error("captcha unavailable"));
        script.onerror = () => finish(new Error("captcha load failed"));
        document.head.appendChild(script);
    });
    return loader;
}

export async function initialize(id, configuration) {
    destroy(id);
    const state = { widget: null, pending: null, initializing: null, destroyed: false, ready: false, consumed: false };
    widgets.set(id, state);
    await loadSdk();
    if (state.destroyed) return;
    await new Promise((resolve, reject) => {
        state.initializing = { resolve, reject, timeout: setTimeout(() => fail(state, new Error("captcha initialization failed")), 20000) };
        try { window.initGeetest({ gt: configuration.gt, challenge: configuration.challenge,
            offline: false, new_captcha: true, product: "bind", https: true, lang: "zh-cn" }, widget => {
            if (state.destroyed) { widget.destroy(); finishInitialization(state); return; }
            state.widget = widget;
            widget.onReady(() => { if (state.destroyed || state.consumed) return; state.ready = true; finishInitialization(state); });
            widget.onSuccess(() => {
                if (state.destroyed || !state.pending) return;
                const result = widget.getValidate();
                if (!result) { fail(state, new Error("captcha verification failed")); return; }
                state.ready = false;
                settle(state, { challenge: result.geetest_challenge, validate: result.geetest_validate,
                    seccode: result.geetest_seccode, imageCode: "", cancelled: false });
            });
            widget.onClose(() => { if (state.destroyed) return; state.ready = false; settle(state, { cancelled: true }); });
            widget.onError(() => fail(state, new Error("captcha verification failed")));
        }); } catch { fail(state, new Error("captcha initialization failed")); }
    });
}

function finishInitialization(state, error) {
    if (!state.initializing) return;
    const pending = state.initializing;
    state.initializing = null;
    clearTimeout(pending.timeout);
    if (error) pending.reject(error);
    else pending.resolve();
}

function settle(state, result, error) {
    if (!state.pending) return;
    const pending = state.pending;
    state.pending = null;
    clearTimeout(pending.timeout);
    if (error) pending.reject(error);
    else pending.resolve(result);
}

function fail(state, error) {
    if (state.destroyed) return;
    state.destroyed = true;
    state.ready = false;
    finishInitialization(state, error);
    settle(state, null, error);
    destroyWidget(state);
}

function destroyWidget(state) {
    const widget = state.widget;
    state.widget = null;
    try { widget?.destroy(); } catch { }
}

export function verify(id) {
    const state = widgets.get(id);
    if (!state?.ready || state.destroyed) return Promise.reject(new Error("captcha not ready"));
    if (state.pending) return Promise.reject(new Error("captcha already pending"));
    state.consumed = true;
    return new Promise((resolve, reject) => {
        state.pending = { resolve, reject, timeout: setTimeout(() => { state.ready = false; settle(state, { cancelled: true }); }, 90000) };
        try { state.widget.verify(); }
        catch { fail(state, new Error("captcha verification failed")); }
    });
}

export function destroy(id) {
    const state = widgets.get(id);
    if (!state) return;
    state.destroyed = true;
    finishInitialization(state);
    settle(state, { cancelled: true });
    destroyWidget(state);
    widgets.delete(id);
}
