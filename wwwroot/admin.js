"use strict";

const historyPanel = document.querySelector("#hit-history");
const feedback = document.querySelector("#feedback");
const pending = new WeakMap();
let feedbackTimer;

function notify(message) {
    feedback.textContent = message;
    clearTimeout(feedbackTimer);
    feedbackTimer = setTimeout(() => { feedback.textContent = ""; }, 3000);
}

async function loadFragment(container, handler, parameters) {
    pending.get(container)?.abort();
    const controller = new AbortController();
    pending.set(container, controller);
    const url = new URL(location.pathname, location.origin);
    url.search = new URLSearchParams({ handler, ...parameters });
    container.setAttribute("aria-busy", "true");
    try {
        const response = await fetch(url, { signal: controller.signal, cache: "no-store" });
        if (response.redirected) {
            location.assign(response.url);
            return false;
        }
        if (!response.ok) throw new Error();
        const html = await response.text();
        if (controller.signal.aborted) return false;
        container.innerHTML = html;
        feedback.textContent = "";
        return true;
    } catch (error) {
        if (error.name !== "AbortError") {
            if (container.closest("dialog")) container.textContent = "加载失败，请关闭后重试。";
            else notify("加载失败，请重试。");
        }
        return false;
    } finally {
        if (pending.get(container) === controller) container.removeAttribute("aria-busy");
    }
}

function showDetails(kind, id, page = 1) {
    const dialog = document.querySelector("#" + kind + "-dialog");
    const container = dialog.querySelector(".dialog-content");
    if (!dialog.open) {
        container.textContent = "加载中…";
        dialog.showModal();
    }
    return loadFragment(container, kind === "key" ? "Key" : "Hit", { id, p: page });
}

function resetConfirmation(button) {
    button.value = "false";
    button.textContent = "吊销";
}

document.addEventListener("focusout", event => {
    if (event.target.matches("[data-revoke]")) resetConfirmation(event.target);
});

document.addEventListener("click", async event => {
    const button = event.target.closest("button");
    if (button?.matches("[data-revoke]") && button.value !== "true") {
        event.preventDefault();
        button.value = "true";
        button.textContent = "确认?";
        return;
    }
    if (button?.matches("[data-refresh], [data-page]")) {
        const page = button.dataset.page || historyPanel.firstElementChild.dataset.historyPage;
        if (button.dataset.keyPage) {
            await showDetails("key", button.dataset.keyPage, page);
        } else if (await loadFragment(historyPanel, "History", { p: page })) {
            document.querySelector("#hit-count").textContent = historyPanel.firstElementChild.dataset.count;
            notify("调用记录已刷新");
        }
        return;
    }
    if (!button && window.getSelection()?.toString()) return;
    const action = button ?? event.target.closest("[data-open-row]")?.querySelector("[data-key], [data-hit]");
    if (action?.dataset.key) await showDetails("key", action.dataset.key);
    if (action?.dataset.hit) await showDetails("hit", action.dataset.hit);
});
