"use strict";

// ═══════════════════════ 共用工具 ═══════════════════════

const $view = document.getElementById("view");
const state = {
  tab: "scan",
  status: null,
  categories: [],
  month: null,          // "2026-11"，帳本與報表共用
  cards: [],            // 掃描後等待確認的發票
  showManual: false,
  live: null,           // 即時掃描狀態
  editingFixed: null,   // 正在編輯的固定支出（null = 沒有）
  confirmDelete: null,  // 兩段式刪除的目標
};
let cardSeq = 0;

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const money = (n) => (n == null || n === "" ? "—" : Number(n).toLocaleString("zh-TW", { maximumFractionDigits: 2 }));
const todayStr = () => toDateStr(new Date());
function toDateStr(d) { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; }
const monthLabel = (m) => { const [y, mm] = m.split("-"); return `${y} 年 ${Number(mm)} 月`; };
function shiftMonth(m, delta) { const [y, mm] = m.split("-").map(Number); const d = new Date(y, mm - 1 + delta, 1); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`; }

async function api(method, url, body, isForm) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    if (isForm) opts.body = body;
    else { opts.body = JSON.stringify(body); opts.headers["Content-Type"] = "application/json"; }
  }
  const res = await fetch(url, opts);
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { error: text }; }
  if (!res.ok) {
    const err = new Error(data?.error || data?.errors?.join("、") || data?.detail || data?.title || `連線錯誤（${res.status}）`);
    err.status = res.status; err.data = data;
    throw err;
  }
  return data;
}

let toastTimer;
function toast(msg, ms = 2600) {
  const t = document.getElementById("toast");
  t.textContent = msg; t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), ms);
}

// 期別工具：key = 民國年 3 碼 + 末月 2 碼，例如 11512
function periodFromDate(d) { const m = d.getMonth() + 1; const end = m % 2 === 0 ? m : m + 1; return `${String(d.getFullYear() - 1911).padStart(3, "0")}${String(end).padStart(2, "0")}`; }
function periodDisplay(key) { if (!key) return "未知期別"; const y = Number(key.slice(0, 3)); const e = Number(key.slice(3)); return `${y}年${String(e - 1).padStart(2, "0")}-${String(e).padStart(2, "0")}月`; }
function prevPeriod(key) { let y = Number(key.slice(0, 3)); let e = Number(key.slice(3)) - 2; if (e < 2) { e = 12; y -= 1; } return `${String(y).padStart(3, "0")}${String(e).padStart(2, "0")}`; }
function drawDate(key) { const y = Number(key.slice(0, 3)) + 1911; const e = Number(key.slice(3)); return new Date(y, e, 25); } // 末月的下個月 25 日
function periodList(n = 8) { const list = []; let k = periodFromDate(new Date()); for (let i = 0; i < n; i++) { list.push(k); k = prevPeriod(k); } return list; }
function latestDrawnPeriod() { let k = periodFromDate(new Date()); const now = new Date(); while (drawDate(k) > now) k = prevPeriod(k); return k; }

const expenseCategories = () => state.categories.filter((c) => !c.isIncome).map((c) => c.name);
const incomeCategories = () => state.categories.filter((c) => c.isIncome).map((c) => c.name);
async function loadCategories() { state.categories = await api("GET", "/api/categories"); }

const sourceLabel = { invoice: "發票", fixed: "固定", manual: "手動", lottery: "中獎" };
const ruleLabel = { UserItemRule: "依你之前的選擇", UserStoreRule: "依你之前對這間店的選擇", Keyword: "依品名關鍵字", Industry: "依店家行業別", None: "" };

// ═══════════════════════ 分頁切換 ═══════════════════════

const views = { scan: renderScan, ledger: renderLedger, lottery: renderLottery, fixed: renderFixed, report: renderReport };

function setTab(tab) {
  if (state.live && tab !== "scan") stopLive();
  state.tab = tab;
  state.confirmDelete = null;
  try { localStorage.setItem("tab", tab); } catch { /* 不能存沒關係 */ }
  document.querySelectorAll(".tab").forEach((b) => b.setAttribute("aria-current", b.dataset.tab === tab ? "page" : "false"));
  render();
  window.scrollTo({ top: 0 });
}

async function render() {
  try { await views[state.tab](); }
  catch (e) { $view.innerHTML = `<div class="notice error">${esc(e.message)}</div>`; }
}

document.querySelectorAll(".tab").forEach((b) => b.addEventListener("click", () => setTab(b.dataset.tab)));

// 用事件代理處理畫面上的按鈕：data-action="名稱"
const actions = {};
$view.addEventListener("click", (e) => {
  const el = e.target.closest("[data-action]");
  if (!el || el.disabled) return;
  const fn = actions[el.dataset.action];
  if (fn) { e.preventDefault(); fn(el, e); }
});
$view.addEventListener("change", (e) => {
  const el = e.target.closest("[data-change]");
  if (el && actions[el.dataset.change]) actions[el.dataset.change](el, e);
});
$view.addEventListener("submit", (e) => {
  const form = e.target.closest("form[data-submit]");
  if (form) { e.preventDefault(); actions[form.dataset.submit]?.(form, e); }
});

// ═══════════════════════ 掃描 ═══════════════════════

async function renderScan() {
  let upcoming = [];
  try { upcoming = await api("GET", "/api/fixed-expenses/upcoming"); } catch { /* 提醒失敗不影響掃描 */ }
  const liveSupported = !!(navigator.mediaDevices?.getUserMedia) && window.isSecureContext;

  $view.innerHTML = `
    ${upcoming.map((u) => `<div class="notice gold">⏰ ${esc(u.expense.name)} ${money(u.expense.amount)} 元將在 ${u.daysLeft === 0 ? "今天" : u.daysLeft === 1 ? "明天" : `${u.daysLeft} 天後`}（${esc(u.dueDate)}）自動記帳</div>`).join("")}
    <section class="card">
      <div class="section-title"><h2>拍發票記帳</h2><span class="muted small">電子・收銀機・手開發票</span></div>
      <div class="scan-actions">
        <label class="btn primary big">
          <svg viewBox="0 0 24 24"><path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/></svg>
          拍照
          <input id="file-camera" type="file" accept="image/*" capture="environment" hidden>
        </label>
        <label class="btn">
          <svg viewBox="0 0 24 24"><rect x="4" y="5" width="16" height="14" rx="2"/><path d="M4 16l5-5 4 4 3-3 4 4"/></svg>
          從相簿匯入
          <input id="file-album" type="file" accept="image/*" multiple hidden>
        </label>
        <button class="btn" data-action="toggleLive" ${liveSupported ? "" : "disabled"}>
          <svg viewBox="0 0 24 24"><rect x="3" y="6" width="13" height="12" rx="2"/><path d="M16 10l5-3v10l-5-3"/></svg>
          ${state.live ? "停止掃描" : "即時掃描"}
        </button>
      </div>
      ${liveSupported ? "" : `<p class="muted small">即時掃描需要 HTTPS 連線；目前可以用「拍照」或「從相簿匯入」。</p>`}
      <div id="live-slot"></div>
      <button class="btn ghost" data-action="toggleManual">＋ 沒有發票？手動記一筆</button>
      ${state.showManual ? manualFormHtml() : ""}
    </section>
    <div id="cards" class="stack">${state.cards.map(cardHtml).join("")}</div>
  `;

  for (const id of ["file-camera", "file-album"]) {
    document.getElementById(id).addEventListener("change", (e) => {
      const files = [...e.target.files];
      e.target.value = ""; // 清掉選擇，同一張照片再選一次也會觸發
      handleFiles(files);
    });
  }
  if (state.live) attachLiveView();
}

function manualFormHtml() {
  const cats = expenseCategories();
  return `
    <form class="stack" data-submit="saveManual">
      <div class="grid-2">
        <label class="field">收支
          <select name="kind" data-change="manualKind">
            <option value="expense">支出</option><option value="income">收入</option>
          </select>
        </label>
        <label class="field">日期<input type="date" name="date" value="${todayStr()}" required></label>
        <label class="field">金額<input type="number" name="amount" min="1" step="1" inputmode="numeric" required placeholder="例如 50"></label>
        <label class="field">分類
          <select name="category" id="manual-category">${cats.map((c) => `<option>${esc(c)}</option>`).join("")}<option value="__new">＋ 自行輸入…</option></select>
        </label>
      </div>
      <label class="field" id="manual-new-wrap" hidden>新分類名稱<input name="newCategory" placeholder="例如：寵物"></label>
      <label class="field">備註<input name="note" placeholder="例如：夜市雞排"></label>
      <div class="confirm-bar"><button class="btn primary" type="submit">記一筆</button></div>
    </form>`;
}

actions.toggleManual = () => { state.showManual = !state.showManual; renderScan(); };
actions.manualKind = (el) => {
  const sel = document.getElementById("manual-category");
  const cats = el.value === "income" ? incomeCategories() : expenseCategories();
  sel.innerHTML = cats.map((c) => `<option>${esc(c)}</option>`).join("") + `<option value="__new">＋ 自行輸入…</option>`;
};
$view.addEventListener("change", (e) => {
  if (e.target.id === "manual-category") document.getElementById("manual-new-wrap").hidden = e.target.value !== "__new";
});
actions.saveManual = async (form) => {
  const f = new FormData(form);
  let category = f.get("category");
  if (category === "__new") category = (f.get("newCategory") || "").trim();
  if (!category) { toast("請輸入分類名稱"); return; }
  try {
    await api("POST", "/api/transactions", { date: f.get("date"), kind: f.get("kind"), amount: Number(f.get("amount")), category, note: f.get("note") || null });
    await loadCategories();
    toast(`已記一筆 ${category} ${money(f.get("amount"))} 元`);
    state.showManual = false;
    renderScan();
  } catch (e) { toast(e.message); }
};

// 照片太大時先在手機端縮小，上傳比較快
async function shrink(file, maxSide = 2000) {
  try {
    const bmp = await createImageBitmap(file);
    const scale = Math.min(1, maxSide / Math.max(bmp.width, bmp.height));
    if (scale === 1 && file.size < 2.5e6) return file;
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(bmp.width * scale); canvas.height = Math.round(bmp.height * scale);
    canvas.getContext("2d").drawImage(bmp, 0, 0, canvas.width, canvas.height);
    return await new Promise((ok) => canvas.toBlob((b) => ok(b || file), "image/jpeg", 0.9));
  } catch { return file; }
}

async function handleFiles(fileList) {
  const files = [...(fileList || [])];
  for (const file of files) {
    const card = { id: ++cardSeq, status: "loading", fileName: file.name };
    state.cards.unshift(card);
    renderCards();
    try {
      const blob = await shrink(file);
      await recognizeInto(card, blob);
    } catch (e) {
      card.status = "error"; card.error = e.message;
    }
    renderCards();
  }
}

async function recognize(blob) {
  const form = new FormData();
  form.append("photo", blob, "invoice.jpg");
  return api("POST", "/api/recognize", form, true);
}

async function recognizeInto(card, blob) {
  const r = await recognize(blob);
  card.result = r;
  card.status = "ready";
  const inv = r.invoice;
  card.form = {
    type: inv.type === "Unknown" ? "CashRegister" : inv.type,
    number: inv.number || "",
    periodKey: inv.period?.key || "",
    date: inv.date || todayStr(),
    total: inv.total ?? "",
    sellerTaxId: inv.sellerTaxId,
    randomCode: inv.randomCode,
    items: inv.items,
    lines: r.lines.map((l) => ({ name: l.name, amount: l.amount, category: l.category, suggested: l.category, source: l.source })),
    rememberStore: !!inv.sellerTaxId,
    force: false,
  };
  if (!card.form.periodKey && card.form.date) card.form.periodKey = periodFromDate(new Date(card.form.date + "T00:00"));
}

function renderCards() {
  const box = document.getElementById("cards");
  if (box) box.innerHTML = state.cards.map(cardHtml).join("");
}

function cardHtml(card) {
  if (card.status === "loading") return `<section class="card"><div class="row"><div class="spinner"></div><span>辨識中…</span></div></section>`;
  if (card.status === "error") return `<section class="card"><div class="notice error">辨識失敗：${esc(card.error)}</div><div class="confirm-bar"><button class="btn small" data-action="dismissCard" data-id="${card.id}">關閉</button></div></section>`;
  if (card.status === "saved") return `<section class="card"><div class="notice good">✔ ${esc(card.savedMessage)}</div>${card.lotteryMessage ? `<div class="notice gold">${esc(card.lotteryMessage)}</div>` : ""}<div class="confirm-bar"><button class="btn small" data-action="dismissCard" data-id="${card.id}">完成</button></div></section>`;

  const r = card.result, inv = r.invoice, f = card.form;
  const periods = [...new Set([f.periodKey, ...periodList(8)].filter(Boolean))];
  const cats = expenseCategories();
  const singleLine = f.lines.length === 1;
  const warnings = [...inv.warnings];
  if (!r.found) warnings.unshift("沒有辨識到發票資料，請手動輸入號碼和金額");

  return `
  <section class="card" data-card="${card.id}">
    <div class="invoice-head">
      <div class="stack" style="gap:4px">
        <span class="type-chip">${esc(inv.typeName)}${r.engine === "mock" ? "・示範資料" : ""}</span>
        <span class="invoice-number">${esc(f.number ? f.number.replace(/^([A-Z]{2})(\d{8})$/, "$1-$2") : "號碼待輸入")}</span>
        ${inv.storeName || inv.sellerTaxId ? `<span class="muted small">${esc(inv.storeName || "")} ${inv.sellerTaxId ? `統編 ${esc(inv.sellerTaxId)}` : ""}</span>` : ""}
      </div>
      <button class="btn small ghost" data-action="dismissCard" data-id="${card.id}" aria-label="取消這張">取消</button>
    </div>

    ${r.duplicate ? `<div class="notice error">這張發票已經記過了</div>` : ""}
    ${r.fixedMatch ? `<div class="notice warn">這筆金額和固定支出「${esc(r.fixedMatch)}」相同，可能已經記過</div>` : ""}
    ${warnings.length ? `<div class="notice warn"><ul>${warnings.map((w) => `<li>${esc(w)}</li>`).join("")}</ul></div>` : ""}

    <div class="grid-2">
      <label class="field">發票號碼<input class="num" value="${esc(f.number)}" maxlength="11" autocapitalize="characters" placeholder="AB12345678" data-card-field="number" data-id="${card.id}"></label>
      <label class="field">期別
        <select data-card-field="periodKey" data-id="${card.id}">${periods.map((p) => `<option value="${p}" ${p === f.periodKey ? "selected" : ""}>${periodDisplay(p)}</option>`).join("")}</select>
      </label>
      <label class="field">日期<input type="date" value="${esc(f.date)}" data-card-field="date" data-id="${card.id}"></label>
      <label class="field">總金額<input class="num" type="number" min="1" step="1" inputmode="numeric" value="${esc(f.total)}" placeholder="請輸入" data-card-field="total" data-id="${card.id}"></label>
    </div>

    <div class="stack">
      <h3>分類${f.lines.length > 1 ? `（依品項拆開記）` : ""}</h3>
      <div class="lines">
        ${f.lines.map((l, i) => `
          <div class="line ${l.category ? "" : "unset"}">
            <span class="name">${esc(l.name)}</span>
            <span class="amount">${money(singleLine ? f.total || l.amount : l.amount)} 元</span>
            <div class="cat-picker" role="group" aria-label="${esc(l.name)}的分類">
              ${[...new Set([...cats, ...(l.category && !cats.includes(l.category) ? [l.category] : [])])].map((c) => `<button type="button" class="cat-btn" aria-pressed="${c === l.category}" data-action="pickCat" data-id="${card.id}" data-line="${i}" data-cat="${esc(c)}">${esc(c)}</button>`).join("")}
              <button type="button" class="cat-btn add" data-action="newCat" data-id="${card.id}" data-line="${i}">＋ 自行輸入</button>
            </div>
            ${l.category ? (l.category === l.suggested && ruleLabel[l.source] ? `<span class="source-note">自動分類：${ruleLabel[l.source]}</span>` : "") : `<span class="source-note">判斷不出來，請選一個分類或自行輸入</span>`}
          </div>`).join("")}
      </div>
    </div>

    ${f.sellerTaxId ? `<label class="check"><input type="checkbox" ${f.rememberStore ? "checked" : ""} data-card-field="rememberStore" data-id="${card.id}"> 記住這間店的分類，下次自動套用</label>` : ""}
    ${card.saveError ? `<div class="notice error">${esc(card.saveError)}</div>` : ""}
    <div class="confirm-bar">
      ${card.needsForce ? `<button class="btn" data-action="saveCard" data-id="${card.id}" data-force="1">仍要記帳</button>` : ""}
      <button class="btn primary" data-action="saveCard" data-id="${card.id}" ${r.duplicate ? "disabled" : ""}>確認記帳</button>
    </div>
  </section>`;
}

const findCard = (id) => state.cards.find((c) => c.id === Number(id));

$view.addEventListener("input", (e) => {
  const el = e.target.closest("[data-card-field]");
  if (!el) return;
  const card = findCard(el.dataset.id);
  if (!card?.form) return;
  const key = el.dataset.cardField;
  if (key === "rememberStore") card.form.rememberStore = el.checked;
  else if (key === "total") card.form.total = el.value === "" ? "" : Number(el.value);
  else if (key === "number") card.form.number = el.value.toUpperCase().replace(/[^A-Z0-9]/g, "");
  else card.form[key] = el.value;
  if (key === "date" && el.value) card.form.periodKey = periodFromDate(new Date(el.value + "T00:00"));
});
$view.addEventListener("change", (e) => {
  const el = e.target.closest("[data-card-field]");
  if (el && ["periodKey", "date", "rememberStore"].includes(el.dataset.cardField)) renderCards();
});

actions.dismissCard = (el) => { state.cards = state.cards.filter((c) => c.id !== Number(el.dataset.id)); renderCards(); };
actions.pickCat = (el) => {
  const card = findCard(el.dataset.id);
  card.form.lines[Number(el.dataset.line)].category = el.dataset.cat;
  renderCards();
};
actions.newCat = (el) => {
  const card = findCard(el.dataset.id);
  const line = card.form.lines[Number(el.dataset.line)];
  const picker = el.parentElement;
  picker.innerHTML = `
    <input class="inline" placeholder="新分類名稱，例如：寵物" aria-label="新分類名稱" style="flex:1;min-width:10em">
    <button type="button" class="btn small primary">加入</button>
    <button type="button" class="btn small">取消</button>`;
  const input = picker.querySelector("input");
  input.focus();
  const [ok, cancel] = picker.querySelectorAll("button");
  const commit = () => {
    const name = input.value.trim();
    if (!name) { input.focus(); return; }
    line.category = name;
    if (!state.categories.some((c) => c.name === name)) state.categories.push({ name, budget: null, isIncome: false });
    renderCards();
  };
  ok.addEventListener("click", commit);
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); commit(); } });
  cancel.addEventListener("click", renderCards);
};

actions.saveCard = async (el) => {
  const card = findCard(el.dataset.id);
  const f = card.form;
  card.saveError = null;
  const total = Number(f.total);
  if (!/^[A-Z]{2}\d{8}$/.test(f.number)) card.saveError = "發票號碼要是 2 個英文字＋8 位數字";
  else if (!(total > 0)) card.saveError = "請輸入總金額";
  else if (f.lines.some((l) => !l.category)) card.saveError = "還有品項沒選分類";
  if (card.saveError) { renderCards(); return; }

  const lines = f.lines.length === 1
    ? [{ category: f.lines[0].category, amount: total, note: f.lines[0].name === "整張發票" ? null : f.lines[0].name }]
    : f.lines.map((l) => ({ category: l.category, amount: l.amount, note: l.name }));
  const learn = {};
  for (const l of f.lines) if (l.name !== "整張發票" && l.category !== l.suggested) learn[l.name] = l.category;

  try {
    const res = await api("POST", "/api/invoices", {
      type: f.type, number: f.number, periodKey: f.periodKey || null, date: f.date, total,
      sellerTaxId: f.sellerTaxId || null, randomCode: f.randomCode || null,
      items: (f.items || []).map((i) => ({ name: i.name, quantity: i.quantity, unitPrice: i.unitPrice })),
      lines, rememberStore: !!f.rememberStore, learnItemCategories: learn, force: el.dataset.force === "1",
    });
    card.status = "saved";
    card.savedMessage = `已記帳：${f.number.replace(/^([A-Z]{2})/, "$1-")} 共 ${money(total)} 元`;
    card.lotteryMessage = res.lottery?.status === "Won" || res.lottery?.status === "WonExpired" ? `🎉 ${res.lottery.message}` : res.lottery?.message;
    await loadCategories();
  } catch (e) {
    card.saveError = e.message;
    card.needsForce = e.status === 409 && /固定支出/.test(e.message);
  }
  renderCards();
};

// ── 即時掃描：每秒拍一張畫面送到電腦辨識，讀到發票就停 ──
actions.toggleLive = async () => {
  if (state.live) { stopLive(); renderScan(); return; }
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment", width: { ideal: 1920 } }, audio: false });
    state.live = { stream, busy: false, timer: null };
    renderScan();
  } catch (e) {
    toast("無法開啟鏡頭：" + e.message);
  }
};

function attachLiveView() {
  const slot = document.getElementById("live-slot");
  slot.innerHTML = `<div class="live"><video playsinline muted autoplay></video><div class="frame"></div><div class="hint" id="live-hint">把發票放進框內</div></div>`;
  const video = slot.querySelector("video");
  video.srcObject = state.live.stream;
  clearInterval(state.live.timer);
  state.live.timer = setInterval(() => liveTick(video), 1000);
}

async function liveTick(video) {
  const live = state.live;
  if (!live || live.busy || !video.videoWidth) return;
  live.busy = true;
  try {
    const canvas = document.createElement("canvas");
    const scale = Math.min(1, 1600 / Math.max(video.videoWidth, video.videoHeight));
    canvas.width = video.videoWidth * scale; canvas.height = video.videoHeight * scale;
    canvas.getContext("2d").drawImage(video, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise((ok) => canvas.toBlob(ok, "image/jpeg", 0.85));
    const card = { id: ++cardSeq, status: "loading" };
    await recognizeInto(card, blob);
    if (card.result.found && card.result.invoice.number) {
      stopLive();
      state.cards.unshift(card);
      if (navigator.vibrate) navigator.vibrate(80);
      renderScan();
      toast("讀到發票了，請確認後記帳");
    } else {
      const hint = document.getElementById("live-hint");
      if (hint) hint.textContent = "還沒讀到，請拿穩、靠近一點";
    }
  } catch (e) {
    const hint = document.getElementById("live-hint");
    if (hint) hint.textContent = e.message;
  } finally {
    if (state.live) state.live.busy = false;
  }
}

function stopLive() {
  if (!state.live) return;
  clearInterval(state.live.timer);
  state.live.stream.getTracks().forEach((t) => t.stop());
  state.live = null;
}

// ═══════════════════════ 帳本 ═══════════════════════

function monthSwitchHtml() {
  return `<div class="month-switch">
    <button class="btn small" data-action="prevMonth" aria-label="上個月">‹</button>
    <strong>${monthLabel(state.month)}</strong>
    <button class="btn small" data-action="nextMonth" aria-label="下個月">›</button>
  </div>`;
}
actions.prevMonth = () => { state.month = shiftMonth(state.month, -1); render(); };
actions.nextMonth = () => { state.month = shiftMonth(state.month, 1); render(); };

async function renderLedger() {
  const tx = await api("GET", `/api/transactions?month=${state.month}`);
  const expense = tx.filter((t) => t.kind === "expense").reduce((s, t) => s + t.amount, 0);
  const income = tx.filter((t) => t.kind === "income").reduce((s, t) => s + t.amount, 0);
  const groups = {};
  for (const t of tx) (groups[t.date] ||= []).push(t);

  $view.innerHTML = `
    <section class="card">
      ${monthSwitchHtml()}
      <div class="summary">
        <div><span>支出</span><b>${money(expense)}</b></div>
        <div><span>收入</span><b>${money(income)}</b></div>
        <div><span>結餘</span><b>${money(income - expense)}</b></div>
      </div>
    </section>
    <section class="card">
      <h2>收支明細</h2>
      ${tx.length === 0 ? `<p class="empty">這個月還沒有紀錄。到「掃描」拍一張發票開始記帳吧！</p>` : `
      <div class="list">
        ${Object.entries(groups).map(([date, items]) => `
          <div class="date-group">${esc(date)}</div>
          ${items.map((t) => `
            <div class="list-item">
              <div class="stack" style="gap:2px;min-width:0">
                <span class="title">${esc(t.category)} <span class="chip ${t.source}">${sourceLabel[t.source]}</span></span>
                <span class="sub">${esc([t.note, t.invoiceNumber].filter(Boolean).join("・") || "—")}</span>
              </div>
              <div class="stack" style="gap:2px;align-items:flex-end">
                <span class="value ${t.kind}">${t.kind === "income" ? "+" : "−"}${money(t.amount)}</span>
                ${state.confirmDelete === t.id
                  ? `<button class="btn small danger" data-action="deleteTx" data-id="${t.id}">${t.invoiceId ? "刪除整張發票？" : "確定刪除？"}</button>`
                  : `<button class="btn small ghost" data-action="askDelete" data-id="${t.id}">刪除</button>`}
              </div>
            </div>`).join("")}`).join("")}
      </div>`}
    </section>`;
}
actions.askDelete = (el) => { state.confirmDelete = Number(el.dataset.id); render(); };
actions.deleteTx = async (el) => {
  try { await api("DELETE", `/api/transactions/${el.dataset.id}`); toast("已刪除"); }
  catch (e) { toast(e.message); }
  state.confirmDelete = null;
  render();
};

// ═══════════════════════ 對獎 ═══════════════════════

async function renderLottery() {
  const [rows, numbers] = await Promise.all([api("GET", "/api/lottery"), api("GET", "/api/winning-numbers")]);
  const won = rows.filter((r) => r.status === "Won");
  const sel = state.lotteryPeriod || latestDrawnPeriod();
  const existing = numbers.find((n) => n.period === sel);

  $view.innerHTML = `
    ${won.length ? `<div class="notice gold">🎉 有 ${won.length} 張發票中獎，共 ${money(won.reduce((s, r) => s + r.prizeAmount, 0))} 元，獎金已自動記成收入</div>` : ""}
    <section class="card">
      <h2>發票對獎結果</h2>
      ${rows.length === 0 ? `<p class="empty">還沒有記過發票</p>` : `
      <div class="list">
        ${rows.map((r) => `
          <div class="list-item">
            <div class="stack" style="gap:2px;min-width:0">
              <span class="title num">${esc(r.number)}</span>
              <span class="sub">${esc(r.period)}・${esc(r.date)}・${money(r.total)} 元</span>
              <span class="sub">${esc(r.message)}</span>
            </div>
            <span class="status ${r.status}">${r.status === "Won" || r.status === "WonExpired" ? esc(r.prize) : { NoPrize: "沒中", NotDrawn: "未開獎", WinningNumbersMissing: "缺號碼", InvalidNumber: "號碼錯誤" }[r.status]}</span>
          </div>`).join("")}
      </div>`}
    </section>
    <section class="card">
      <div class="section-title"><h2>輸入中獎號碼</h2><span class="muted small">請依財政部公告輸入</span></div>
      <form class="stack" data-submit="saveWinning">
        <label class="field">期別
          <select name="period" data-change="pickLotteryPeriod">${periodList(8).map((p) => `<option value="${p}" ${p === sel ? "selected" : ""}>${periodDisplay(p)}${drawDate(p) > new Date() ? "（尚未開獎）" : ""}</option>`).join("")}</select>
        </label>
        <div class="grid-2">
          <label class="field">特別獎（8 碼）<input class="num" name="special" inputmode="numeric" maxlength="8" pattern="\\d{8}" required value="${esc(existing?.special)}"></label>
          <label class="field">特獎（8 碼）<input class="num" name="grand" inputmode="numeric" maxlength="8" pattern="\\d{8}" required value="${esc(existing?.grand)}"></label>
        </div>
        <div class="grid-3">
          ${[0, 1, 2].map((i) => `<label class="field">頭獎 ${i + 1}<input class="num" name="first" inputmode="numeric" maxlength="8" pattern="\\d{8}" ${i === 0 ? "required" : ""} value="${esc(existing?.first?.[i])}"></label>`).join("")}
        </div>
        <label class="field">增開六獎（3 碼，沒有就留空，多組用逗號分開）<input class="num" name="sixth" inputmode="numeric" value="${esc(existing?.additionalSixth?.join(","))}"></label>
        <div class="confirm-bar"><button class="btn primary" type="submit">儲存並對獎</button></div>
      </form>
      ${numbers.length ? `<p class="muted small">已輸入：${numbers.map((n) => esc(n.periodDisplay)).join("、")}</p>` : ""}
    </section>`;
}
actions.pickLotteryPeriod = (el) => { state.lotteryPeriod = el.value; renderLottery(); };
actions.saveWinning = async (form) => {
  const f = new FormData(form);
  const period = f.get("period");
  try {
    await api("PUT", `/api/winning-numbers/${period}`, {
      special: f.get("special"), grand: f.get("grand"),
      first: f.getAll("first").filter(Boolean),
      additionalSixth: String(f.get("sixth") || "").split(/[,，\s]+/).filter(Boolean),
    });
    toast(`已儲存 ${periodDisplay(period)} 中獎號碼`);
    renderLottery();
  } catch (e) { toast(e.message, 4000); }
};

// ═══════════════════════ 固定支出 ═══════════════════════

async function renderFixed() {
  const list = await api("GET", "/api/fixed-expenses");
  const active = list.filter((x) => x.active);
  const total = active.reduce((s, x) => s + x.amount, 0);
  const e = state.editingFixed;

  $view.innerHTML = `
    <section class="card">
      <div class="section-title"><h2>我的每月固定支出</h2><button class="btn small primary" data-action="newFixed">＋ 新增</button></div>
      <p class="muted small">房租、通勤月票、訂閱服務等，設定一次，每月扣款日自動記帳，前一天會提醒。</p>
      ${list.length === 0 ? `<p class="empty">還沒有固定支出</p>` : `
      <div class="list">
        ${list.map((x) => `
          <div class="list-item">
            <div class="stack" style="gap:2px;min-width:0">
              <span class="title">${esc(x.name)} ${x.active ? "" : `<span class="chip">已暫停</span>`}</span>
              <span class="sub">每月 ${x.dayOfMonth} 日｜${esc(x.category)}${x.active && x.nextDueDate ? `｜下次 ${esc(x.nextDueDate)}` : ""}</span>
            </div>
            <div class="stack" style="gap:2px;align-items:flex-end">
              <span class="value">${money(x.amount)} 元</span>
              <div class="row" style="gap:4px">
                <button class="btn small ghost" data-action="editFixed" data-id="${x.id}">編輯</button>
                ${state.confirmDelete === `f${x.id}`
                  ? `<button class="btn small danger" data-action="deleteFixed" data-id="${x.id}">確定刪除？</button>`
                  : `<button class="btn small ghost" data-action="askDeleteFixed" data-id="${x.id}">刪除</button>`}
              </div>
            </div>
          </div>`).join("")}
      </div>
      <div class="row spread"><strong>每月合計</strong><strong class="num" style="color:var(--accent)">${money(total)} 元</strong></div>`}
    </section>
    ${e ? fixedFormHtml(e) : ""}`;
  state.fixedCache = list;
}

function fixedFormHtml(e) {
  const cats = expenseCategories();
  return `
    <section class="card">
      <h2>${e.id ? "編輯固定支出" : "新增固定支出"}</h2>
      <form class="stack" data-submit="saveFixed">
        <label class="field">名稱<input name="name" required value="${esc(e.name)}" placeholder="例如：通勤月票"></label>
        <div class="grid-2">
          <label class="field">金額<input name="amount" type="number" min="1" step="1" inputmode="numeric" required value="${esc(e.amount)}"></label>
          <label class="field">每月幾號扣款<input name="dayOfMonth" type="number" min="1" max="31" required value="${esc(e.dayOfMonth || 1)}"></label>
          <label class="field">分類
            <select name="category">${[...new Set([...cats, e.category].filter(Boolean))].map((c) => `<option ${c === e.category ? "selected" : ""}>${esc(c)}</option>`).join("")}</select>
          </label>
          <label class="field">從哪天開始<input name="startDate" type="date" required value="${esc(e.startDate || todayStr())}"></label>
        </div>
        <label class="check"><input type="checkbox" name="active" ${e.active !== false ? "checked" : ""}> 啟用（取消勾選就暫停自動記帳）</label>
        <p class="muted small">29～31 號遇到小月時，會在該月最後一天記帳。開始日期設在過去，會自動補記之前每個月的紀錄。</p>
        <div class="confirm-bar">
          <button class="btn" type="button" data-action="cancelFixed">取消</button>
          <button class="btn primary" type="submit">儲存</button>
        </div>
      </form>
    </section>`;
}
actions.newFixed = () => { state.editingFixed = { id: 0, category: "交通" }; renderFixed(); };
actions.editFixed = (el) => { state.editingFixed = { ...state.fixedCache.find((x) => x.id === Number(el.dataset.id)) }; renderFixed(); };
actions.cancelFixed = () => { state.editingFixed = null; renderFixed(); };
actions.askDeleteFixed = (el) => { state.confirmDelete = `f${el.dataset.id}`; renderFixed(); };
actions.deleteFixed = async (el) => {
  try { await api("DELETE", `/api/fixed-expenses/${el.dataset.id}`); toast("已刪除（之前已記的帳不會刪除）"); }
  catch (e) { toast(e.message); }
  state.confirmDelete = null;
  renderFixed();
};
actions.saveFixed = async (form) => {
  const f = new FormData(form);
  const e = state.editingFixed;
  const body = {
    id: e.id, name: f.get("name"), amount: Number(f.get("amount")), category: f.get("category"),
    dayOfMonth: Number(f.get("dayOfMonth")), startDate: f.get("startDate"), active: f.get("active") === "on", endDate: null,
  };
  try {
    if (e.id) await api("PUT", `/api/fixed-expenses/${e.id}`, body);
    else await api("POST", "/api/fixed-expenses", body);
    toast(`已儲存「${body.name}」`);
    state.editingFixed = null;
    renderFixed();
  } catch (err) { toast(err.message); }
};

// ═══════════════════════ 報表 ═══════════════════════

async function renderReport() {
  const r = await api("GET", `/api/report?month=${state.month}`);
  const max = Math.max(1, ...r.byCategory.map((c) => Math.max(c.amount, c.budget || 0)));
  const daysInMonth = new Date(...state.month.split("-").map(Number), 0).getDate();
  const daily = Array.from({ length: daysInMonth }, (_, i) => {
    const d = `${state.month}-${String(i + 1).padStart(2, "0")}`;
    return r.daily.find((x) => x.date === d)?.amount || 0;
  });
  const dmax = Math.max(1, ...daily);
  const W = 310, H = 110, bw = W / daysInMonth;

  $view.innerHTML = `
    <section class="card">
      ${monthSwitchHtml()}
      <div class="summary">
        <div><span>支出</span><b>${money(r.totalExpense)}</b></div>
        <div><span>收入</span><b>${money(r.totalIncome)}</b></div>
        <div><span>結餘</span><b>${money(r.balance)}</b></div>
      </div>
    </section>
    ${r.budgetAlerts.map((a) => `<div class="notice error">⚠ ${esc(a)}</div>`).join("")}
    <section class="card">
      <h2>各分類花費</h2>
      ${r.byCategory.length === 0 ? `<p class="empty">這個月還沒有支出</p>` : `
      <div class="stack" style="gap:12px">
        ${r.byCategory.map((c) => `
          <div class="bar-row">
            <span>${esc(c.category)}</span>
            <div class="bar-track" title="${esc(c.category)} ${money(c.amount)} 元${c.budget ? `／預算 ${money(c.budget)}` : ""}">
              <div class="bar-fill ${c.budget && c.amount > c.budget ? "over" : ""}" style="width:${(c.amount / max) * 100}%"></div>
              ${c.budget ? `<div class="bar-budget" style="left:${Math.min(99.5, (c.budget / max) * 100)}%"></div>` : ""}
            </div>
            <span class="num">${money(c.amount)}${c.budget ? `<span class="muted small"> / ${money(c.budget)}</span>` : ""}</span>
          </div>`).join("")}
      </div>
      <p class="muted small">直線是預算，用到 80% 會提醒。</p>`}
    </section>
    <section class="card">
      <h2>每日支出</h2>
      <svg class="daily-chart" viewBox="0 0 ${W} ${H + 16}" role="img" aria-label="每日支出長條圖">
        <line x1="0" y1="${H}" x2="${W}" y2="${H}" stroke="var(--line)" />
        ${daily.map((v, i) => v ? `<rect x="${i * bw + 1}" y="${H - (v / dmax) * (H - 8)}" width="${Math.max(1, bw - 2)}" height="${(v / dmax) * (H - 8)}" rx="2" fill="var(--bar)"><title>${i + 1} 日：${money(v)} 元</title></rect>` : "").join("")}
        ${[1, 10, 20, daysInMonth].map((d) => `<text x="${(d - 0.5) * bw}" y="${H + 13}" font-size="9" text-anchor="middle" fill="var(--muted)">${d}</text>`).join("")}
      </svg>
    </section>
    <section class="card">
      <h2>每月預算</h2>
      <form class="stack" data-submit="saveBudgets">
        <div class="grid-2">
          ${expenseCategories().map((name) => {
            const c = state.categories.find((x) => x.name === name);
            return `<label class="field">${esc(name)}<input type="number" min="0" step="100" inputmode="numeric" name="${esc(name)}" value="${c?.budget ?? ""}" placeholder="不設定"></label>`;
          }).join("")}
        </div>
        <div class="confirm-bar"><button class="btn primary" type="submit">儲存預算</button></div>
      </form>
    </section>`;
}
actions.saveBudgets = async (form) => {
  const f = new FormData(form);
  try {
    for (const [name, value] of f.entries()) {
      const old = state.categories.find((c) => c.name === name)?.budget ?? null;
      const budget = value === "" ? null : Number(value);
      if (budget !== old) await api("PUT", `/api/categories/${encodeURIComponent(name)}`, { budget });
    }
    await loadCategories();
    toast("已儲存預算");
    renderReport();
  } catch (e) { toast(e.message); }
};

// ═══════════════════════ 啟動 ═══════════════════════

(async function start() {
  state.month = todayStr().slice(0, 7);
  try {
    state.status = await api("GET", "/api/status");
    if (state.status.isDemo) {
      const badge = document.getElementById("engine-badge");
      badge.textContent = "示範模式";
      badge.title = "電腦端沒有啟用 Open eVision，辨識結果是示範資料";
      badge.hidden = false;
    }
    await loadCategories();
  } catch (e) {
    $view.innerHTML = `<div class="notice error">連不到電腦端的伺服器：${esc(e.message)}<br>請確認電腦已執行程式，且手機和電腦連在同一個 Wi-Fi。</div>`;
    return;
  }
  let tab = "scan";
  try { tab = localStorage.getItem("tab") || "scan"; } catch { /* ignore */ }
  setTab(views[tab] ? tab : "scan");
})();

if ("serviceWorker" in navigator && window.isSecureContext) {
  navigator.serviceWorker.register("sw.js").catch(() => { /* 沒有離線快取也能用 */ });
}
