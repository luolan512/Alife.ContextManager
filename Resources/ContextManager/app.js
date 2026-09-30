"use strict";

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, c => ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;" }[c]));
const fmtTokens = n => Math.round(Number(n || 0)).toLocaleString("zh-CN");
const initials = name => String(name || "?").trim().slice(0, 1).toUpperCase();
const moduleId = () => window.require("crypto").randomUUID();
const ipcChannel = new URLSearchParams(window.location.search).get("channel") || "marisa-context-manager";
// 每次改动前端都要递增：日志里出现它就说明窗口加载的是这份 app.js。
const APP_BUILD = "cm-app-2026-09-30-p";

// ---- 渲染端诊断日志：证明窗口实际加载了哪份 app.js、用的哪个 IPC 通道 ----
const traceDir = (() => {
  try {
    const fs = window.require("fs");
    const path = window.require("path");
    const file = decodeURIComponent(window.location.pathname).replace(/^\/+/, "");
    let dir = path.dirname(file);
    if (!path.isAbsolute(dir)) return null;
    for (let i = 0; i < 8; i++) {
      try { if (fs.existsSync(path.join(dir, "Character"))) return path.join(dir, "ContextManager"); } catch (_) { }
      const parent = path.dirname(dir);
      if (parent === dir) break;
      dir = parent;
    }
  } catch (_) { }
  return null;
})();
// ⚠️ 这个文件必须自己封顶。它是**跨会话追加**的：窗口每开一次就多一行 boot，
// 而且同一个 Storage 目录下所有历史会话都写同一个文件。后端那份 trace.log 有 600 KB
// 上限（见 ContextTrace.Write），这里以前完全没有 —— 长时间使用会一直涨，而且每条
// IPC 都要走一次 appendFileSync（渲染主线程同步 IO），自动拉全文时一次就是几百条。
// 现在按字节封顶：超过 TRACE_MAX_BYTES 就只保留后半段重写，做法与 ContextTrace 一致
// （代价是可能从某行中间截断，但日志尾部始终完整）。
const TRACE_MAX_BYTES = 512 * 1024;
const traceFile = traceDir ? traceDir + "\\renderer-trace.log" : null;
// 起始大小只量一次，之后自己累加 —— 否则每条日志都要多一次 statSync 系统调用。
let traceBytes = (() => {
  if (!traceFile) return 0;
  try { return window.require("fs").statSync(traceFile).size; } catch (_) { return 0; }
})();
function trace(line) {
  if (!traceFile) return;
  try {
    const fs = window.require("fs");
    const text = "[" + new Date().toTimeString().slice(0, 12) + "] " + line + "\n";
    if (traceBytes + text.length > TRACE_MAX_BYTES) {
      let existing = "";
      try { existing = fs.readFileSync(traceFile, "utf8"); } catch (_) { existing = ""; }
      const keep = existing.slice(-Math.floor(TRACE_MAX_BYTES / 2));
      fs.writeFileSync(traceFile, keep);
      traceBytes = keep.length;
    }
    fs.appendFileSync(traceFile, text);
    traceBytes += text.length;
  } catch (_) { }
}
trace("boot build=" + APP_BUILD + " channel=" + ipcChannel + " href=" + location.href);

let snapshot = null;
let currentView = "assembly";
let selectedOwner = "";
let worldBooks = {};
// worldbook-state 的元信息（分页进度）。报文装不下时会分页/截断，
// 这里记下 nextOffset / remaining / total / truncatedCount，面板据此提示「还有多少条没读回来」。
let worldBookMeta = {};
let presetNames = [];
let presetData = {};
let selectedPreset = "";
let historyPageOffset = 0;
let pendingChatAutoScroll = false;
let historyPageQuery = "";
let assemblyToggles = { character:true, presetSystem:true, presetUser:true, presetAssistant:true, world:true };
let assemblyMode = "official"; // official(官方装配还原/只读) | experiment(实验装配,可编辑)
let selectedAssemblyModule = "";
// 右栏「模块搜索」的关键字。143 个模块的计划里肉眼找不到某一条，所以右栏顶部常驻一个搜索框。
// 关键字只活在内存里：切换视图/角色会清空，不需要持久化。
let moduleSearchQuery = "";
let presetDrawerOpen = false;
let loadedOfflineOwner = "";   // 当前按需加载的离线角色（一次只保留一个，切走即卸载）
let bundleLoading = false;
let planOwner = "", planLoading = false, planTimer = null, bundleRequest = 0, planError = "", planDirty = false;
let planNotice = null;
let planState = null; // ContextPlan: { mode, applyMacros, modules:[{id,name,role,content,enabled}] }
// 「角色预设」：每个角色在插件里保留的上下文记录快照。
let characterPresets = {};   // { [owner]: [ {name, auto, updatedAt, mode, modules} ] }
const nativePrompts = {};
const nativeSystems = {};    // 官方系统消息全文（含名称/生日/简介/设定/私人文件夹）
const nativePromptRequestedAt = {};
const loadingItems = new Set();
// 已经通过 item:load 拿到「全文」的条目 id。
// 后端为了控制 IPC 报文体积，会把 state 里的条目正文截成预览并打 item.truncated=true
// （见 ContextStateBudget）。把预览当成正文写回去会毁掉用户数据，所以任何保存路径
// 都要先确认这条已经被完整加载过。
const loadedItems = new Set();

// 返回 true 表示「已经拦下这次保存」：先把全文读回来并打开大窗口让用户在那里保存。
function guardTruncatedItem(item) {
  if (!item || !item.truncated || loadedItems.has(item.id)) return false;
  loadingItems.delete(item.id);
  openMessage(item.id);
  setStatus("这条内容是预览，已先读取全文；请在打开的窗口里保存", false);
  return true;
}

// ── 装配计划 / 世界书的「正文未随报文下发」处理 ──
//
// 一张 142 条世界书的角色卡，计划有 143 个模块、正文 9.9 万字；一张 40 条长条目的卡，
// 世界书正文就有 7.8 万字。序列化后都超过单条 IPC 报文的预算，一次全发会把桥顶爆
// （整程序卡死）。所以后端按预算下发：结构（标题/来源/开关/关键词）全给，
// 正文放得下给全文、放不下给预览并打 truncated（见 ContextStateBudget.FitText）。
//
// 关键约定：**后端在 save / preview / apply 时会按 Id 从磁盘上的计划里把正文回填**，
// 所以前端不需要持有全文也不会丢数据。但编辑框必须是只读的 ——
// 否则用户改的是预览，保存时被后端回填覆盖，编辑就白做了。
const loadedPlanModules = new Set();   // 已通过 plan:module 取回全文的模块 id
const loadedWorldEntries = new Set();  // 已通过 worldbook:entry 取回全文的世界书条目 id
let worldLoadingAll = false;           // 「读取全部正文」正在分页拉取

const moduleTruncated = m => !!(m && m.truncated) && !loadedPlanModules.has(m.id);
const worldEntryTruncated = e => !!(e && e.truncated) && !loadedWorldEntries.has(e.id);

// 后端只会截「超过这个字数」的正文（见 ContextStateBudget.MinKeepChars）。
// 前端文案里把门槛说出来，免得用户看到 244 字也被叫「正文太大」而困惑。
const TRUNC_KEEP_CHARS = 2000;

function loadPlanModule(id) {
  send("plan:module", { owner: selectedOwner, id });
  setStatus("正在读取该模块的完整正文…");
}
function loadWorldEntry(id) {
  send("worldbook:entry", { owner: selectedOwner, id });
  setStatus("正在读取该条目的完整正文…");
}
function loadAllWorldContent() {
  if (worldLoadingAll) return;
  worldLoadingAll = true;
  setStatus("正在分页读取世界书全文…");
  send("worldbook:page", { owner: selectedOwner, offset: 0 });
}
// 「读取全部正文」是分页拉的：每收到一页就按 nextOffset 继续，直到 remaining 归零。
// 加 nextOffset 必须前进的保护 —— 否则后端某页装不下任何一条时前端会死循环。
function continueWorldPaging() {
  const meta = worldBookMeta[selectedOwner] || {};
  const next = Number(meta.nextOffset) || 0;
  const offset = Number(meta.offset) || 0;
  const remaining = Math.max(0, Number(meta.remaining) || 0);
  if (remaining > 0 && next > offset) {
    send("worldbook:page", { owner: selectedOwner, offset: next });
    return;
  }
  worldLoadingAll = false;
  setStatus(remaining > 0
    ? `世界书还有 ${remaining} 条单条太大，请用「读取全文」逐条读取`
    : "世界书全文已全部读取");
}

// ── 自动后台拉全文 ────────────────────────────────────────────────────────────
// 后端为了不把 IPC 桥顶爆，模块正文可能只随报文下发预览（truncated）。以前要用户
// 自己点「读取全文」，一张 143 模块的卡就等于 143 次手动点击 —— 观感上就是「内容不全」。
//
// 现在改成**后台自动**拉回来。两个硬性约束：
//   1. 必须**串行**：一次只发一个请求，等回包再发下一个。143 个请求一起发的话，
//      它们的回包会一起挤进出站队列（限速 600ms / 512 KB），既慢又容易顶到 1 MB 断连线。
//   2. 必须有**超时保护**：某一条没回包也要继续下一条，不能因为一次丢包卡死整条队列。
// 这只是把「同一批正文分多次拿回来」，完全在原有分页/限速机制内，不碰任何体积红线。
const AUTO_FULLTEXT_KEY = "marisa-context-auto-fulltext";
let autoFullText = (() => {
  try { const saved = localStorage.getItem(AUTO_FULLTEXT_KEY); return saved === null ? true : saved === "1"; }
  catch (_) { return true; }   // 读不到就当开 —— 默认行为是「内容尽量给全」
})();
function saveAutoFullText() { try { localStorage.setItem(AUTO_FULLTEXT_KEY, autoFullText ? "1" : "0"); } catch (_) { /* optional */ } }

const autoPlanQueue = [];      // 待拉取的模块 id（串行队列）
let autoPlanFetching = false;  // 队列正在跑
let autoPlanTotal = 0;         // 本轮一共要拉多少条
let autoPlanDone = 0;          // 已经拉回多少条
let autoPlanTimer = 0;         // 单条请求的超时保护

// 把当前计划里所有「只有预览」的模块排进队列。plan-state 每次到达都会调一次，
// 但已经在队列里或已读回的会被跳过，所以不会重复拉。
function autoFetchPlanModules() {
  if (!autoFullText) return;
  const queued = new Set(autoPlanQueue);
  const ids = (planState?.modules || [])
    .filter(m => m && m.truncated && !loadedPlanModules.has(m.id) && !queued.has(m.id))
    .map(m => m.id);
  if (!ids.length) return;
  autoPlanQueue.push(...ids);
  autoPlanTotal = autoPlanDone + autoPlanQueue.length;
  pumpAutoPlanFetch();
}

function pumpAutoPlanFetch() {
  if (autoPlanFetching) return;
  if (!autoFullText || autoPlanQueue.length === 0) {
    const finished = autoPlanTotal;
    autoPlanFetching = false;
    autoPlanTotal = 0; autoPlanDone = 0;
    // finished 为 0 说明这是用户手点的「读取全文」，那条提示由调用方自己给，这里别覆盖。
    if (finished > 0) setStatus(`已自动读取全部模块正文（${finished} 条）`);
    return;
  }
  autoPlanFetching = true;
  setStatus(`正在后台读取模块正文…（${autoPlanDone}/${autoPlanTotal}）`);
  send("plan:module", { owner: selectedOwner, id: autoPlanQueue.shift() });
  clearTimeout(autoPlanTimer);
  autoPlanTimer = setTimeout(() => { if (autoPlanFetching) { autoPlanFetching = false; pumpAutoPlanFetch(); } }, 15000);
}

// 换角色 / 重置时必须停掉：否则队列会拿着旧 owner 的 id 去问新 owner 要正文。
function resetAutoFetch() {
  clearTimeout(autoPlanTimer);
  autoPlanQueue.length = 0;
  autoPlanFetching = false;
  autoPlanTotal = 0; autoPlanDone = 0;
  worldLoadingAll = false;
}

// 把后端的降级步骤翻成人话。
function describeDegradeSteps(steps) {
  const map = {
    "drop-plans": "装配计划改为按需读取",
    "drop-prompts": "角色提示词改为按需读取",
    "items-content-dropped": "条目正文全部改为按需读取"
  };
  return (steps || []).map(s => {
    if (map[s]) return map[s];
    if (/^items-preview-/.test(s)) return `条目正文预览截断到 ${s.slice("items-preview-".length)} 字`;
    if (/^items-trimmed-/.test(s)) return `条目过多，只列出最近 ${s.slice("items-trimmed-".length)} 条`;
    return s;
  }).join("；");
}

// 报文降级说明。除了 degradeSteps，还要单独说明「有多少条根本没列出来」——
// 那是 ContextStateBudget 的最后一道兜底（条目数量本身就能把报文顶爆），
// 不说明的话用户会以为上下文只有列出来的那些。
function degradedBanner() {
  const hidden = Number(snapshot?.hiddenItems) || 0;
  const steps = snapshot?.degraded ? (snapshot.degradeSteps || []) : [];
  if (!steps.length && hidden <= 0) return "";
  const parts = [];
  if (steps.length) parts.push(`本次状态推送做了降级：${esc(describeDegradeSteps(steps))}`);
  if (hidden > 0) parts.push(`另有 ${hidden} 条条目因报文体积限制没有列出`);
  return `<div class="plan-help plan-help-strong">为控制 IPC 报文体积（过大会把 Electron 的 IPC 桥顶断），${parts.join("；")}。点开条目或装配页时会自动读取全文。</div>`;
}

const navDefs = [
  ["characters", "角色管理", "选择或编辑要管理的角色", "☷"],
  ["chat", "对话测试", "与当前角色交流并测试本轮上下文", "◌"],
  ["assembly", "上下文装配", "查看官方装配或调整酒馆兼容配方", "⬙"],
  ["charpresets", "角色预设", "为每个角色保存一份上下文记录，随时读取或一键生效", "❐"],
  ["memory", "长期记忆", "查看当前角色的 MemoryService 压缩层与事实", "◈"],
  ["media", "角色附件", "按角色查看存储的图片、音频和文件引用", "◐"],
  ["tavern", "酒馆兼容", "导入角色卡、世界书和预设配方", "☰"]
];

// 每种请求期望的回复类型。用于看门狗：超时后必须给出明确提示，
// 绝不允许界面永远停在“正在保存 / 正在应用 / 正在选择”。
const requestExpectations = {
  "context:refresh": ["state", "error"],
  "plan:get": ["plan-state", "plan-error", "plan-unavailable", "plan-loading"],
  "plan:save": ["plan-state", "plan-error"],
  "plan:apply": ["plan-state", "plan-error"],
  "plan:preview": ["plan-preview", "plan-error"],
  "plan:reset": ["plan-state", "plan-error"],
  // 正文被截断时按需取回单个模块的全文（plan-state 只带结构 + 预览）。
  "plan:module": ["plan-module", "plan-error"],
  "plan:import": ["plan-imported", "plan-error"],
  "plan:import-file": ["plan-imported", "plan-import-cancelled", "plan-error"],
  "import:options": ["import-options"],
  "import:options-save": ["import-options"],
  "worldbook:get": ["worldbook-state", "error"],
  "worldbook:save": ["worldbook-state", "error"],
  "worldbook:import": ["worldbook-state", "error"],
  // 按需读取世界书全文：page 走分页（每页都是完整正文），entry 取单条。
  "worldbook:page": ["worldbook-state", "error"],
  "worldbook:entry": ["worldbook-entry", "error"],
  "character:fields": ["character-fields", "error"],
  "character:save": ["character-saved", "error"],
  "character:create": ["tavern-imported", "error"],
  "character:load": ["character-bundle", "error", "owner-inactive"],
  "items:page": ["items-page", "error"],
  "native-prompt:get": ["native-prompt", "error"],
  "item:load": ["item-state", "error"],
  "item:update": ["state", "error"],
  "item:create": ["state", "error"],
  "item:delete": ["state", "error"],
  "presets:list": ["presets-list", "error"],
  "presets:get": ["preset-state", "error"],
  "presets:save": ["preset-state", "error"],
  "presets:delete": ["preset-deleted", "error"],
  "charpreset:list": ["charpreset-list", "error"],
  "charpreset:save": ["charpreset-saved", "charpreset-list", "plan-error", "error"],
  "charpreset:load": ["charpreset-loaded", "plan-state", "plan-error", "error"],
  "charpreset:apply": ["charpreset-loaded", "plan-applied", "plan-error", "error"],
  "charpreset:delete": ["charpreset-deleted", "charpreset-list", "error"],
  "charpreset:export": ["charpreset-exported", "charpreset-export-cancelled", "plan-error", "error"],
  "charpreset:import": ["charpreset-imported", "charpreset-import-cancelled", "plan-error", "error"],
  "chat:send": ["chat-finished", "error"],
  "chat:prune": ["chat-pruned", "plan-error", "error"],
  "history:page": ["history-page", "error"],
  "tavern:import": ["tavern-imported", "error"],
  "tavern:import-preset": ["preset-state", "error"],
  "character:activate": ["state", "error"],
  "character:deactivate": ["state", "error"]
};
function requestTimeoutMs(type) {
  // 文件对话框要等用户选文件，不能短超时。
  if (["plan:import-file", "tavern:import", "tavern:import-preset", "worldbook:import", "charpreset:export", "charpreset:import"].includes(type)) return 600000;
  if (["chat:send"].includes(type)) return 300000;
  if (["character:activate", "character:deactivate"].includes(type)) return 120000;
  return 15000;
}
const pendingRequests = new Map();
let requestSeq = 0;
function send(type, payload = {}) {
  const seq = ++requestSeq;
  trace("-> " + type + " #" + seq + " " + JSON.stringify(payload).slice(0, 240));
  window.require("electron").ipcRenderer.send(ipcChannel, JSON.stringify({ type, ...payload }));
  const expected = requestExpectations[type];
  if (!expected) return seq;
  const entry = { type, started: Date.now() };
  entry.fire = () => {
    pendingRequests.delete(seq);
    const seconds = Math.round((Date.now() - entry.started) / 1000);
    trace("timeout " + type + " after " + seconds + "s");
    setStatus("后端无响应", false);
    alertError(`“${type}”在 ${seconds} 秒内没有收到后端响应。\n\n常见原因：\n· 插件尚未重载（Alife 仍在运行旧编译产物）\n· 本窗口加载的是旧 app.js\n· 后端抛出了异常\n· 后端向窗口发送超大报文时 Electron.NET 的 IPC 桥卡死（此时后端会停止响应一切请求）\n· 出站队列堆积：后端的小回包被大报文挤到后面，甚至被同类型合并掉一条\n\n请先看 Storage/ContextManager/trace.log：\n· 如果最后一行是 “-> xxx” 而没有后续的 “<- ${type}”，说明请求根本没到后端，通常需要重启 Alife 才能恢复；\n· 如果连 trace.log 都停止更新，就是 IPC 桥卡死；\n· 如果有 “<- ${type}” 却没有对应的 “-> ” 回包，看有没有 outbox backlog / outbox coalesced —— 那是出站队列堆积导致回包被推迟或（旧版本）被合并掉。\n\n前端日志在 renderer-trace.log。`);
  };
  entry.timer = setTimeout(entry.fire, requestTimeoutMs(type));
  pendingRequests.set(seq, entry);
  return seq;
}
// 一条回复核销一个待回请求 —— 这个 1:1 假设是看门狗成立的前提。
// 唯一的例外是 state：后端允许把队列里**尚未发出**的多条 state 合并成最新的一份
// （它本来就是「最新一份即完整状态」，丢旧的无损），所以一份 state 必须核销**所有**
// 在等 state 的请求，否则剩下的会误报超时。
// ⚠️ 反过来说：worldbook-state / presets-list / charpreset-list / plan-state / native-prompt
// 都是「某个请求的专属回复」，后端**不允许**合并它们（见 ContextIpcBridge.Coalescible）。
const broadcastReplies = new Set(["state"]);
function noteReply(type) {
  const broadcast = broadcastReplies.has(type);
  for (const [seq, entry] of Array.from(pendingRequests.entries())) {
    if (!(requestExpectations[entry.type] || []).includes(type)) continue;
    clearTimeout(entry.timer);
    if (type === "busy") { entry.timer = setTimeout(entry.fire, requestTimeoutMs(entry.type)); return; }
    pendingRequests.delete(seq);
    if (!broadcast) return;
  }
}
// 把当前所有待回请求的看门狗统一推后。
// 用在「后端正在等用户做决定」的场景：这时请求没丢、后端也没卡，只是人在思考，
// 用默认 15 秒 / 10 分钟去判超时会弹出误导性的「后端无响应」。
function postponePendingRequests(ms) {
  for (const entry of pendingRequests.values()) {
    clearTimeout(entry.timer);
    entry.timer = setTimeout(entry.fire, ms);
  }
}
function planCacheKey(owner) { return `marisa-context-plan:${owner}`; }function localDefaultPlan(owner) {
  try {
    const saved = JSON.parse(localStorage.getItem(planCacheKey(owner)) || "null");
    if (saved && Array.isArray(saved.modules)) return normalizePlan(saved);
  } catch (_) { /* cache is optional */ }
  // 本地草稿里的「角色设定 #0」用**完整官方系统消息**，与后端保持一致：
  // 这样在计划回复到达前就点“应用”，也不会把 index[0] 变成没有外壳的裸设定。
  const system = nativeSystemContent(owner) || nativePromptContent(owner);
  return { mode:"Off", applyMacros:true, autoMacros:true, userName:"User", customMacros:{}, modules:[{
    id:"native-role", name:"角色设定 #0", role:"system", content:system, enabled:true,
    group:"system", source:"native", constant:true, keywords:[]
  }] };
}
function cachePlan(owner, plan) {
  try { localStorage.setItem(planCacheKey(owner), JSON.stringify(plan)); } catch (_) { /* cache is optional */ }
}
function submitPlan(type, message, commit = true) {
  if (!planState || !ownerInfo(selectedOwner).active) return alertError("请先激活角色并加载装配计划");
  if (commit) commitSidebar();
  cachePlan(selectedOwner, planState);
  if (type !== "plan:preview") planDirty = false;
  setStatus(message);
  send(type, { owner:selectedOwner, plan:planState });
}
function normalizePlan(plan) {
  if (!plan) return plan;
  plan.modules ||= [];
  plan.userName ||= "User";
  plan.customMacros = (plan.customMacros && typeof plan.customMacros === "object") ? plan.customMacros : {};
  // 「自动宏应用」默认开（与后端 ContextPlan.AutoMacros 的默认值一致）：
  // 老缓存里没有这个字段，不能因为 undefined 就当成「关」。
  plan.autoMacros = plan.autoMacros !== false;
  plan.modules.forEach(m => {
    m.group ||= "system";
    m.role = (m.role || "system").toLowerCase();
    if (m.source === "native" && m.name === "角色设定") m.name = "角色设定 #0";
  });
  return plan;
}

// 未识别的模板宏：后端不再中断请求，改为原样保留并回报清单，这里统一提示。
function unresolvedMacros(msg) { return Array.isArray(msg && msg.unresolved) ? msg.unresolved : []; }
function macroNoticeText(msg) {
  const list = unresolvedMacros(msg);
  if (!list.length) return "";
  return `；未识别宏 ${list.length} 个（已按原文保留）：${list.slice(0, 6).join("、")}${list.length > 6 ? " 等" : ""}`;
}

// 装配页来源分层：酒馆预设 / 角色卡 / 世界书 / 角色设定 / 自定义 各自成块，避免混在同一层。
const planSources = [["native","角色设定"],["preset","酒馆预设"],["card","角色卡"],["worldbook","世界书"],["custom","自定义"]];
const planSourceLabels = Object.fromEntries(planSources);
const ASSEMBLY_FILTER_KEY = "marisa-context-assembly-filter";
let assemblyFilter = (() => {
  try {
    const saved = JSON.parse(localStorage.getItem(ASSEMBLY_FILTER_KEY) || "null");
    if (saved && typeof saved === "object")
      return { group:saved.group||"all", source:saved.source||"all", view:saved.view==="source"?"source":"group" };
  } catch (_) { /* filter state is optional */ }
  return { group:"all", source:"all", view:"group" };
})();
function saveAssemblyFilter() { try { localStorage.setItem(ASSEMBLY_FILTER_KEY, JSON.stringify(assemblyFilter)); } catch (_) { /* optional */ } }

// 「导入角色卡时要不要连卡内世界书一起导」不再用工具栏勾选框 —— 用户没法预先知道
// 某张卡里到底有没有世界书，勾了也可能白勾。现在改成：后端读到卡之后**检测到 character_book
// 就弹窗问一次**（弹窗里带复选框「记住我的选择」，写进 Storage/ContextManager/ImportOptions.json）。
// 策略值由后端持有（ask / always / never），前端只在酒馆兼容页展示与修改。
let importOptions = { cardWorldbook: "ask" };
let importOptionsLoaded = false;
const IMPORT_CARD_WORLDBOOK_LABEL = { ask: "每次询问", always: "总是导入", never: "从不导入" };

function itemsBy(pred) { return snapshot ? snapshot.items.filter(pred) : []; }
const byOwner = name => itemsBy(i => i.owner === name);
const isCharItem = i => !["全局"].includes(i.owner);
const roleLabel = i => (i.kind || "unknown").toLowerCase();
function ownerInfo(name) { return (snapshot?.owners || []).find(o => o.name === name) || { name, count: byOwner(name).length, tokens:0, active:false, memoryEnabled:false }; }
function runtimeSystemContent(owner) {
  const sent = snapshot?.runtimeSystemByOwner?.[owner];
  if (sent) return sent;
  const system = snapshot?.items?.find(i => i.owner === owner && i.sourceKey === "live-system" && messageIndex(i) === 0);
  return system?.content || "";
}
// 「角色设定 #0」在「插件覆盖」下的真实内容 = 计划里保存的模块内容（每次请求前会替换 index[0]）。
// 只有「关闭 / 本地覆盖」时，运行时的 index[0] 才等于实际发送的系统消息。
// 以前这里一律显示运行时的 index[0]，于是“插件覆盖已生效、预览却还是本地内容”，看着像没生效。
function nativeModuleDisplay(owner, m) {
  const mode = planState?.mode;
  if (m && mode === "Temporary") return m.content || "";
  return runtimeSystemContent(owner) || m?.content || snapshot?.characterPromptByOwner?.[owner] || "";
}
function storageRootGuess() {
  // traceDir 是 <Storage>/ContextManager；derived 出来可在 diagnostics 缺失时兜底。
  if (!traceDir) return "";
  try { return window.require("path").resolve(traceDir, ".."); } catch (_) { return ""; }
}
// 直接读角色的 index.json：窗口已开 Node 集成，丢一条 IPC 回复不该让编辑器变空白。
function readCharacterIndex(owner) {
  if (!owner) return null;
  try {
    const root = snapshot?.diagnostics?.storageRoot || storageRootGuess();
    if (!root) return null;
    const fs = window.require("fs");
    const path = window.require("path");
    const characterRoot = path.resolve(root, "Character");
    const readIndex = dir => {
      const file = path.resolve(characterRoot, dir, "index.json");
      const relative = path.relative(characterRoot, file);
      if (!relative || relative.startsWith("..") || path.isAbsolute(relative)) return null;
      if (!fs.existsSync(file)) return null;
      return JSON.parse(fs.readFileSync(file, "utf8"));
    };
    const direct = readIndex(owner);
    if (direct && direct.Name === owner) return direct;
    // 角色目录名可能与 index.json 的 Name 不一致：再按 Name 找一次。
    const entries = fs.existsSync(characterRoot) ? fs.readdirSync(characterRoot, { withFileTypes: true }) : [];
    for (const entry of entries) {
      if (!entry.isDirectory() || entry.name === owner) continue;
      const hit = readIndex(entry.name);
      if (hit && hit.Name === owner) return hit;
    }
    return direct;
  } catch (err) { trace("native-prompt disk error " + (err && err.message)); return null; }
}
function nativePromptContent(owner) {
  if (!ownerInfo(owner).active) return "";
  const index = readCharacterIndex(owner);
  if (index) {
    trace("native-prompt disk hit owner=" + owner + " len=" + String(index.Prompt ?? "").length);
    return String(index.Prompt ?? "");
  }
  const sent = nativePrompts[owner] ?? snapshot?.characterPromptByOwner?.[owner];
  if (sent) return sent;
  // 框架在 index[0] 外面套了一层固定外壳。这里只做展示兜底，绝不把整段外壳写回角色 Prompt。
  const match = runtimeSystemContent(owner).match(/(?:^|\n)[ \t]*-[ \t]*设定[：:][ \t]*\r?\n([\s\S]*?)(?=(?:\r?\n)+[ \t]*这是你的私人文件夹[：:]|$)/);
  return match ? match[1].trim() : "";
}
// 完整的官方系统消息（名称 / 生日 / 简介 / 设定 / 私人文件夹）。
// 装配页的「角色设定 #0」模块保存的就是这段全文，插件覆盖下整段都可编辑。
function nativeSystemContent(owner) {
  if (!ownerInfo(owner).active) return "";
  if (nativeSystems[owner]) return nativeSystems[owner];
  const index = readCharacterIndex(owner);
  if (index) return buildOfficialSystemPreview(index.Name || owner, index.Birthday || "", index.Description || "", index.Prompt || "");
  const live = runtimeSystemContent(owner);
  if (live) return live;
  const prompt = nativePromptContent(owner);
  return prompt ? buildOfficialSystemPreview(owner, "", "", prompt) : "";
}
function requestNativePrompt(owner) {
  if (!owner || !ownerInfo(owner).active || Object.hasOwn(nativePrompts, owner)) return;
  const now = Date.now();
  if (now - (nativePromptRequestedAt[owner] || 0) < 1500) return;
  nativePromptRequestedAt[owner] = now;
  send("native-prompt:get", { owner });
}

function isOfflineBundleItem(i) {
  const k = i.sourceKey || "";
  return ["offline-system","offline-history","offline-history-truncated","local-media"].includes(k) || k.startsWith("archive-L");
}
function unloadOfflineOwner() {
  if (!loadedOfflineOwner || !snapshot) return;
  snapshot.items = snapshot.items.filter(i => !(i.owner === loadedOfflineOwner && isOfflineBundleItem(i)));
  loadedOfflineOwner = "";
}
function ensureOwnerLoaded(name) {
  if (!name || !snapshot) return;
  const info = ownerInfo(name);
  if (!info.active) {
    // Strict lazy-loading: an inactive character contributes metadata only.
    snapshot.items = snapshot.items.filter(i => i.owner === "全局");
    worldBooks = {}; worldBookMeta = {}; loadedPlanModules.clear(); loadedWorldEntries.clear(); resetAutoFetch();
    loadedOfflineOwner = ""; bundleLoading = false;
    selectedAssemblyModule = ""; planState = null; planOwner = ""; planLoading = false; planDirty = false;
    return;
  }
  if (loadedOfflineOwner === name) return;
  // Keep the active owner's initial live snapshot on screen while the bounded
  // character-bundle request refreshes it.  Clearing it here made every active
  // role appear to have no system prompt or function messages whenever Electron
  // delayed that secondary reply.
  snapshot.items = snapshot.items.filter(i => i.owner === "全局" || i.owner === name);
  worldBooks = {}; worldBookMeta = {}; loadedPlanModules.clear(); loadedWorldEntries.clear(); worldLoadingAll = false;
  loadedOfflineOwner = name; bundleLoading = true;
  // Keep the plan delivered with the initial snapshot while refreshing messages.
  selectedAssemblyModule = "";
  send("character:load", {owner:name,requestId:++bundleRequest});
  ensureWorldBook(name);
}
function setStatus(text, ok = true) {
  $("#statusText").textContent = text;
  $("#statusDot").style.background = ok ? "#8dffbf" : "#ffd166";
}

function renderInactiveContextPage(title) {
  const info = ownerInfo(selectedOwner);
  if (info.active) return false;
  head(title, "该角色尚未激活。为避免多个角色的上下文同时占用内存，本窗口不会读取它的本地 Prompt、历史、记忆、世界书或装配计划。");
  $("#pageBody").innerHTML = `<section class="empty"><h3>角色未激活</h3><p>激活后才会读取并显示 ${esc(selectedOwner || "该角色")} 的完整上下文。</p><button class="btn primary" id="activateContextOwner">激活角色</button></section>`;
  $("#activateContextOwner").onclick = () => send("character:activate", { owner:selectedOwner });
  return true;
}

function renderNav() {
  $("#navList").innerHTML = navDefs.map(([id, label, desc, icon]) => {
    const count = id === "characters" ? (snapshot?.owners?.length || 0) :
      id === "memory" ? itemsBy(i => i.owner === selectedOwner && ["长期记忆", "归档记忆文件"].includes(i.category)).length :
      id === "media" ? itemsBy(i => i.owner === selectedOwner && ((i.attachments || []).length || ["image", "audio", "multimodal"].includes(i.modality))).length : "";
    return `<button class="nav-item ${currentView === id ? "active" : ""}" data-view="${id}" title="${esc(desc)}"><span class="nav-ic">${icon}</span><span><b>${label}</b><small>${esc(desc)}</small></span>${count !== "" ? `<em>${count}</em>` : ""}</button>`;
  }).join("");
  $$(".nav-item").forEach(btn => btn.onclick = () => { currentView = btn.dataset.view; selectedAssemblyModule = ""; render(); });
}

function countMedia() {
  return itemsBy(i => (i.attachments || []).length || ["image","audio","multimodal"].includes(i.modality)).length;
}

function render() {
  // 所有页面从空工作区开始渲染；页面函数可以安全地使用 += 组合局部内容，避免切换时叠加旧视图。
  $("#pageHead").innerHTML = "";
  $("#pageBody").innerHTML = "";
  document.body.dataset.view = currentView;
  if (charPresetOwner && charPresetOwner !== selectedOwner) charPresetOwner = "";
  ensureOwnerLoaded(selectedOwner);
  renderNav();
  const views = {
    overview: renderOverview, characters: renderCharacters, prompts: renderPrompts,
    worldbook: renderWorldBook, chat: renderChat, memory: renderMemory, media: renderMedia,
    presets: renderPresets, tavern: renderTavernCompatibility, assembly: renderAssembly,
    charpresets: renderCharacterPresets, configs: renderConfigs
  };
  (views[currentView] || renderOverview)();
  renderInspector();
}

function renderInspector() {
  if (currentView === "assembly") {
    renderPlanInspector();
    return;
  }
  const o = ownerInfo(selectedOwner);
  const tips = {
    overview:["总览原则","初始页只呈现计数、分布和短预览，不把全部历史正文加载到内存。"],
    characters:["角色库","卡片适合横向比较所有角色；点击卡片进入角色编辑。"],
    prompts:["提示词编辑","大窗口编辑用于长 Prompt；保存写入 index.json 并尝试同步实时上下文。"],
    worldbook:["世界书","常驻条目总是注入；关键词条目后续可按当前对话自动触发。"],
    chat:["聊天上下文","实时角色读取 ChatBot.ChatHistory；离线角色读取 History.json。"],
    memory:["分层记忆","History 是流水，L1/L2/L3 是逐级压缩摘要。"],
    media:["多模态判定","embedded 是 base64，ok 是路径可用，missing 是路径丢失。"],
    presets:["酒馆预设库","酒馆预设会转换为统一片段；选择后可在装配台中继续微调。"],
    charpresets:["角色预设","每个角色保留一份上下文记录；「插件覆盖」的记录会在插件启动时自动侧装。"],
    tavern:["酒馆兼容","导入资源后不会变成黑箱：角色、酒馆预设和世界书都会回到可查看、可编辑的上下文模块。"],
    assembly:["官方装配还原","这里呈现框架实际发送给模型的顺序，只读且不混入实验草稿。"],
    configs:["模块配置","这里的 JSON 是模块配置，不应混入角色提示词。"]
  }[currentView] || ["检查器","上下文信息"];
  $("#inspector").innerHTML = `<div class="inspector-kicker">CONTEXT DETAILS</div><h3>${tips[0]}</h3><p class="inspector-tip">${tips[1]}</p>
    <div class="inspector-divider"></div>
    <div class="kv">
      <span>当前角色</span><b>${esc(selectedOwner || "未选择")}</b>
      <span>运行状态</span><b>${o.active ? "实时会话" : "离线数据"}</b>
      <span>Memory</span><b>${o.memoryEnabled ? "已启用" : "未启用"}</b>
      <span>条目数</span><b>${o.count || 0}</b>
      <span>估算</span><b>≈ ${fmtTokens(o.tokens)} tokens</b>
    </div>`;
}

function assemblyModuleKey(group, item, index) {
  return `${group}:${item.id || item.Id || item.sourceIndex || index}`;
}
function assemblyModuleLabel(group) {
  return ({ character:"角色系统", presetSystem:"System 预设", presetUser:"User 预设", presetAssistant:"AI 预设", world:"世界书" })[group] || "上下文模块";
}
function findAssemblyModule(key) {
  for (const [group, title, list] of assemblyGroups(selectedOwner)) {
    const index = list.findIndex((item, i) => assemblyModuleKey(group, item, i) === key);
    if (index >= 0) return { group, title, item:list[index], index };
  }
  return null;
}
function assemblyModuleEditable(module) {
  if (!module) return false;
  if (module.group === "world" || module.group.startsWith("preset")) return true;
  if (module.group === "character") return true;
  const sk = String(module.item?.sourceKey || module.item?.SourceKey || "");
  if (sk === "character-prompt" || sk === "offline-history" || sk === "live-system" || sk === "live-history") return true;
  if (sk.startsWith("archive-L")) return true;
  return false;
}
function renderAssemblyInspector() {
  const groups = assemblyGroups(selectedOwner);
  if (!selectedAssemblyModule || !findAssemblyModule(selectedAssemblyModule)) {
    const first = groups.flatMap(([group, title, list]) => list.map((item, index) => ({ group, item, index }))).find(x => x.item);
    selectedAssemblyModule = first ? assemblyModuleKey(first.group, first.item, first.index) : "";
  }
  const module = findAssemblyModule(selectedAssemblyModule);
  if (!module) {
    $("#inspector").innerHTML = `<div class="inspector-kicker">MODULE EDITOR</div><h3>选择一个模块</h3><p class="inspector-tip">从中间装配流中点选一张卡片，即可在这里编辑该模块，同时保持它在整体上下文中的位置可见。</p>`;
    return;
  }
  const { group, item, index } = module;
  const editable = assemblyModuleEditable(module);
  const type = assemblyModuleLabel(group);
  const trigger = group === "world" ? (item.constant ? "常驻注入" : "关键词触发") : group.startsWith("preset") ? "预设顺序注入" : group === "character" ? "固定顶部" : "由运行时决定";
  const content = item.content || item.Content || "";
  const name = item.title || item.Title || `${type} ${index + 1}`;
  $("#inspector").innerHTML = `<div class="inspector-topline"><span class="inspector-kicker">MODULE EDITOR</span><button class="icon-quiet" id="openModuleSource" title="查看完整原文">↗</button></div>
    <span class="module-type ${group}">${type}</span><h3>${esc(name)}</h3><p class="inspector-tip">${esc(trigger)} · 位于装配流第 ${index + 1} 个 ${type}模块</p>
    <div class="inspector-divider"></div>
    <div class="field compact"><label>模块名称 <small>${group === "character" ? "角色名由角色卡管理" : "仅此模块使用"}</small></label><input id="assemblyModuleName" value="${esc(name)}" ${group === "character" ? "readonly" : ""}/></div>
    <div class="field inspector-editor"><label>提示词内容 <small>${fmtTokens(Math.ceil(content.length / 1.7))} tokens</small></label><textarea id="assemblyModuleContent" ${editable ? "" : "readonly"}>${esc(content)}</textarea></div>
    <div class="assembly-editor-meta"><span>来源</span><b>${esc(item.source || (group === "world" ? "世界书" : group.startsWith("preset") ? "预设库" : "上下文"))}</b><span>状态</span><b>${assemblyToggles[group] ? "已参与实验装配" : "当前已暂停"}</b></div>
    <div class="inspector-actions">${editable ? `<button class="btn" id="resetAssemblyModule">撤销本次编辑</button><button class="btn primary" id="saveAssemblyModule">保存修改</button>` : `<button class="btn primary" id="openModuleDetail">查看完整内容</button>`}</div>`;
  $("#openModuleSource").onclick = () => openAssemblyModuleSource(module);
  $("#openModuleDetail")?.addEventListener("click", () => openAssemblyModuleSource(module));
  $("#resetAssemblyModule")?.addEventListener("click", () => renderAssemblyInspector());
  $("#saveAssemblyModule")?.addEventListener("click", () => saveAssemblyModule(module));
}
function openAssemblyModuleSource(module) {
  const item = module.item;
  if (item.id) openMessage(item.id);
  else openModal(item.title || item.Title || "上下文模块", `<pre class="preview-code long">${esc(item.content || item.Content || "")}</pre>`);
}
function saveAssemblyModule(module) {
  const title = $("#assemblyModuleName").value.trim();
  const content = $("#assemblyModuleContent").value;
  const { group, item } = module;
  if (group === "world") {
    item.title = title || item.title;
    item.content = content;
    send("worldbook:save", { owner:selectedOwner, entries:(worldBooks[selectedOwner]?.entries || []) });
    setStatus("正在保存世界书模块…");
  } else if (group.startsWith("preset")) {
    const raw = item.raw;
    if (raw) {
      if (Object.prototype.hasOwnProperty.call(raw, "Name")) raw.Name = title || raw.Name;
      else raw.name = title || raw.name;
      if (Object.prototype.hasOwnProperty.call(raw, "Content")) raw.Content = content;
      else raw.content = content;
      send("presets:save", { name:selectedPreset, preset:presetData[selectedPreset] });
      setStatus("正在保存预设片段…");
    }
  } else if (item.id) {
    if (guardTruncatedItem(item)) return;
    send("item:update", { id:item.id, content });
    setStatus("正在保存上下文模块…");
  }
  render();
}


function head(title, desc, actions = "") {
  const owners = snapshot?.owners || [];
  if (!selectedOwner && owners.length) selectedOwner = owners[0].name;
  const current = ownerInfo(selectedOwner);
  $("#pageHead").innerHTML = `<div class="page-title-block"><div><div class="eyebrow">${currentView === "assembly" ? "CONTEXT COMPOSER" : "CONTEXT WORKSPACE"}</div><h2>${title}</h2><p>${desc}</p></div>
    <div class="context-lens"><span class="lens-avatar">${initials(selectedOwner)}</span><div><small>正在查看</small><select id="headOwner" aria-label="当前角色">${owners.map(o => `<option value="${esc(o.name)}" ${o.name === selectedOwner ? "selected" : ""}>${esc(o.name)}${o.active ? " · 实时" : ""}</option>`).join("") || `<option>未选择角色</option>`}</select></div><span class="lens-state ${current.active ? "live" : ""}">${current.active ? "实时会话" : "离线资料"}</span></div></div>${actions ? `<div class="head-actions">${actions}</div>` : ""}${degradedBanner()}`;
  $("#headOwner")?.addEventListener("change", event => { selectedOwner = event.target.value; selectedAssemblyModule = ""; render(); ensureOwnerLoaded(selectedOwner); });
}

function renderOverview() {
  head("总览驾驶舱", "先显示全局元数据、短预览和附件索引；正文、图片详情与编辑面板均按需打开，避免扫描大 History 造成无响应。");
  if (!snapshot) return;
  const active = snapshot.owners.filter(o => o.active).length;
  const mem = snapshot.owners.filter(o => o.memoryEnabled).length;
  const media = countMedia();
  $("#pageBody").innerHTML = `
    <div class="metric-grid">
      ${metric("角色总数", snapshot.owners.length, "全部角色，不要求激活")}
      ${metric("运行中", active, "来自实时 ChatHistory")}
      ${metric("记忆模块", mem, "启用 MemoryService 的角色")}
      ${metric("多模态条目", media, "图片 / 音频 / 文件引用")}
    </div>
    <div class="overview-grid">
      <section class="panel-card">
        <div class="section-title"><h3>角色上下文量</h3><button class="btn tiny" data-action="refresh">刷新</button></div>
        <div class="owner-bars">${snapshot.owners.map(o => ownerBar(o)).join("")}</div>
      </section>
      <section class="panel-card">
        <div class="section-title"><h3>内容分类</h3></div>
        ${(snapshot.categories || []).map(c => `<div class="cat-line"><span>${c.name}</span><b>${c.count}</b><em>≈ ${fmtTokens(c.tokens)}</em></div>`).join("")}
      </section>
    </div>
    <section class="panel-card recent">
      <div class="section-title"><h3>最近条目</h3></div>
      <div class="mini-table">${snapshot.items.slice(0, 12).map(miniRow).join("")}</div>
      ${overviewPagingNote()}
    </section>`;
  $('[data-action="refresh"]').onclick = () => { setStatus("正在刷新…", true); send("context:refresh"); };
  $$(".owner-bar,.mini-row").forEach(el => el.onclick = () => { selectedOwner = el.dataset.owner; currentView = el.dataset.view || "chat"; render(); });
  $$("[data-omitted-owner]").forEach(b => b.onclick = () => { selectedOwner = b.dataset.omittedOwner; currentView = "chat"; render(); });
}

function metric(label, value, sub) { return `<div class="metric"><b>${value}</b><span>${label}</span><em>${sub}</em></div>`; }

// 总览页只列最近 12 条，但「还有多少条没进这次报文」必须说出来，
// 否则用户会以为上下文只有列出来的这些。真正的翻页入口在各角色的对话/记忆/附件页。
function overviewPagingNote() {
  const owners = snapshot?.owners || [];
  const total = owners.reduce((n, o) => n + omittedFor(o.name), 0);
  if (total <= 0) return "";
  const detail = owners
    .filter(o => omittedFor(o.name) > 0)
    .map(o => `<button class="link-btn" data-omitted-owner="${esc(o.name)}">${esc(o.name)} ${omittedFor(o.name)} 条</button>`)
    .join("、");
  return `<div class="toolbar paging-bar"><span class="paging-note">本次报文为控制体积做了分页，还有 <b>${total}</b> 条更早的条目未加载：${detail}。点角色名进入该角色的对话页后可用「加载更早」继续读取。</span></div>`;
}
function ownerBar(o) {
  const max = Math.max(1, ...snapshot.owners.map(x => x.tokens));
  const pct = Math.max(3, Math.round(o.tokens / max * 100));
  return `<div class="owner-bar" data-owner="${esc(o.name)}" data-view="chat">
    <div class="owner-main"><b>${esc(o.name)}</b><span>${o.active ? "实时" : "离线"} · ${o.count} 条</span></div>
    <div class="bar"><i style="width:${pct}%"></i></div><em>≈${fmtTokens(o.tokens)}</em>
  </div>`;
}
function miniRow(i) {
  return `<div class="mini-row" data-owner="${esc(i.owner)}" data-view="${i.attachments?.length ? "media" : "chat"}">
    <span class="role-pill ${roleLabel(i)}">${roleLabel(i)}</span><b>${esc(i.title)}</b><em>${esc(i.owner)} / ${esc(i.category)}</em>
  </div>`;
}

function renderCharacters() {
  head("角色管理", "选择一个角色后，所有对话测试、装配、记忆与附件页面都会切换到该角色。角色较多时列表独立滚动，不会挤压工作流。",
    `<button class="btn" id="refreshCharacterBtn">刷新角色</button><button class="btn primary" id="createCharacterFromList">新建角色</button>`);
  const owners = snapshot?.owners || [];
  $("#pageBody").innerHTML = `<section class="character-list-page">${owners.map(o => characterRow(o)).join("") || emptyMini("尚未创建角色")}</section>`;
  $("#refreshCharacterBtn").onclick = () => send("context:refresh");
  $("#createCharacterFromList").onclick = () => $("#newCharacterBtn").click();
  $$("[data-character-select]").forEach(button => button.onclick = () => { selectedOwner = button.dataset.characterSelect; currentView = "chat"; render(); ensureOwnerLoaded(selectedOwner); });
}
function characterRow(o) {
  const prompt = itemsBy(i => i.owner === o.name && i.sourceKey === "character-prompt")[0];
  const worldCount = worldBooks[o.name]?.entries?.length || 0;
  const selected = o.name === selectedOwner;
  const summary = o.active ? (prompt?.content || "正在加载实时上下文…") : "未激活：本地上下文未加载";
  return `<article class="character-row ${selected ? "selected" : ""}"><button data-character-select="${esc(o.name)}"><span class="character-row-avatar">${initials(o.name)}</span><span class="character-row-main"><b>${esc(o.name)}</b><small>${o.active ? "实时会话" : "未激活 · 未加载本地上下文"} · ≈ ${fmtTokens(o.tokens)} tokens · ${o.count} 条索引</small><p>${esc(summary)}</p></span><span class="character-row-meta"><em class="badge ${o.active ? "green" : "amber"}">${o.active ? "运行中" : "未激活"}</em><small>Memory ${o.memoryEnabled ? "已启用" : "未启用"}</small><small>${o.active ? `${worldCount} 个世界书条目` : "世界书未加载"}</small></span><span class="character-row-arrow">→</span></button></article>`;
}

function characterCard(name) {
  const o = ownerInfo(name);
  const prompt = itemsBy(i => i.owner === name && i.sourceKey === "character-prompt")[0];
  const desc = prompt?.content || "";
  const messageCount = itemsBy(i => i.owner === name && ["实时上下文","历史对话"].includes(i.category)).length;
  const mediaCount = itemsBy(i => i.owner === name && ((i.attachments || []).length || ["image","audio"].includes(i.modality))).length;
  const worldCount = worldBooks[name]?.entries?.length || 0;
  return `<article class="card character-card" data-owner="${esc(name)}">
    <div class="char-head">
      <div class="avatar">${initials(name)}</div>
      <div class="char-badges">${badges(o)}</div>
    </div>
    <h3>${esc(name)}</h3>
    <p class="preview">${esc(desc || "暂无简介")}</p>
    <div class="char-statline">
      <span><b>${messageCount}</b>消息</span><span><b>${mediaCount}</b>多模态</span><span><b>${worldCount}</b>世界书</span>
    </div>
    <div class="card-actions">
      <button class="btn tiny" data-card-action="prompt" data-owner="${esc(name)}">提示词</button>
      <button class="btn tiny" data-card-action="chat" data-owner="${esc(name)}">对话</button>
      <button class="btn tiny" data-card-action="memory" data-owner="${esc(name)}">记忆</button>
      <button class="btn tiny" data-card-action="activate" data-owner="${esc(name)}">${o.active ? "停用" : "激活"}</button>
    </div>
  </article>`;
}

function badges(o) {
  return `<span class="badge ${o.active ? "green" : "amber"}">${o.active ? "运行中" : "未激活"}</span>
    <span class="badge ${o.memoryEnabled ? "green" : ""}">${o.memoryEnabled ? "长期记忆" : "无记忆模块"}</span>`;
}

function renderPrompts() {
  if (renderInactiveContextPage("提示词工作室")) return;
  head("提示词工作室", "编辑角色原始字段（生日 / 简介 / Prompt），中间大编辑区直接展开；下方实时预览“装配后官方系统提示词”。保存写入 index.json，并尝试同步实时上下文。");
  const list = snapshot.owners.map(o => o.name);
  if (!selectedOwner || !list.includes(selectedOwner)) selectedOwner = list[0] || "";
  const promptItem = itemsBy(i => i.owner === selectedOwner && i.sourceKey === "character-prompt")[0];
  const info = ownerInfo(selectedOwner);
  const msgCount = itemsBy(i => i.owner === selectedOwner && ["实时上下文","历史对话"].includes(i.category)).length;

  // read additional fields from index via the offline-system item tags? Use snapshot raw unavailable; derive from item path.
  // We keep simple editable fields; description defaults to empty unless provided through snapshot.
  $("#pageBody").innerHTML = `
    <div class="prompt-workbench">
      <section class="prompt-main">
        <div class="prompt-fields">
          <div class="field compact"><label>角色名</label><input id="pfName" value="${esc(selectedOwner)}" readonly /></div>
          <div class="field compact"><label>生日</label><input id="pfBirthday" type="datetime-local" value=""/></div>
        </div>
        <div class="field"><label>简介 Description</label><textarea id="pfDescription" class="desc-editor" placeholder="角色简介…"></textarea></div>
        <div class="field grow"><label>角色 Prompt（设定）</label><textarea id="pfPrompt" class="prompt-full-editor" placeholder="输入角色 Prompt…">${esc(promptItem?.content || "")}</textarea></div>
        <div class="toolbar prompt-toolbar">
          <button class="btn primary" id="saveCharacter">保存角色</button>
          <button class="btn" id="bigEdit">大窗口编辑 Prompt</button>
          <button class="btn" id="activateBtn">${info.active ? "停用角色" : "激活角色"}</button>
          <button class="btn" id="previewAssembly">查看装配</button>
        </div>
        <details class="official-preview" open>
          <summary>装配后官方系统提示词（预览，只读）</summary>
          <pre id="officialSystemPreview" class="preview-code"></pre>
        </details>
      </section>
      <aside class="prompt-right">
        <h3>选择角色</h3>
        <div class="prompt-role-list">${list.map(n => `
          <button class="prompt-role ${n === selectedOwner ? "active" : ""}" data-owner-switch="${esc(n)}">
            <b>${esc(n)}</b><small>${ownerInfo(n).active ? "实时" : "离线"} · ${itemsBy(i=>i.owner===n && ["实时上下文","历史对话"].includes(i.category)).length}条</small>
          </button>`).join("")}</div>
      </aside>
    </div>`;

  // request full index to populate birthday/description (lightweight single file)
  send("character:fields", { name: selectedOwner });

  const updatePreview = () => {
    $("#officialSystemPreview").textContent = buildOfficialSystemPreview(
      selectedOwner, $("#pfBirthday").value, $("#pfDescription").value, $("#pfPrompt").value);
  };
  ["pfBirthday","pfDescription","pfPrompt"].forEach(id => $("#"+id).addEventListener("input", updatePreview));
  updatePreview();

  $("#saveCharacter").onclick = () => send("character:save", {
    name: selectedOwner,
    birthday: $("#pfBirthday").value || null,
    description: $("#pfDescription").value,
    prompt: $("#pfPrompt").value
  });
  $("#bigEdit").onclick = () => openLargeEditor(promptItem, $("#pfPrompt").value);
  $("#activateBtn").onclick = () => send(info.active ? "character:deactivate" : "character:activate", { owner:selectedOwner });
  $("#previewAssembly").onclick = () => { currentView = "assembly"; render(); };
}

function buildOfficialSystemPreview(name, birthday, description, prompt) {
  const storageKey = `Character\${name}`;
  return `这是你的人物信息：
- 名称：${name}
- 生日：${birthday ? birthday.replace("T"," ") : "（未设置）"}
- 简介：${description || ""}
- 设定：
${prompt || ""}

这是你的私人文件夹：
Storage/${storageKey}/Storage`;
}


function renderChat() {
  if (renderInactiveContextPage("对话测试")) return;
  const info = ownerInfo(selectedOwner);
  ensureOwnerLoaded(selectedOwner);
  // 激活角色：只取真实时 ChatHistory（全局 #序号，顺序确定）；未激活才回退离线历史，避免两类编号撞车
  const messages = info.active
    ? itemsBy(i => i.owner === selectedOwner && i.category === "实时上下文").sort((a,b) => messageIndex(a) - messageIndex(b))
    : itemsBy(i => i.owner === selectedOwner && i.category === "历史对话").sort((a,b) => messageIndex(a) - messageIndex(b));
  const contextTokens = itemsBy(i => i.owner === selectedOwner && ["角色预设", "功能说明", "长期记忆", "实时上下文", "历史对话"].includes(i.category)).reduce((n, i) => n + (i.estimatedTokens || 0), 0);
  head("对话测试", "与当前角色直接交流。此页只保留对话和本轮 token 总量；具体注入顺序请在“上下文装配”查看。",
    `<span class="chat-token-bar">本轮上下文 ≈ ${fmtTokens(contextTokens)} tokens</span><button class="btn" id="chatActivateBtn">${info.active ? "停用角色" : "激活角色"}</button><button class="btn" id="historyBrowserBtn">搜索历史</button><button class="btn primary" id="openAssemblyFromChat">查看装配</button>`);
  $("#pageBody").innerHTML = `<section class="chat-test-page"><div class="chat-log">${messages.map(messageHtml).join("") || `<div class="empty">该角色暂无可显示对话</div>`}</div>${pagingFooter()}<div class="chat-input"><textarea id="newChatText" placeholder="输入消息。加入上下文只记录这一轮；真实发送会请求模型回复。"></textarea><div class="send-stack"><button class="btn" id="addChat">加入上下文</button><button class="btn primary" id="sendChat">发送给模型</button></div></div></section>`;
  wirePaging();
  $$(".msg-card").forEach(card => card.ondblclick = () => openMessage(card.dataset.id));
  $("#openAssemblyFromChat").onclick = () => { currentView = "assembly"; render(); };
  $("#addChat").onclick = () => { const content = $("#newChatText").value.trim(); if (!content) return; pendingChatAutoScroll = true; send("item:create", { owner:selectedOwner, kind:"user", content }); };
  $("#sendChat").onclick = () => { const content = $("#newChatText").value.trim(); if (!content) return alertError("请输入要发送的内容"); pendingChatAutoScroll = true; send("chat:send", { owner:selectedOwner, content }); };
  $("#chatActivateBtn").onclick = () => send(info.active ? "character:deactivate" : "character:activate", { owner:selectedOwner });
  $("#historyBrowserBtn").onclick = openHistoryBrowser;
  requestAnimationFrame(() => { const log = $(".chat-log"); if (log && pendingChatAutoScroll) log.scrollTop = log.scrollHeight; });
}

function renderOwnerSelector(showGlobal) {
  const names = snapshot?.owners?.map(o => o.name) || [];
  if (!names.includes(selectedOwner)) selectedOwner = names[0] || "";
  ensureOwnerLoaded(selectedOwner);
  // 角色选择在固定左侧角色列完成，避免每个页面再放一套重复选择器。
}

function messageHtml(i) {
  const role = roleLabel(i);
  return `<article class="msg ${role} msg-card" data-id="${i.id}">
    <div class="who">${esc(role.slice(0,3))}</div>
    <div class="msg-body">
      <div class="msg-meta"><b>${esc(i.title)}</b> · ${esc(i.source)} · ≈${fmtTokens(i.estimatedTokens)} tokens</div>
      <div class="msg-content">${contentOrPending(i)}</div>
      ${(i.attachments || []).length ? `<div class="inline-attach">${i.attachments.map(attachmentChip).join("")}</div>` : ""}
    </div>
  </article>`;
}

// 条目正文可能没跟 state 一起来（后端按报文预算分页装填，装不下的标 truncated 且内容为空）。
// 这种情况必须明确写出来并给一键读取入口，绝不能渲染成空白 —— 那会被当成「消息是空的」。
function contentOrPending(i) {
  if (i.truncated && !(i.content || "").trim()) {
    return `<span class="pending-content">正文未随状态推送加载 <button class="link-btn" data-load-item="${esc(i.id)}">读取全文</button></span>`;
  }
  return formatText(i.content);
}

// 分页条：state / character-bundle 装不下的条目数，点一下走 items:page 补回来。
// 数量按**当前角色**取（snapshot.omittedByOwner），不能用全局 omittedItems ——
// 那是所有角色加起来的欠账，会把它当成当前角色的条数，用户点了半天也补不完。
function omittedFor(owner) {
  const per = snapshot?.omittedByOwner?.[owner];
  if (Number.isFinite(Number(per))) return Math.max(0, Number(per));
  return 0;
}
function pagingFooter() {
  const omitted = omittedFor(selectedOwner);
  if (omitted <= 0) return "";
  const total = ownerInfo(selectedOwner).count || 0;
  return `<div class="toolbar paging-bar"><span class="paging-note">「${esc(selectedOwner || "")}」共 ${total} 条，还有 <b>${omitted}</b> 条更早的条目未加载（为控制 IPC 报文体积，改为按需分页）</span><button class="btn" id="loadMoreItems">加载更早的 40 条</button></div>`;
}

function wirePaging() {
  const button = $("#loadMoreItems");
  if (button) button.onclick = () => {
    if (!selectedOwner) return;
    // offset 优先用后端回传的 nextOffset；没有时（还没翻过页）退回「已持有该角色的条数」。
    // 后端按同一个 OrderForFill 分页（当前角色优先 → 配置类优先 → 更新时间倒序），
    // 所以这个 offset 对得上的。
    const held = itemsBy(i => i.owner === selectedOwner).length;
    const offset = Number(snapshot?.pageOffsetByOwner?.[selectedOwner]);
    send("items:page", { owner: selectedOwner, offset: Number.isFinite(offset) ? offset : held, count: 40 });
    setStatus("正在加载更早的条目…");
  };
  $$("[data-load-item]").forEach(b => b.onclick = (e) => {
    e.stopPropagation();
    openMessage(b.dataset.loadItem);
    setStatus("正在读取全文…");
  });
}
function messageIndex(i) {
  const m = /#(\d+)/.exec(i.title || ""); return m ? Number(m[1]) : (i.sourceKey === "character-prompt" ? -2 : -1);
}

let memorySearchQuery = "";
function renderMemory() {
  if (renderInactiveContextPage("长期记忆")) return;
  head("长期记忆", "MemoryService 的磁盘结构天然分层：History.json 是逐条原始流水（原始记忆流），L1/L2/L3 是逐级压缩摘要。两层都能改，但改的对象不同 —— 见每个盒子里的说明。");
  renderOwnerSelector(false);
  const o = ownerInfo(selectedOwner);
  let history = itemsBy(i => i.owner === selectedOwner && i.category === "长期记忆");
  let archives = itemsBy(i => i.owner === selectedOwner && i.category === "归档记忆文件");
  // 关键词搜索过滤
  if (memorySearchQuery.trim()) {
    const q = memorySearchQuery.trim().toLowerCase();
    history = history.filter(i => (i.title || "").toLowerCase().includes(q) || (i.content || "").toLowerCase().includes(q));
    archives = archives.filter(i => (i.title || "").toLowerCase().includes(q) || (i.content || "").toLowerCase().includes(q));
  }
  const levels = [...new Set(archives.map(i => i.memoryLevel))].sort((a,b)=>a-b);
  const totalFound = history.length + archives.length;
  $("#pageBody").innerHTML += `
    <div class="memory-state ${o.memoryEnabled ? "on" : "off"}">
      <b>${o.memoryEnabled ? "MemoryService 已启用" : "MemoryService 未启用"}</b>
      <span>${o.memoryEnabled ? "对话由 Memory/History.json 与 L1/L2/L3 分层归档支撑。" : "未启用时对话主要存在实时内存，重启后通常不会保留；磁盘上已有文件仍可离线查看。"}</span>
    </div>
    <div class="toolbar">
      <input id="memorySearchInput" class="memory-search-input" placeholder="输入关键词搜索记忆内容…" value="${esc(memorySearchQuery)}" style="flex:1;min-width:200px;padding:8px 10px;border:1px solid var(--line-strong);border-radius:8px;font-size:12px;"/>
      <button class="btn" id="memorySearchClear">清除搜索</button>
      <span style="color:var(--muted);font-size:11px;align-self:center;">${memorySearchQuery.trim() ? `匹配 ${totalFound} 条` : ""}</span>
    </div>
    <div class="memory-nest">
      <section class="memory-box root"><h3>原始记忆流</h3><p class="memory-box-note">当前 <b>ChatHistory</b> 里真实存在的消息 —— 也就是下一次请求会原样发出去的内容。标记里的序号就是它在本轮上下文中的位置。<b>可以修改</b>：双击或点「编辑」改的是实时上下文里那一条消息，<b>不会</b>写进 Memory/L1·L2·L3 的归档文件。</p><div class="memory-list">${history.map(memoryRow).join("") || emptyMini(memorySearchQuery.trim() ? "没有匹配的记忆条目" : "没有识别到记忆标记")}</div></section>
      ${levels.map(lv => `<section class="memory-box L${lv}"><h3>L${lv} 压缩层</h3><p class="memory-box-note">MemoryService 写下的压缩归档文件 <code>Memory/L${lv}/*.txt</code>。这一层不是实时上下文，只有被 MemoryService 取用后才会进入对话。<b>可以修改</b>：双击或点「编辑」直接改写该文件。</p><div class="memory-list">${archives.filter(x=>x.memoryLevel===lv).map(memoryRow).join("") || emptyMini("本层暂无匹配条目")}</div></section>`).join("") || (memorySearchQuery.trim() && !archives.length ? emptyMini("归档层无匹配条目") : "")}
    </div>
    ${pagingFooter()}`;
  $$(".memory-item").forEach(el => el.ondblclick = () => openMessage(el.dataset.id));
  $$("[data-memory-edit]").forEach(b => b.onclick = (e) => { e.stopPropagation(); openMessage(b.dataset.memoryEdit); });
  wirePaging();
  $("#memorySearchInput").oninput = (e) => { memorySearchQuery = e.target.value; };
  $("#memorySearchInput").onkeydown = (e) => { if (e.key === "Enter") render(); };
  $("#memorySearchClear").onclick = () => { memorySearchQuery = ""; render(); };
}
function memoryRow(i) {
  return `<div class="memory-item" data-id="${i.id}"><b>${esc(i.title)}</b><span>${contentOrPending(i)}</span><em>${esc(i.source)} · ${fmtTokens(i.estimatedTokens)}${i.readOnly ? " · 只读" : ""}</em><div class="memory-item-actions"><button class="link-btn" data-memory-edit="${i.id}">编辑</button></div></div>`;
}

function renderMedia() {
  if (renderInactiveContextPage("角色附件与文件")) return;
  head("角色附件与文件", "这里按当前选中角色归类。它保留聊天中的图片、音频、文件引用和可读取的本地路径；记忆压缩后遗失的 base64 不会被伪装成附件。");
  const rows = itemsBy(i => i.owner === selectedOwner && ((i.attachments || []).length || ["image","audio","multimodal"].includes(i.modality)));
  const embedded = rows.reduce((count, item) => count + (item.attachments || []).filter(a => a.status === "embedded").length, 0);
  const referenced = rows.reduce((count, item) => count + (item.attachments || []).filter(a => a.status !== "embedded").length, 0);
  $("#pageBody").innerHTML = `<section class="media-owner-summary"><div><div class="eyebrow">${esc(selectedOwner || "未选择角色")}</div><h3>当前角色的附件仓库</h3><p>聊天与历史条目中的附件按角色归集；文件是否仍可用会明确显示。</p></div><div><b>${rows.length}</b><span>来源条目</span><b>${embedded}</b><span>内嵌内容</span><b>${referenced}</b><span>路径 / 引用</span></div></section><div class="media-grid">${rows.map(mediaCard).join("") || emptyMini("当前角色暂未发现可展示的多模态附件")}</div>${pagingFooter()}`;
  wirePaging();
}
function mediaCard(i) {
  const atts = (i.attachments && i.attachments.length) ? i.attachments : [{ modality:i.modality, status:"ok", path:i.path, title:i.title }];
  return `<article class="media-card source-${sourceClass(i)}">
    <div class="media-title"><b>${esc(i.owner)}</b><span>${esc(i.title)}</span></div>
    <div class="att-list">${atts.map(attachmentHtml).join("")}</div>
    <div class="media-meta">${esc(i.source)}</div>
  </article>`;
}
function sourceClass(i) { return i.origin === "live" ? "live" : "disk"; }
function attachmentChip(a) { return `<span class="att-chip ${a.status}">${esc(a.modality)} · ${esc(a.status || "ok")} ${esc(a.title || a.path || "")}</span>`; }
function attachmentHtml(a) {
  const title = esc(a.title || a.path || a.detail || a.modality);
  if (a.modality === "image" && a.status === "embedded") return `<div class="att-block"><img src="${a.path}" alt="${title}"/><small>Base64 内嵌图片</small></div>`;
  if (a.modality === "image" && a.status === "ok") return `<div class="att-block"><img src="${toFileUrl(a.path)}" alt="${title}" onerror="this.parentElement.innerHTML='<div class=\\'path-lost\\'>文件路径丢失<br>${esc(a.path)}</div>'"/><small>${title}<br>${esc(a.path)}</small></div>`;
  if (a.modality === "image") return `<div class="att-block path-lost">文件路径丢失<br><code>${esc(a.path || a.detail)}</code></div>`;
  if (a.modality === "audio") return `<div class="att-block filebox">♫ ${title}<small>${esc(a.path || a.detail)}</small></div>`;
  return `<div class="att-block filebox">${esc(a.modality)} / ${esc(a.status || "reference")}<small>${esc(a.path || a.path || a.detail || "")}</small></div>`;
}
function toFileUrl(p) {
  if (!p || /^(data:|https?:|file:)/i.test(p)) return p || "";
  return "file:///" + p.replace(/\\/g,"/").split("/").map(encodeURIComponent).join("/");
}

function renderWorldBook(matchedOnly = false) {
  if (renderInactiveContextPage("世界书 / Lorebook")) return;
  // 此页面可在“只看命中条目 / 显示全部”之间直接切换，先清空避免追加到旧内容下方。
  $("#pageBody").innerHTML = "";
  head("世界书 / Lorebook", "由 ContextManager 独立管理，存储在角色目录下；支持关键词、常驻、启用状态、排序和注入预览。结构借鉴酒馆，但不绑定酒馆文件格式。");
  renderOwnerSelector(false);
  const book = worldBooks[selectedOwner] || { entries: [] };
  const meta = worldBookMeta[selectedOwner] || {};
  const cutCount = book.entries.filter(worldEntryTruncated).length;
  const remaining = Math.max(0, Number(meta.remaining) || 0);
  const total = Math.max(book.entries.length, Number(meta.total) || 0);
  // 只有「正文被截断」或「后端还有没下发的条目」时才需要读全文按钮。
  const needFull = cutCount > 0 || remaining > 0;
  const loadAllBtn = needFull ? `<button class="btn warn" id="loadAllWorld" data-world-load-all>读取全部正文</button>` : "";
  const cutBanner = needFull
    ? `<div class="plan-help plan-help-strong">${cutCount ? `有 ${cutCount} 条条目的正文没有随报文下发（报文体积上限），这里是预览` : ""}${cutCount && remaining ? "；" : ""}${remaining ? `另有 ${remaining} 条条目尚未读取回来` : ""}。点单条的「读取全文」按需读取，或点「读取全部正文」分页读回全部（共 ${total} 条）。</div>`
    : "";
  $("#pageBody").innerHTML += `
    <div class="toolbar"><button class="btn" id="importWorld">导入世界书</button><button class="btn primary" id="addEntry">新增条目</button><button class="btn" id="saveWorld">保存世界书</button><button class="btn" id="worldMatch">按当前对话匹配</button><button class="btn" id="worldClearMatch">显示全部</button><button class="btn" id="worldPreview">预览注入内容</button>${loadAllBtn}</div>
    ${cutBanner}
    <div class="world-grid">${book.entries.filter(e => !matchedOnly || worldEntryTriggered(e, selectedOwner)).map(worldEntryHtml).join("") || emptyMini("暂无匹配条目；常驻条目也会显示")}</div>`;
  $("#importWorld").onclick=()=>send("worldbook:import",{owner:selectedOwner});
  $("#addEntry").onclick = () => { book.entries.push({ id:moduleId(), title:"新条目", keywords:[], content:"", enabled:true, constant:false, insertionOrder:100 }); updateWorldUi(); };
  $("#saveWorld").onclick = saveWorld;
  $("#worldPreview").onclick = previewWorld;
  $("#worldMatch").onclick = () => renderWorldBook(true);
  $("#worldClearMatch").onclick = () => renderWorldBook(false);
  bindWorldEditors(book);
}
function worldEntryHtml(e) {
  // 后端为了不把 IPC 桥顶爆，正文可能只随报文下发了预览（e.truncated）。
  // 标出来 + 只读，否则用户改的是预览，保存时被磁盘上的原文覆盖。
  const cut = worldEntryTruncated(e);
  return `<article class="card world-entry ${e.enabled ? "" : "disabled"}${cut ? " truncated" : ""}" data-id="${esc(e.id)}">
    <input class="world-title" value="${esc(e.title)}" data-field="title"/>
    <textarea class="world-content" data-field="content"${cut ? " readonly" : ""}>${esc(e.content)}</textarea>
    ${cut ? `<div class="trunc-note">正文未随报文下发，这里只是预览（全文 ${Number(e.contentLength || 0).toLocaleString()} 字）。<button class="btn tiny warn" data-world-load="${esc(e.id)}">读取全文</button></div>` : ""}
    <input class="world-keys" data-field="keywords" value="${esc((e.keywords||[]).join(", "))}" placeholder="关键词，用逗号分隔"/>
    <div class="world-foot"><label><input type="checkbox" data-field="enabled" ${e.enabled?"checked":""}/>启用</label><label><input type="checkbox" data-field="constant" ${e.constant?"checked":""}/>常驻</label><input class="order" type="number" data-field="insertionOrder" value="${Number(e.insertionOrder||100)}"></div>
  </article>`;
}
function updateWorldUi() { worldBooks[selectedOwner] = worldBooks[selectedOwner] || { entries:[] }; renderWorldBook(); }
function bindWorldEditors(book) {
  $$(".world-entry").forEach(card => {
    const entry = book.entries.find(e => e.id === card.dataset.id);
    if (!entry) return;
    // 截断条目的正文是预览，绑了 oninput 反而会让用户以为改动了内容；
    // 全文由「读取全文」取回，保存时后端按 id 用磁盘原文回填。
    const frozen = worldEntryTruncated(entry);
    $$("input,textarea", card).forEach(el => {
      const f = el.dataset.field;
      if (frozen && f === "content") { el.oninput = null; return; }
      el.oninput = () => {
        if (f === "keywords") entry.keywords = el.value.split(",").map(x=>x.trim()).filter(Boolean);
        else if (["enabled","constant"].includes(f)) entry[f] = el.checked;
        else if (f === "insertionOrder") entry[f] = Number(el.value || 100);
        else entry[f] = el.value;
      };
    });
  });
}
function saveWorld() {
  const entries = (worldBooks[selectedOwner]||{entries:[]}).entries;
  // 截断条目的 content 只是预览，但后端 SaveWorldBook 会按 id 保留磁盘上的原文，
  // 所以这里照常提交，只是把「哪些是预览」讲清楚，免得用户以为保存的是预览。
  const cut = entries.filter(worldEntryTruncated).length;
  send("worldbook:save", { owner:selectedOwner, entries });
  setStatus(cut > 0
    ? `正在保存世界书…（其中 ${cut} 条正文是预览，将保留磁盘上的原文）`
    : "正在保存世界书…");
}
function previewWorld() {
  const text = (worldBooks[selectedOwner]?.entries || []).filter(e=>e.enabled).sort((a,b)=>a.insertionOrder-b.insertionOrder)
    .map(e=>`【${e.title}】\n${e.content}`).join("\n\n");
  openModal("世界书注入预览", `<pre class="preview-code">${esc(text)}</pre>`);
}

function renderAssembly() {
  if (renderInactiveContextPage("上下文装配")) return;
  requestNativePrompt(selectedOwner);
  head("上下文装配", "单击模块在右侧修改；点击编辑打开大窗口。启用的模块按区域与列表顺序发送。");
  ensureOwnerLoaded(selectedOwner);
  if (planOwner !== selectedOwner) { planOwner = selectedOwner; planState = null; planLoading = false; planError = ""; planDirty = false; selectedAssemblyModule = ""; }
  if (!planState && !planLoading && selectedOwner) {
    // The editor must remain usable even if an Electron IPC reply is lost.  Keep a
    // local draft immediately, then let a later plan-state replace it when available.
    planState = localDefaultPlan(selectedOwner);
    planDirty = false;
    planLoading = false;
    send("plan:get", {owner:selectedOwner});
    setStatus("已打开本地草稿；正在同步已保存的计划…");
  }
  const plan = planState;
  // 这里以前会在每次渲染时“只要 native 为空就用磁盘 Prompt 回填”。那会让用户在装配页
  // 主动「重置为空白」的结果一刷新就消失，所以改成只在完全没有草稿时才兜底
  // （localDefaultPlan 已经用完整官方系统消息填好了）。后端 FillNativePlanPrompt 同理。
  const diskPrompt = nativeSystemContent(selectedOwner);
  if (plan && !plan.modules.length && diskPrompt && !planDirty) {
    plan.modules.push({ id:moduleId(), name:'角色设定 #0', role:'system', content:diskPrompt, enabled:true, group:'system', source:'native', constant:true, keywords:[] });
  }
  $("#pageBody").innerHTML = `<div class="plan-editor" data-plan-editor="${esc(selectedOwner)}">
    <header class="plan-header"><div class="plan-header-doc">
      <span class="eyebrow">覆盖方式：插件覆盖 vs 本地覆盖</span>
	  <p><b>插件覆盖</b>（默认，不动角色文件）—— 只在每次发送请求前把上下文重排一遍；随时可以关，关掉就回到角色原本的设定。插件启动时自动生效。</p>
      <p><b>本地覆盖</b>（写进角色文件）—— 把装配结果直接写进该角色 index.json 的 Prompt，就是将所有内容写到了角色原本的设定里。角色重启生效。想撤回要用「还原原始设定」。</p>
      <p class="plan-header-hint">模块从上至下依次写入请求。单击卡片在右侧编辑，点“编辑”打开大窗口。</p>
    </div><div class="plan-header-actions"><button class="btn" id="previewPlan">预览</button><button class="btn" id="saveDraft">保存草稿</button><button class="btn primary" id="applyPlan">应用到当前角色</button></div></header>
    <section class="plan-control-bar"><div class="plan-control"><span>覆盖方式</span><select id="planMode" ${plan ? "" : "disabled"}>${[["Off","关闭"],["Temporary","插件覆盖"],["Permanent","本地覆盖"]].map(([v,n])=>`<option value="${v}" ${plan?.mode===v?"selected":""}>${n}</option>`).join("")}</select></div><label class="plan-control" title="勾选后，装配和应用时会把 {{char}}、{{user}}、{{description}} 等酒馆宏替换成当前角色数据；取消勾选则保留原样文本。"><input id="planMacros" type="checkbox" ${plan?.applyMacros!==false?"checked":""}/><span>启用 Handlebars 宏</span></label><label class="plan-control" title="本插件没有酒馆那么丰富的宏能力：勾上后，没实现也没定义的宏（例如 {{lastPrompt}}）直接填成它自己的名字，不再原样保留。{{char}} 仍取角色名。"><input id="planAutoMacros" type="checkbox" ${plan?.autoMacros!==false?"checked":""} ${plan?"":"disabled"}/><span>自动宏应用</span></label><label class="plan-control" title="勾上后，只有预览的模块/世界书条目会在后台自动逐条读回全文（串行、限速，不用你手点「读取全文」）。关掉则维持「点了才读」。这只是把同一批正文分多次拿回来，不会让任何一条报文变大。"><input id="planAutoFull" type="checkbox" ${autoFullText?"checked":""}/><span>自动读取全文</span></label><span class="plan-help">勾选：替换酒馆变量；取消：按原文发送</span><button class="btn quiet" id="macroSettings" title="设置 {{user}} 的取值、补充自定义宏，并查看可用宏清单">宏设置</button><button class="btn quiet" id="restorePlan" title="恢复本地覆盖前保存的原始角色设定，并清除当前插件覆盖计划">还原原始设定</button></section>
    <section class="plan-import-bar"><span>导入</span><select id="planPreset"><option value="">${presetNames.length?'选择酒馆预设或导入 JSON':'暂无酒馆预设，点击右侧导入 JSON'}</option>${presetNames.map(n=>`<option value="${esc(n)}" ${n===selectedPreset?"selected":""}>${esc(n)}</option>`).join("")}</select><button class="btn" data-plan-import="preset">酒馆预设</button><button class="btn" data-plan-import="card" title="选择 .json / .png 角色卡。如果卡里内嵌了世界书（character_book），会弹窗问你要不要一起导入；选择可以在酒馆兼容页改成「总是 / 从不」。">角色卡</button><button class="btn" data-plan-import="worldbook">世界书</button><span class="plan-import-sep"></span><button class="btn" id="charPresetBtn" title="把当前方案存成该角色的上下文记录，或读取/导入/导出之前保存的记录">角色预设</button><button class="btn" id="charPresetImportBtn" title="从 .json 文件导入一份角色上下文记录到当前角色">导入角色记录</button></section>
    ${plan && plan.mode==='Temporary' ? `<div class="plan-help plan-help-strong">「插件覆盖」已启用：该角色保存的计划会在插件加载/角色激活时自动侧装，直接对话即可生效，无需再手动点“应用到当前角色”。</div>` : ''}
    ${plan ? renderAssemblyFilterBar() + renderAssemblyLanes(plan) : `<div class="empty">${planLoading ? '计划加载中…' : (planError || '计划未返回。')} ${!planLoading ? '<button class="btn" id="retryPlan">重试</button>' : ''}</div>`}
  </div>`;
  $("#planMode").onchange=e=>{if(planState){planState.mode=e.target.value;planDirty=true;}};
  $("#planMacros").onchange=e=>{if(planState){planState.applyMacros=e.target.checked;planDirty=true;}};
  $("#planAutoMacros").onchange=e=>{if(planState){planState.autoMacros=e.target.checked;planDirty=true;submitPlan("plan:save","正在保存宏设置…",false);}};
  // 「自动读取全文」是纯前端偏好（不影响发给模型的内容），存 localStorage 即可，不必回后端。
  $("#planAutoFull").onchange=e=>{
    autoFullText=e.target.checked;saveAutoFullText();
    if(!autoFullText){resetAutoFetch();setStatus("已关闭自动读取全文：需要时请手动点「读取全文」");return;}
    setStatus("已开启自动读取全文，正在补回缺少的正文…");
    autoFetchPlanModules();
    const meta=worldBookMeta[selectedOwner];
    if(meta&&(meta.truncatedCount>0||meta.remaining>0))loadAllWorldContent();
  };
  $("#pageBody").oninput=e=>{if(e.target.closest('.plan-editor'))planDirty=true;};
  $("#planPreset").onchange=e=>{selectedPreset=e.target.value;};
  $("#retryPlan")?.addEventListener("click", () => { clearTimeout(planTimer); planLoading=false; planError=""; planState=null; planOwner=""; renderAssembly(); });
  $("#saveDraft").onclick=()=>submitPlan("plan:save","正在保存草稿…");
  $("#previewPlan").onclick=()=>submitPlan("plan:preview","正在生成装配预览…");
  $("#applyPlan").onclick=()=>submitPlan("plan:apply","正在应用到当前角色…");
  $("#restorePlan").onclick=()=>{if(confirm('还原会清除当前装配计划和插件覆盖，并在存在备份时恢复本地覆盖前的原始角色设定。继续吗？'))send("plan:reset",{owner:selectedOwner});};
  $("#macroSettings").onclick=()=>openMacroSettings();
  $("#charPresetBtn").onclick=()=>openCharacterPresetPanel();
  $("#charPresetImportBtn")?.addEventListener("click",()=>importCharacterPreset());
  $("#filterView")&&($("#filterView").onchange=e=>{assemblyFilter.view=e.target.value;saveAssemblyFilter();render();});
  $("#filterGroup")&&($("#filterGroup").onchange=e=>{assemblyFilter.group=e.target.value;saveAssemblyFilter();render();});
  $("#filterSource")&&($("#filterSource").onchange=e=>{assemblyFilter.source=e.target.value;saveAssemblyFilter();render();});
  $$("[data-plan-import]").forEach(b=>b.onclick=()=>{
    if(!planState)return alertError("装配计划尚未就绪");
    commitSidebar(); cachePlan(selectedOwner,planState); planDirty=false;
    const kind=b.dataset.planImport;
    const fromLibrary=kind==='preset' && selectedPreset;
    setStatus(fromLibrary ? `正在导入酒馆预设 ${selectedPreset}…` : '正在选择导入文件…');
    // 不再发 withCardWorldbook：选完文件后，后端会自己读卡、检测 character_book，
    // 有的话弹窗问用户（带「记住我的选择」）。前端预判不了卡里有没有世界书。
    send(fromLibrary ? 'plan:import' : 'plan:import-file',{owner:selectedOwner,plan:planState,kind,name:selectedPreset});
  });
  $$("[data-plan-add]").forEach(b=>b.onclick=()=>{
    const group=b.dataset.planAdd, m={id:moduleId(),name:"新模块",role:"system",content:"",enabled:true,group,source:"custom",constant:true,keywords:[]};
    const at=planState.modules.reduce((last,m,index)=>(m.group||'system')===group?index:last,-1);
    if(at>=0) planState.modules.splice(at+1,0,m); else {planState.modules.push(m); sortPlanGroups();}
    selectedAssemblyModule=m.id; planDirty=true;
    submitPlan('plan:save','正在新增模块…',false);
    render();
  });
  $$("[data-plan-add-source]").forEach(b=>b.onclick=()=>{
    if(!planState)return alertError("装配计划尚未就绪");
    const src=b.dataset.planAddSource;
    const group=(assemblyFilter.group!=='all'?assemblyFilter.group:'system');
    const m={id:moduleId(),name:"新模块",role:"system",content:"",enabled:true,group,source:src,constant:true,keywords:[]};
    planState.modules.push(m); sortPlanGroups();
    selectedAssemblyModule=m.id; planDirty=true;
    submitPlan('plan:save','正在新增模块…',false);
    render();
  });
  $$("[data-drop-unchecked]").forEach(b=>b.onclick=()=>{
    const g=b.dataset.dropUnchecked;
    bulkDeleteModules(m=>(m.group||'system')===g && !m.enabled,
      `范围：「${groupLabel(g)}」中取消勾选的模块。`, '该区域没有取消勾选的模块');
  });
  $$("[data-clear-group]").forEach(b=>b.onclick=()=>{
    const g=b.dataset.clearGroup;
    bulkDeleteModules(m=>(m.group||'system')===g,
      `范围：「${groupLabel(g)}」的全部模块（运行时消息不受影响）。`, '该区域已经是空的');
  });
  $$("[data-drop-unchecked-src]").forEach(b=>b.onclick=()=>{
    const s=b.dataset.dropUncheckedSrc;
    bulkDeleteModules(m=>(m.source||'custom')===s && !m.enabled,
      `范围：来源为「${planSourceLabels[s]||s}」且取消勾选的模块。`, '该来源没有取消勾选的模块');
  });
  $$("[data-clear-source]").forEach(b=>b.onclick=()=>{
    const s=b.dataset.clearSource;
    bulkDeleteModules(m=>(m.source||'custom')===s,
      `范围：来源为「${planSourceLabels[s]||s}」的全部模块。`, '该来源已经是空的');
  });
  $$("[data-reset-system]").forEach(b=>b.onclick=()=>{
    if(!planState)return alertError("装配计划尚未就绪");
    const permanent=planState.mode==='Permanent';
    const warn=permanent?'\n\n注意：当前是「本地覆盖」，重新应用后角色 index.json 的 Prompt 会被写成空内容（之后可用「还原原始设定」恢复）。':'';
    if(!confirm('将清空「系统提示词」的全部模块，只保留一个空的官方角色设定模块。'+warn+'\n\n继续吗？'))return;
    planState.modules=planState.modules.filter(m=>(m.group||'system')!=='system' || m.source==='framework');
    planState.modules.push({id:moduleId(),name:'角色设定 #0',role:'system',content:'',enabled:true,group:'system',source:'native',constant:true,keywords:[]});
    sortPlanGroups();
    afterBulkEdit('已重置系统提示词为空白角色设定');
  });
  $$("[data-plan-select]").forEach(el=>{
    el.onclick=e=>{if(e.target.closest('button,input,label'))return;commitSidebar();selectedAssemblyModule=el.dataset.planSelect;renderInspector();};
    el.ondblclick=e=>{if(e.target.closest('button,input,label'))return;commitSidebar();selectedAssemblyModule=el.dataset.planSelect;openPlanModal();};
  });
  $$("[data-plan-edit]").forEach(b=>b.onclick=e=>{e.stopPropagation();selectedAssemblyModule=b.dataset.planEdit;openPlanModal();});
  $$("[data-plan-toggle]").forEach(b=>b.onchange=()=>{planState.modules.find(m=>m.id===b.dataset.planToggle).enabled=b.checked;planDirty=true;submitPlan('plan:save','正在保存模块启用状态…',false);render();});
  $$("[data-framework-id]").forEach(b=>{
    const selectLive=()=>{const item=snapshot.items.find(i=>i.id===b.dataset.frameworkId);if(!item)return;commitSidebar();const override=frameworkOverride(item);selectedAssemblyModule=override.id;renderInspector();};
    b.onclick=e=>{if(e.target.closest('button,input,label'))return;selectLive();};
    b.ondblclick=e=>{if(e.target.closest('button,input,label'))return;selectLive();openPlanModal();};
  });
  $$("[data-framework-edit]").forEach(button=>button.onclick=e=>{e.stopPropagation();const item=snapshot.items.find(i=>i.id===button.dataset.frameworkEdit);if(!item)return;const override=frameworkOverride(item);selectedAssemblyModule=override.id;openPlanModal();});
  $$("[data-framework-toggle]").forEach(b=>b.onchange=()=>{const item=snapshot.items.find(i=>i.id===b.dataset.frameworkToggle);if(item){frameworkOverride(item).enabled=b.checked;planDirty=true;submitPlan('plan:save','正在保存参与装配状态…',false);}render();});
  $$('[data-prune-chat]').forEach(button=>button.onclick=()=>{
    const excluded=frameworkItems('chat').filter(item=>planState.modules.find(m=>m.source==='framework'&&m.targetIndex===messageIndex(item))?.enabled===false);
    if(!excluded.length){setStatus('没有未参与装配的对话');return;}
    if(confirm(`将从当前实时 ChatHistory 删除 ${excluded.length} 条未参与装配的 user/assistant 消息。此操作会改变当前会话历史，是否继续？`)){
      planDirty=false;
      setStatus(`正在删除 ${excluded.length} 条未参与装配的对话…`);
      send('chat:prune',{owner:selectedOwner,plan:planState,indices:excluded.map(messageIndex)});
    }
  });
}
function renderAssemblyFilterBar(){
  return `<section class="plan-filter-bar">
    <div class="plan-control"><span>视图</span><select id="filterView"><option value="group" ${assemblyFilter.view!=='source'?'selected':''}>按区域（发送顺序）</option><option value="source" ${assemblyFilter.view==='source'?'selected':''}>按来源（酒馆预设/角色卡/世界书）</option></select></div>
    <div class="plan-control"><span>区域</span><select id="filterGroup"><option value="all">全部</option>${planGroups.map(([g,n])=>`<option value="${g}" ${assemblyFilter.group===g?'selected':''}>${esc(n)}</option>`).join('')}</select></div>
    <div class="plan-control"><span>来源</span><select id="filterSource"><option value="all">全部</option>${planSources.map(([s,n])=>`<option value="${s}" ${assemblyFilter.source===s?'selected':''}>${esc(n)}</option>`).join('')}</select></div>
    <span class="plan-help">酒馆预设 / 角色卡 / 世界书 各自成块，可单独筛选后修改。</span>
  </section>`;
}
function renderAssemblyLanes(plan){
  return assemblyFilter.view==='source' ? renderAssemblyBySource(plan) : renderAssemblyByGroup(plan);
}
function planModuleCard(m,plan,showRegion){
  // 「插件覆盖」下 native 模块的真实内容就是 m.content（请求前会替换 index[0]）；
  // 显示运行时的 index[0] 会让人以为“覆盖没生效”。
  const display=m.source==='native'?nativeModuleDisplay(selectedOwner,m):m.content;
  const role=String(m.role||'system').toLowerCase();
  const meta=showRegion?`${esc(planSourceLabels[m.source]||'自定义')} · ${esc((planGroups.find(g=>g[0]===(m.group||'system'))||[,'系统提示词'])[1])}`:esc(planSourceLabels[m.source]||'自定义');
  // 后端为了不把 IPC 桥顶爆，模块正文可能只随报文下发了预览（m.truncated）。
  // 这里必须显式标出来 —— 否则用户会以为模块内容真的只有这么点，编辑也是在改预览。
  const cut=moduleTruncated(m);
  const lengthNote=cut&&m.contentLength?` · 全文 ${Number(m.contentLength).toLocaleString()} 字`:'';
  const badge=cut?`<span class="trunc-badge" title="正文超过 ${TRUNC_KEEP_CHARS} 字，报文里只带了预览；点「读取全文」查看完整内容">预览${esc(lengthNote)}</span>`:'';
  const loadBtn=cut?`<button class="btn tiny warn" data-plan-load="${esc(m.id)}">读取全文</button>`:'';
  return `<article class="assembly-module ${selectedAssemblyModule===m.id?'selected':''} ${cut?'truncated':''}" data-plan-select="${esc(m.id)}"><div class="module-card-top"><span class="module-order">${m.enabled ? plan.modules.filter(x=>x.enabled).indexOf(m)+1 : '—'}</span><b>${esc(m.name)}</b><span class="identity-badge ${esc(role)}">${esc(role)}</span>${badge}${loadBtn}<button class="btn tiny" data-plan-edit="${esc(m.id)}">编辑</button></div><p class="module-preview">${esc(display ? display.replace(/\s+/g,' ').slice(0,600) : '角色设定为空：请在右侧读取或编辑角色 Prompt')}</p><div class="module-card-meta"><span>${meta}</span><label><input type="checkbox" data-plan-toggle="${esc(m.id)}" ${m.enabled?'checked':''}/>参与装配</label></div></article>`;
}
function renderAssemblyByGroup(plan){
  return planGroups.filter(([g])=>assemblyFilter.group==='all'||assemblyFilter.group===g).map(([group,label])=>{
    const all=plan.modules.filter(m=>(m.group||"system")===group && m.source!=='framework');
    const live=frameworkItems(group);
    const sources=assemblyFilter.source==='all'?planSources:planSources.filter(([s])=>s===assemblyFilter.source);
    const blocks=sources.map(([src,sname])=>{
      const list=all.filter(m=>(m.source||'custom')===src);
      if(!list.length) return '';
      return `<div class="source-block"><div class="source-head"><span class="source-chip ${esc(src)}">${esc(sname)}</span><span class="source-count">${list.length} 个模块</span></div><div class="module-stack">${list.map(m=>planModuleCard(m,plan,false)).join('')}</div></div>`;
    }).join('');
    const unchecked=all.filter(m=>!m.enabled).length;
    const actions=`<div class="lane-actions">
      ${unchecked?`<button class="btn tiny" data-drop-unchecked="${esc(group)}" title="删除本区域内取消勾选的模块">删除未勾选 (${unchecked})</button>`:''}
      ${all.length?`<button class="btn tiny danger" data-clear-group="${esc(group)}" title="删除本区域内的全部模块（运行时消息不受影响）">清空本区域</button>`:''}
      ${group==='system'?`<button class="btn tiny danger" data-reset-system title="清空系统提示词，只保留一个空的官方角色设定模块">重置为空白</button>`:''}
      ${group==='chat'?`<button class="btn tiny danger" data-prune-chat title="从实时 ChatHistory 删除未参与装配的对话消息">删除未参与装配的对话</button>`:''}
    </div>`;
    return `<section class="assembly-lane" data-lane-group="${esc(group)}"><div class="lane-body"><div class="lane-head"><h4>${esc(label)}</h4>${actions}</div>
      ${group==='system'?'':`<div class="module-stack">${live.map(frameworkModuleCard).join('')}</div>`}
      ${blocks}
      ${!all.length && (group==='system'||!live.length) ? '<div class="empty-slot">该区域暂无模块，可点击下方“新增模块”。</div>' : ''}
      ${!live.length && group!=='system' && !ownerInfo(selectedOwner).active ? '<div class="empty-slot">角色未激活；运行时模块将在激活后显示</div>':''}
      <button class="btn" data-plan-add="${esc(group)}">+ 新增模块</button></div></section>`;
  }).join('');
}
function renderAssemblyBySource(plan){
  const sources=assemblyFilter.source==='all'?planSources:planSources.filter(([s])=>s===assemblyFilter.source);
  const inGroup=m=>assemblyFilter.group==='all'||(m.group||'system')===assemblyFilter.group;
  const sections=sources.map(([src,sname])=>{
    const list=plan.modules.filter(m=>m.source!=='framework' && (m.source||'custom')===src && inGroup(m))
      .sort((a,b)=>planGroups.findIndex(g=>g[0]===(a.group||'system'))-planGroups.findIndex(g=>g[0]===(b.group||'system')));
    if(!list.length) return '';
    const unchecked=list.filter(m=>!m.enabled).length;
    return `<section class="assembly-lane source-lane"><div class="lane-body"><div class="lane-head"><h4><span class="source-chip ${esc(src)}">${esc(sname)}</span></h4><div class="lane-actions"><span class="source-count">${list.length} 个模块</span>${unchecked?`<button class="btn tiny" data-drop-unchecked-src="${esc(src)}" title="删除本来源中取消勾选的模块">删除未勾选 (${unchecked})</button>`:''}<button class="btn tiny danger" data-clear-source="${esc(src)}" title="删除本来源的全部模块">清空本来源</button><button class="btn tiny" data-plan-add-source="${esc(src)}">+ 新增</button></div></div><div class="module-stack">${list.map(m=>planModuleCard(m,plan,true)).join('')}</div></div></section>`;
  }).join('');
  const liveAll=[...frameworkItems('features'),...frameworkItems('chat')].filter(i=>assemblyFilter.group==='all'||(assemblyFilter.group==='features'?messageIndex(i)>0:true));
  const liveSection=(assemblyFilter.source==='all' && liveAll.length) ? `<section class="assembly-lane source-lane"><div class="lane-body"><div class="lane-head"><h4><span class="source-chip framework">Alife 运行时</span></h4><div class="lane-actions"><span class="source-count">${liveAll.length} 个模块</span><button class="btn tiny danger" data-prune-chat>删除未参与装配的对话</button></div></div><div class="module-stack">${liveAll.map(frameworkModuleCard).join('')}</div></div></section>` : '';
  return (sections + liveSection) || '<div class="empty">当前筛选下没有模块。</div>';
}
function groupLabel(g){ return (planGroups.find(x=>x[0]===g)||[,'模块'])[1]; }
// 批量删除模块：只动计划里的模块，运行时（framework）消息永远不在这里被删。
function bulkDeleteModules(match, detail, emptyMessage){
  if(!planState) return alertError("装配计划尚未就绪");
  const drop=planState.modules.filter(m=>m.source!=='framework' && match(m));
  if(!drop.length) return setStatus(emptyMessage);
  if(!confirm(`将删除 ${drop.length} 个模块。\n\n${detail}\n\n继续吗？`)) return;
  planState.modules=planState.modules.filter(m=>!(m.source!=='framework' && match(m)));
  afterBulkEdit(`已删除 ${drop.length} 个模块`);
}
// 批量编辑之后：清掉选中项 → 保存草稿；若当前不是「关闭」模式则一并重新应用，
// 这样「插件覆盖」会更新 applied.json，「本地覆盖」会重写 index.json 的 Prompt。
function afterBulkEdit(message){
  selectedAssemblyModule='';
  const modeLabel=({Temporary:'插件覆盖',Permanent:'本地覆盖'})[planState.mode];
  if(modeLabel) submitPlan('plan:apply', `${message}；正在按「${modeLabel}」重新应用…`);
  else submitPlan('plan:save', `${message}（当前模式为「关闭」，仅保存草稿，未写回角色设定）`);
  render();
}
function openMacroSettings(){
  if(!planState) return alertError("装配计划尚未就绪");
  const custom=planState.customMacros||{};
  const customText=Object.keys(custom).map(k=>`${k}=${custom[k]}`).join("\n");
  const builtin=["char","user","description","personality","scenario","system","mesExamples","postHistoryInstructions","charCreatorNotes","greeting","greeting2","greeting3","alternateGreetings","greetingCount","newline","noop","original","charPrompt","systemPrompt","charDescription","charPersonality","charScenario"];
  const autoOn=planState.autoMacros!==false;
  openModal("宏设置", `
    <p class="plain-text">装配与应用时，酒馆宏会按这里的定义替换。未定义的宏不再报错，而是原样保留并提示。</p>
    <div class="field"><label>{{user}} 的取值（对话中代表“我”的名字）</label><input id="macroUser" value="${esc(planState.userName||'User')}"/></div>
    <label class="ask-remember macro-auto"><input type="checkbox" id="macroAuto" ${autoOn?'checked':''}/><span><b>自动宏应用</b>：本插件没有酒馆那么丰富的宏能力，勾上后把没实现也没定义的宏直接填成它自己的名字</span></label>
    <p class="plain-text macro-auto-hint">规则：<code>{{char}}</code> = 角色名，<code>{{user}}</code> = 上面填的名字，角色卡里能读到的字段（描述 / 性格 / 场景 / 开场白…）各用各的取值；其余例如 <code>{{lastPrompt}}</code>、<code>{{time}}</code> 这类本插件不支持的宏，就替换成 <code>lastPrompt</code>、<code>time</code> 本身，不再在预览里堆一长串「未识别的宏」。关掉它则恢复原样保留 <code>{{lastPrompt}}</code> 并给出提示。</p>
    <div class="field"><label>自定义宏（每行一个，格式 <code>名称=替换文本</code>；会覆盖同名内置宏）</label><textarea id="macroCustom" class="inspector-content" placeholder="例如：\npersona=我的角色设定\nrules=请保持简洁">${esc(customText)}</textarea></div>
    <details class="official-preview" open><summary>可用内置宏（点击名称可插入自定义宏模板）</summary><div class="macro-list">${builtin.map(k=>`<code data-macro-insert="${esc(k)}">{{${esc(k)}}}</code>`).join('')}<code>{{setvar::名::值}}</code><code>{{getvar::名}}</code><code>{{#if 名}}…{{else}}…{{/if}}</code><code>{{!注释}}</code><code>{{//注释}}</code></div></details>
    <div class="toolbar modal-actions"><button class="btn" id="macroCancel">取消</button><button class="btn primary" id="macroSave">保存并同步</button></div>`);
  $("#macroCancel").onclick=closeModal;
  $$("[data-macro-insert]").forEach(el=>el.onclick=()=>{const ta=$("#macroCustom");ta.value=(ta.value?ta.value.replace(/\n?$/,"\n"):"")+el.dataset.macroInsert+"=";ta.focus();});
  $("#macroSave").onclick=()=>{
    planState.userName=$("#macroUser").value.trim()||"User";
    planState.autoMacros=$("#macroAuto").checked;
    const map={};
    ($("#macroCustom").value||"").split(/\r?\n/).forEach(line=>{
      const i=line.indexOf("="); if(i<=0) return;
      const k=line.slice(0,i).trim().replace(/^\{\{|\}\}$/g,""); if(!k) return;
      map[k]=line.slice(i+1);
    });
    planState.customMacros=map;
    planDirty=true;
    closeModal();
    submitPlan("plan:save","正在保存宏设置…");
  };
}
const planGroups=[["system","系统提示词"],["features","功能模块"],["memory","MemoryService 上下文"],["chat","对话窗口"]];
function sortPlanGroups(){planState.modules.sort((a,b)=>planGroups.findIndex(g=>g[0]===(a.group||'system'))-planGroups.findIndex(g=>g[0]===(b.group||'system')));}
function frameworkItems(group){
  const rows=byOwner(selectedOwner).filter(i=>i.sourceKey==='live-system'||i.sourceKey==='live-history'||i.sourceKey==='offline-history');
  if(group==='features') return rows.filter(i=>i.sourceKey==='live-system'&&messageIndex(i)>0);
  if(group==='memory') return byOwner(selectedOwner).filter(i=>i.category==='长期记忆');
  if(group==='chat') return rows.filter(i=>i.sourceKey!=='live-system'&&i.category!=='长期记忆').sort((a,b)=>messageIndex(a)-messageIndex(b));
  return [];
}
function frameworkModuleCard(i){
 const feature=i.sourceKey==='live-system'&&messageIndex(i)>0;
 const m=feature?planState?.modules.find(m=>m.source==='framework'&&m.targetIndex===messageIndex(i)):null;
 const role=String(m?.role||i.kind||'system').toLowerCase();
 const chat=i.sourceKey==='live-history';
 const participation=planState?.modules.find(m=>m.source==='framework'&&m.targetIndex===messageIndex(i));
 return `<article class="assembly-module framework-module" data-framework-id="${esc(i.id)}"><div class="module-card-top"><span class="module-order">${messageIndex(i)}</span><b>${esc(i.title)}</b><span class="identity-badge ${esc(role)}">${esc(role)}</span>${feature?`<button class="btn tiny" data-framework-edit="${esc(i.id)}">编辑</button><label class="module-enabled"><input type="checkbox" data-framework-toggle="${esc(i.id)}" ${m?.enabled!==false?'checked':''}/>参与装配</label>`:chat?`<label class="module-enabled"><input type="checkbox" data-framework-toggle="${esc(i.id)}" ${participation?.enabled!==false?'checked':''}/>参与装配</label>`:'<span></span>'}</div><p class="module-preview">${esc(feature?(m?.content??i.content):i.content).replace(/\s+/g,' ').slice(0,600)}</p><div class="module-card-meta"><span>${feature?'Alife 运行时功能模块':'实时对话消息'}</span><span>实际位置 #${messageIndex(i)}</span></div></article>`;
}
function frameworkOverride(item){
 let m=planState.modules.find(m=>m.source==='framework'&&m.targetIndex===messageIndex(item));
 if(!m){const group=item.sourceKey==='live-system'?'features':'chat';m={id:moduleId(),name:item.title,source:'framework',group,targetIndex:messageIndex(item),originalContent:item.content,originalRole:item.kind,enabled:true,role:item.kind,content:item.content};planState.modules.push(m);sortPlanGroups();planDirty=true;}
 return m;
}

function roleOptions(role){return ['system','user','assistant'].map(r=>`<option ${r===String(role).toLowerCase()?'selected':''} value="${r}">${r}</option>`).join('');}
function planFields(m,prefix){
  const cut=moduleTruncated(m);
  const contentNote=cut
    ? `<div class="trunc-note">这条正文较长（全文 ${Number(m.contentLength||0).toLocaleString()} 字，只有超过 ${TRUNC_KEEP_CHARS} 字的正文才会只下发预览）。这里先设为只读：编辑预览没有意义，保存时后端会按 Id 用磁盘上的原文回填。<button class="btn tiny warn" data-plan-load="${esc(m.id)}">读取全文</button></div>`
    : '';
  // 大窗口才有「放大」：把内容编辑器顶到整个弹窗，隐藏模块名称 / 输出身份 / 区域 / 区域内顺序。
  // 侧栏那一份本来就窄，放大了也没意义，所以只在 prefix==='modal' 时给按钮。
  const expandBtn=prefix==='modal'
    ? `<button type="button" class="btn tiny expand-editor" id="expandEditor" title="把内容放大到整个窗口，隐藏上面的字段">⤢ 放大</button>`
    : '';
  return `<div class="field"><label>模块名称</label><input id="${prefix}Name" value="${esc(m.name)}"/></div>`
    + `<div class="field"><label>输出身份</label><select id="${prefix}Role">${roleOptions(m.role)}</select></div>`
    + `<div class="field"><label>区域</label><select id="${prefix}Group">${planGroups.map(([g,n])=>`<option value="${g}" ${g===(m.group||'system')?'selected':''}>${n}</option>`).join('')}</select></div>`
    + `<div class="field"><label>区域内顺序</label><input id="${prefix}Order" type="number" min="1" value="${planState.modules.filter(x=>(x.group||'system')===(m.group||'system')).indexOf(m)+1}"/></div>`
    + `<div class="field field-content"><label><span>内容${cut?' <small>只读 · 正文未随报文下发</small>':''}</span>${expandBtn}</label>`
    + `<textarea id="${prefix}Content" class="${prefix==='modal'?'large-editor':'inspector-content'}"${cut?' readonly':''}>${esc(m.content)}</textarea>${contentNote}</div>`
    + (m.source==='worldbook'
        ? `<label><input id="${prefix}Constant" type="checkbox" ${m.constant?'checked':''}/>常驻</label><div class="field"><label>触发关键词（逗号分隔）</label><input id="${prefix}Keys" value="${esc((m.keywords||[]).join(', '))}"/></div>`
        : '');
}
// 编辑大窗口的「放大内容」：只做视图切换，不动任何数据。关掉窗口再打开会回到默认布局。
function bindExpandEditor(){
  const button=$("#expandEditor"); if(!button) return;
  button.onclick=event=>{
    event.preventDefault(); event.stopPropagation();
    const body=button.closest(".modal-body"); if(!body) return;
    const expanded=body.classList.toggle("editor-expanded");
    button.textContent=expanded?"⤡ 还原":"⤢ 放大";
    button.title=expanded?"还原到完整表单":"把内容放大到整个窗口，隐藏上面的字段";
  };
}
function readPlanFields(m,prefix){
  planDirty=true;
  m.name=$('#'+prefix+'Name').value;m.role=$('#'+prefix+'Role').value;
  // 截断的模块正文只是报文里的预览。写回去会把磁盘上的原文覆盖成预览，
  // 所以这里跳过 content —— 后端在 save / apply 时会按 Id 从磁盘计划里回填（RestoreTruncatedModules）。
  if(!moduleTruncated(m)) m.content=$('#'+prefix+'Content').value;
  m.group=$('#'+prefix+'Group').value;
  const order=Math.max(1,Number($('#'+prefix+'Order').value)||1);
  planState.modules=planState.modules.filter(x=>x.id!==m.id);
  const same=planState.modules.filter(x=>(x.group||'system')===m.group);
  const target=same[Math.min(order-1,same.length)];
  if(target)planState.modules.splice(planState.modules.indexOf(target),0,m);else {const last=same[same.length-1];if(last)planState.modules.splice(planState.modules.indexOf(last)+1,0,m);else planState.modules.push(m);}
  sortPlanGroups();
  if(m.source==='worldbook'){m.constant=$('#'+prefix+'Constant').checked;m.keywords=$('#'+prefix+'Keys').value.split(',').map(s=>s.trim()).filter(Boolean);}
}
// 报文瘦身是**每次下发**都做的，所以用户读过全文的模块在下一次 plan-state 里又会变回预览。
// 直接把旧正文贴回来：否则「读完全文 → 编辑 → 保存 → 又变回预览」会让人以为读取白做了。
// 只在**这一轮确实是预览、且上一轮已经读过全文**时才回填 —— 后端改了正文的情况以报文为准。
function keepLoadedModuleContent(next, previous) {
  if (!next?.modules?.length || !previous?.modules?.length) return;
  const byId = new Map(previous.modules.map(m => [m.id, m]));
  next.modules.forEach(m => {
    if (!m.truncated || !loadedPlanModules.has(m.id)) return;
    const old = byId.get(m.id);
    if (!old || typeof old.content !== "string") return;
    m.content = old.content;
    m.contentLength = Number(old.contentLength) || old.content.length;
    m.truncated = false;
  });
}
function commitSidebar(){ const m=planState?.modules.find(m=>m.id===selectedAssemblyModule); if(m && $("#sideContent")) readPlanFields(m,"side"); return true; }

// ── 右栏「模块搜索」 ────────────────────────────────────────────────────
//
// 用户原话：「在没有选择具体模块内容的时候，右边栏显示的是模块编辑 / 单击模块在这里修改……
// 那可以加一个搜索框可以搜索中间的模块信息然后快速定位」。
// 一张 142 条世界书的卡导入后有 143 个模块，肉眼在中间列里滚不现实。
//
// 实现要点：
//   1. 搜索框常驻右栏顶部（空态、已选中态都在），命中结果就地展开，不跳页。
//   2. 输入时**只重绘结果容器**，不动输入框本身 —— 否则每敲一个字都会丢焦点。
//   3. 点结果 → 选中该模块（右栏切到编辑表单）+ 中间列滚动定位 + 闪一下。
function moduleSourceLabel(m){
  return m?.source === "framework" ? "Alife 运行时" : (planSourceLabels[m?.source] || "自定义");
}
function moduleRegionLabel(m){
  return (planGroups.find(g => g[0] === (m?.group || "system")) || [, "系统提示词"])[1];
}
// 正文可能有三万字（那种卡），143 个模块 × 每次按键都重新 toLowerCase 会卡。
// 按模块对象缓存一份「小写化的检索文本」，正文没变就直接复用。
const moduleSearchCache = new WeakMap();
function moduleSearchHaystack(m){
  const cached = moduleSearchCache.get(m);
  if (cached && cached.content === m.content) return cached.text;
  const text = [
    m.name, m.content, (m.keywords || []).join(" "), (m.secondaryKeywords || []).join(" "),
    m.role, moduleRegionLabel(m), moduleSourceLabel(m)
  ].join(" ").toLowerCase();
  moduleSearchCache.set(m, { content: m.content, text });
  return text;
}
function moduleSearchMatches(query){
  const q = String(query || "").trim().toLowerCase();
  if (!q || !planState?.modules) return [];
  const hits = [];
  planState.modules.forEach((m, index) => {
    if (!moduleSearchHaystack(m).includes(q)) return;
    // 名称命中排最前，其次是关键词/来源/区域，正文命中排最后 —— 用户多数时候记得的是模块名。
    const name = String(m.name || "").toLowerCase();
    let score = 0;
    if (name === q) score += 400;
    else if (name.includes(q)) score += 200;
    if ((m.keywords || []).join(" ").toLowerCase().includes(q)) score += 40;
    if (moduleSourceLabel(m).toLowerCase().includes(q) || moduleRegionLabel(m).toLowerCase().includes(q)) score += 20;
    hits.push({ m, index, score });
  });
  return hits.sort((a, b) => b.score - a.score || a.index - b.index);
}
function moduleSnippet(content, query){
  const text = String(content || "").replace(/\s+/g, " ").trim();
  if (!text) return "";
  const at = text.toLowerCase().indexOf(String(query || "").toLowerCase());
  if (at < 0) return esc(text.slice(0, 80));
  const start = Math.max(0, at - 24);
  const end = Math.min(text.length, at + query.length + 56);
  return (start > 0 ? "…" : "")
    + esc(text.slice(start, at)) + "<mark>" + esc(text.slice(at, at + query.length)) + "</mark>"
    + esc(text.slice(at + query.length, end)) + (end < text.length ? "…" : "");
}
function moduleSearchResultsHtml(){
  const query = moduleSearchQuery.trim();
  if (!query) return "";
  if (!planState) return `<p class="search-empty">装配计划尚未就绪。</p>`;
  const hits = moduleSearchMatches(query);
  if (!hits.length) return `<p class="search-empty">没有匹配「${esc(query)}」的模块。</p>`;
  const shown = hits.slice(0, 40);
  const more = hits.length > shown.length ? `，只显示前 ${shown.length} 个` : "";
  return `<div class="search-count">${hits.length} 个匹配${more}</div><div class="module-search-results">`
    + shown.map(({ m }) => `<button type="button" class="search-hit ${m.id === selectedAssemblyModule ? "selected" : ""} ${m.enabled ? "" : "off"}" data-module-jump="${esc(m.id)}">
        <span class="search-hit-name">${esc(m.name || "未命名模块")}</span>
        <span class="search-hit-meta">${esc(moduleSourceLabel(m))} · ${esc(moduleRegionLabel(m))} · ${esc(String(m.role || "system").toLowerCase())}${m.enabled ? "" : " · 未参与装配"}</span>
        <span class="search-hit-snippet">${moduleSnippet(m.content, query)}</span>
      </button>`).join("")
    + `</div>`;
}
function moduleSearchHtml(){
  return `<div class="module-search">
    <input id="moduleSearch" type="search" autocomplete="off" placeholder="搜索模块：名称 / 正文 / 关键词 / 来源" value="${esc(moduleSearchQuery)}"/>
    <div id="moduleSearchResults">${moduleSearchResultsHtml()}</div>
  </div>`;
}
function bindModuleSearch(){
  const input = $("#moduleSearch");
  if (!input) return;
  // 只重绘结果容器：整个 #inspector 重绘会让输入框丢焦点，一个字都打不下去。
  input.oninput = () => {
    moduleSearchQuery = input.value;
    const box = $("#moduleSearchResults");
    if (box) box.innerHTML = moduleSearchResultsHtml();
  };
  input.onkeydown = event => {
    if (event.key !== "Enter") return;
    const hits = moduleSearchMatches(moduleSearchQuery);
    if (hits.length) { event.preventDefault(); jumpToModule(hits[0].m.id); }
  };
}
// 中间列那张卡片的选择器：计划模块用 data-plan-select，运行时模块用 data-framework-id。
function moduleCardElement(m){
  const direct = document.querySelector(`[data-plan-select="${esc(m.id)}"]`);
  if (direct) return direct;
  if (m.source !== "framework") return null;
  const item = snapshot?.items?.find(i => messageIndex(i) === m.targetIndex);
  return item ? document.querySelector(`[data-framework-id="${esc(item.id)}"]`) : null;
}
function jumpToModule(id){
  const m = planState?.modules.find(x => x.id === id);
  if (!m) return;
  commitSidebar();
  selectedAssemblyModule = id;
  moduleSearchQuery = "";
  renderInspector();
  const card = moduleCardElement(m);
  if (!card) return;
  card.scrollIntoView({ block: "center", behavior: "smooth" });
  card.classList.add("jump-flash");
  setTimeout(() => card.classList.remove("jump-flash"), 1600);
}

// 右栏在装配页的渲染：先让 renderPlanInspectorBody 铺好内容，再把搜索框插到最上面。
// 之所以不在每个分支里各写一遍，是因为这个函数有 4 个出口（运行时消息 / 空态 / 官方角色设定 /
// 普通模块），逐个插入迟早会漏一个 —— 搜索框必须**任何状态下都在**。
function renderPlanInspector(){
  renderPlanInspectorBody();
  const box = $('#inspector');
  if (!box) return;
  box.insertAdjacentHTML('afterbegin', moduleSearchHtml());
  bindModuleSearch();
}
function renderPlanInspectorBody(){
  if(selectedAssemblyModule.startsWith('live:')) {
    const item=snapshot.items.find(i=>i.id===selectedAssemblyModule.slice(5));
    if(!item)return;
    const override=item.sourceKey==='live-system'?planState?.modules.find(m=>m.source==='framework' && m.targetIndex===messageIndex(item)):null;
    const isFrameworkModule=item.sourceKey==='live-system'&&messageIndex(item)>0;
    const editorValue=override?.content??item.content;
    $('#inspector').innerHTML=`<h3>${esc(item.title)}</h3><p class="inspector-tip">${isFrameworkModule ? '这是 Alife 当前生成的功能模块。这里保存的是插件覆盖草稿，只有应用“插件覆盖”后才会改变本次请求中的消息。' : '这是 Alife 当前运行时消息。保存后会直接写回对应的本地内容。'}</p><div class="field"><label>当前运行时消息 <small>只读</small></label><textarea class="inspector-content" readonly>${esc(item.content || '当前消息为空')}</textarea></div><div class="field"><label>${isFrameworkModule ? '插件覆盖草稿' : '编辑内容'} <small>${isFrameworkModule ? '保留原内容可取消覆盖' : '保存后立即写回'}</small></label><textarea class="inspector-content" id="liveContent">${esc(editorValue)}</textarea></div><div class="field"><label>输出身份</label><select id="liveRole">${roleOptions(override?.role||item.kind)}</select></div><div class="toolbar"><button class="btn" id="largeLive">编辑</button><button class="btn primary" id="saveLive">保存草稿</button></div>`;
    const save=()=>{
      // 这条正文可能只是预览（后端为控制报文体积截断过），直接写回会毁掉原文。
      if (guardTruncatedItem(item)) return;
      if(item.sourceKey==='live-system' && messageIndex(item)>0){
        let m=planState.modules.find(m=>m.source==='framework'&&m.targetIndex===messageIndex(item));
        if(!m){m={id:moduleId(),name:item.title,source:'framework',group:'features',targetIndex:messageIndex(item),originalContent:item.content,originalRole:item.kind,enabled:true};planState.modules.push(m);}
        m.role=$('#liveRole').value;m.content=$('#liveContent').value;
        submitPlan('plan:save','正在保存模块草稿…',false);
      }else send('item:update',{id:item.id,owner:item.owner,path:item.path,sourceKey:item.sourceKey,role:$('#liveRole').value,content:$('#liveContent').value});
    };
    $('#saveLive').onclick=save;
    $('#largeLive').onclick=()=>{openModal(item.title,`<div class="field"><label>输出身份</label><select id="modalLiveRole">${roleOptions($('#liveRole').value)}</select></div><div class="field field-content"><label><span>内容</span><button type="button" class="btn tiny expand-editor" id="expandEditor" title="把内容放大到整个窗口，隐藏上面的字段">⤢ 放大</button></label><textarea class="large-editor" id="modalLiveContent">${esc($('#liveContent').value)}</textarea></div><div class="toolbar modal-actions"><button class="btn primary" id="saveLargeLive">保存</button></div>`);bindExpandEditor();$('#saveLargeLive').onclick=()=>{$('#liveRole').value=$('#modalLiveRole').value;$('#liveContent').value=$('#modalLiveContent').value;save();closeModal();};};
    return;
  }
  const m=planState?.modules.find(m=>m.id===selectedAssemblyModule);
  if(!m){$('#inspector').innerHTML='<h3>模块编辑</h3><p class="inspector-tip">单击中间列的模块在这里修改；模块太多时用上面的搜索框直接找。</p>';return;}
  if (m.source === 'native') {
    requestNativePrompt(selectedOwner);
    const effective = nativeModuleDisplay(selectedOwner, m);
    const live = runtimeSystemContent(selectedOwner);
    // 官方角色设定模块同样可能因为报文体积只带了预览，处理方式与普通模块一致。
    const cut = moduleTruncated(m);
    const modeNote = planState?.mode === 'Temporary'
      ? '「插件覆盖」下这段内容会在每次请求前替换掉 index[0]：框架注入的「名称 / 生日 / 简介 / 设定 / 私人文件夹」都在这里一起改。'
      : '当前不是「插件覆盖」，实际发送的仍是运行时的 index[0]；把覆盖方式切到「插件覆盖」后，这里的内容才会生效。';
    const cutNote = cut
      ? `<div class="trunc-note">这段系统消息较长（全文 ${Number(m.contentLength||0).toLocaleString()} 字，只有超过 ${TRUNC_KEEP_CHARS} 字的正文才会只下发预览），已设为只读：保存时后端会按 Id 用磁盘上的原文回填，改预览没有意义。<button class="btn tiny warn" data-plan-load="${esc(m.id)}">读取全文</button></div>`
      : '';
    $('#inspector').innerHTML=`<h3>${esc(m.name)}</h3><p class="inspector-tip">${modeNote}</p>
      <div class="field"><label>将发送的系统消息 <small>只读</small></label><textarea class="inspector-content" readonly>${esc(effective || '（空：本次请求不会发送系统消息）')}</textarea></div>
      <div class="field"><label>系统消息内容 <small>${cut ? '只读 · 正文未随报文下发' : '可编辑：名称 / 生日 / 简介 / 设定 / 私人文件夹'}</small></label><textarea class="inspector-content" id="nativeCoreContent"${cut ? ' readonly' : ''}>${esc(m.content || '')}</textarea>${cutNote}</div>
      <details class="official-preview"><summary>当前运行时 index[0]（本地，未被插件覆盖）</summary><pre class="preview-code">${esc(live || '（未收到运行时系统消息）')}</pre></details>
      <div class="toolbar"><button class="btn" id="largePlan">编辑</button><button class="btn" id="nativeFillOfficial" title="用当前角色字段重新生成官方系统消息模板">填入官方模板</button><button class="btn primary" id="saveNative">保存草稿</button></div>`;
    // 截断时不要读 textarea（那是预览），后端会按 Id 回填磁盘原文。
    const readNativeCore=()=>{ if(!moduleTruncated(m)) m.content=$('#nativeCoreContent').value; };
    $('#saveNative').onclick=()=>{readNativeCore();submitPlan('plan:save','正在保存系统消息草稿…',false);render();};
    $('#largePlan').onclick=()=>{readNativeCore();openPlanModal();};
    $('#nativeFillOfficial').onclick=()=>{
      const template = nativeSystemContent(selectedOwner);
      if (!template) return alertError('暂时读不到角色字段（名称/生日/简介/设定），请稍后重试或先激活角色。');
      $('#nativeCoreContent').value = template;
      // 「填入官方模板」是**本地生成**的完整内容，不是预览：标记成已加载，
      // 免得后端 RestoreTruncatedModules 又把磁盘上的旧正文回填回来、把模板覆盖掉。
      loadedPlanModules.add(m.id);
      m.truncated = false;
      m.content = template; planDirty = true;
      submitPlan('plan:save','已填入官方系统消息模板…',false);
      render();
    };
    return;
  }
  $('#inspector').innerHTML=planFields(m,'side')+'<div class="toolbar"><button class="btn" id="largePlan">编辑</button><button class="btn primary" id="saveSide">保存草稿</button><button class="btn" id="deletePlanModule">删除模块</button></div>';
  $('#saveSide').onclick=()=>{readPlanFields(m,'side');submitPlan('plan:save','正在保存模块草稿…',false);render();};
  $('#largePlan').onclick=()=>{readPlanFields(m,'side');openPlanModal();};
  $('#deletePlanModule').onclick=()=>{planState.modules=planState.modules.filter(x=>x!==m);selectedAssemblyModule='';submitPlan('plan:save','正在删除模块…',false);render();};
}
function openPlanModal(){
  const m=planState.modules.find(m=>m.id===selectedAssemblyModule);if(!m)return;
  const owner=selectedOwner;
  openModal(`编辑：${m.name}`,planFields(m,'modal')+`<div class="toolbar modal-actions"><button class="btn primary" id="saveModalPlan">保存草稿</button></div>`);
  bindExpandEditor();
  $('#saveModalPlan').onclick=()=>{if(owner!==selectedOwner)return closeModal();readPlanFields(m,'modal');submitPlan('plan:save','正在保存模块草稿…',false);closeModal();render();};
}

function renderExperimentAssembly(o) {
  const groups = assemblyGroups(o.name);
  const totalTokens = groups.filter(([group]) => assemblyToggles[group]).flatMap(([, , list]) => list).reduce((sum, item) => sum + (item.estimatedTokens || Math.ceil(String(item.content || item.Content || "").length / 1.7)), 0);
  const activeModules = groups.filter(([group]) => assemblyToggles[group]).reduce((sum, [, , list]) => sum + list.length, 0);
  $("#pageBody").innerHTML += `<div class="assembly-studio">
    <section class="assembly-hero">
      <div><div class="eyebrow">ASSEMBLY RECIPE</div><h3>把片段编排成一次清晰的模型对话</h3><p>顺序就是语义。点选任意模块，在右侧局部编辑；你始终看得见它会在什么位置进入模型。</p></div>
      <div class="assembly-hero-actions"><button class="btn" id="focusAssembly">专注装配</button><button class="btn primary" id="openPresetDrawer">预设库 <span>→</span></button></div>
    </section>
    <section class="assembly-summary"><div><b>${activeModules}</b><span>启用模块</span></div><div><b>≈ ${fmtTokens(totalTokens)}</b><span>预计 tokens</span></div><div><b>${selectedPreset ? esc(selectedPreset) : "未选择"}</b><span>当前预设</span></div><div class="assembly-summary-note">世界书会按“常驻 + 当前对话关键词”自动筛选</div></section>
    ${assemblyTokenStrip(groups, totalTokens)}
    <section class="assembly-flow" id="assemblyFlow">
      ${groups.map(g => assemblyLane(g)).join("")}
    </section>
    <section class="assembly-preview-section"><div class="section-title"><div><div class="eyebrow">COMPILED CONTEXT</div><h3>实验装配预览</h3></div><div class="toolbar"><button class="btn primary" id="buildPlan">生成预览</button><button class="btn" id="openPlanLarge">大窗口查看</button></div></div><pre id="assemblyPreview" class="preview-code">点击“生成预览”，检查本次会如何把各个模块拼接给模型。</pre></section>
  </div>`;
  $$(".switch").forEach(s => s.onclick = event => { event.stopPropagation(); assemblyToggles[s.dataset.group] = !assemblyToggles[s.dataset.group]; render(); });
  $$("[data-assembly-key]").forEach(card => card.onclick = () => {
    selectedAssemblyModule = decodeURIComponent(card.dataset.assemblyKey);
    const module = findAssemblyModule(selectedAssemblyModule);
    if (module) openAssemblyModalEditor(module.group, module.item, module.index);
    renderInspector();
  });
  $$("[data-add-group]").forEach(btn => btn.onclick = () => addAssemblyModule(btn.dataset.addGroup));
  $("#buildPlan").onclick = buildAssemblyPreview;
  $("#openPlanLarge").onclick = () => openModal("实验装配预览", `<pre class="preview-code long">${esc($("#assemblyPreview").textContent)}</pre>`);
  $("#openPresetDrawer").onclick = openPresetDrawer;
  $("#focusAssembly").onclick = () => { document.body.classList.toggle("assembly-focus"); $("#focusAssembly").textContent = document.body.classList.contains("assembly-focus") ? "退出专注" : "专注装配"; };
}

function assemblyTokenStrip(groups, totalTokens) {
  const palette = { character:"#278778", presetSystem:"#7057e8", presetUser:"#527fc2", presetAssistant:"#a56ab8", world:"#d97633" };
  const rows = groups.map(([group, title, list]) => ({ group, title, tokens:assemblyToggles[group] ? list.reduce((sum, item) => sum + (item.estimatedTokens || Math.ceil(String(item.content || item.Content || "").length / 1.7)), 0) : 0 })).filter(row => row.tokens > 0);
  if (!rows.length) return `<section class="token-ledger empty-ledger">尚未有可计入的上下文模块。</section>`;
  return `<section class="token-ledger"><div class="token-ledger-head"><div><div class="eyebrow">TOKEN LEDGER</div><h3>本次装配的 token 去向</h3></div><span>总计 ≈ ${fmtTokens(totalTokens)} tokens</span></div><div class="token-track">${rows.map(row => `<i class="token-segment ${row.group}" style="width:${Math.max(2, row.tokens / Math.max(1, totalTokens) * 100)}%;background:${palette[row.group]}" title="${esc(row.title)}：约 ${fmtTokens(row.tokens)} tokens"></i>`).join("")}</div><div class="token-legend">${rows.map(row => `<span><i style="background:${palette[row.group]}"></i>${esc(row.title)} <b>≈${fmtTokens(row.tokens)}</b></span>`).join("")}</div></section>`;
}

function assemblyGroups(owner) {
  const entries = presetAssemblyEntries();
  const roleOf = entry => String(entry.role || entry.Role || "system").toLowerCase();
  return [
    ["character", "角色卡独立系统提示词", itemsBy(i => i.owner === owner && i.sourceKey === "character-prompt")],
    ["presetSystem", "预设模块 · System", entries.filter(entry => ["system", "developer"].includes(roleOf(entry)))],
    ["presetUser", "预设模块 · User", entries.filter(entry => roleOf(entry) === "user")],
    ["presetAssistant", "预设模块 · AI", entries.filter(entry => ["assistant", "ai", "model"].includes(roleOf(entry)))],
    ["world", "世界书模块", activeWorldEntries(owner)]
  ];
}
function assemblyLane([group, title, list]) {
  const tokens = list.reduce((sum, item) => sum + (item.estimatedTokens || Math.ceil(String(item.content || item.Content || "").length / 1.7)), 0);
  const enabled = assemblyToggles[group];
  const subtitle = ({ character:"来自角色卡，作为独立 System 提示词", presetSystem:"酒馆预设中的 System 片段，可独立开关", presetUser:"酒馆预设中的 User 片段，可独立开关", presetAssistant:"酒馆预设中的 AI / Assistant 片段，可独立开关", world:"常驻或按关键词命中后注入" })[group] || "";
  const order = ["character", "presetSystem", "presetUser", "presetAssistant", "world"];
  return `<section class="assembly-lane ${enabled ? "" : "is-paused"}"><div class="lane-rail"><span class="lane-index">${String(order.indexOf(group) + 1).padStart(2, "0")}</span></div><div class="lane-body">
    <div class="lane-head"><div><span class="module-type ${group}">${assemblyModuleLabel(group)}</span><h4>${title}</h4><p>${subtitle}</p></div><div class="lane-controls"><span>${list.length} 个 · ≈ ${fmtTokens(tokens)} tokens</span><button class="switch ${enabled ? "on" : ""}" data-group="${group}" aria-label="切换 ${title}"></button></div></div>
    <div class="module-stack">${list.map((item, index) => assemblyModuleCard(group, item, index)).join("") || `<button class="empty-slot" data-add-group="${group}">+ 在这里添加 ${assemblyModuleLabel(group)} 模块</button>`}</div>
  </div></section>`;
}
function assemblyModuleCard(group, item, index) {
  const key = assemblyModuleKey(group, item, index);
  const active = key === selectedAssemblyModule;
  const content = item.content || item.Content || "";
  const title = item.title || item.Title || `${assemblyModuleLabel(group)} ${index + 1}`;
  const tags = group === "world" ? (item.constant ? "常驻" : `关键词：${(item.keywords || []).slice(0, 3).join("、") || "未设置"}`) : group.startsWith("preset") ? `${item.role || "system"} · 顺序 ${Number(item.Order || item.order || index + 1)}` : item.source || "上下文";
  const tokens = item.estimatedTokens || Math.ceil(content.length / 1.7);
  return `<button class="assembly-module ${active ? "selected" : ""}" data-assembly-key="${encodeURIComponent(key)}"><div class="module-card-top"><span class="module-grip">⋮⋮</span><b>${esc(title)}</b><span class="module-arrow">编辑 →</span></div><p>${esc(content || "（该模块没有可展示的文本内容）")}</p><div class="module-card-meta"><span>${esc(tags)}</span><span>≈ ${fmtTokens(tokens)} tokens</span></div></button>`;
}
function openAssemblyModalEditor(group, item, index) {
  const editable = assemblyModuleEditable({ group, item, index });
  const type = assemblyModuleLabel(group);
  const content = item.content || item.Content || "";
  const name = item.title || item.Title || `${type} ${index + 1}`;
  const trigger = group === "world" ? (item.constant ? "常驻注入" : "关键词触发") : group.startsWith("preset") ? "预设顺序注入" : group === "character" ? "固定顶部" : "由运行时决定";
  openModal(name, `<div class="framework-editor-note">${editable ? "修改将直接保存到对应模块。" : "该模块为只读快照。"}</div>
    <div class="field compact"><label>模块名称</label><input id="assemblyModalName" value="${esc(name)}" ${group === "character" ? "readonly" : ""}/></div>
    <div class="field"><label>提示词内容</label><textarea class="large-editor" ${editable ? "" : "readonly"}>${esc(content)}</textarea></div>
    <div class="toolbar modal-actions">${editable ? `<button class="btn primary" id="saveAssemblyModal">保存修改</button>` : ""}<button class="btn" id="copyAssemblyModal">复制全文</button></div>`);
  $("#copyAssemblyModal").onclick = () => navigator.clipboard.writeText($(".large-editor").value);
  $("#saveAssemblyModal")?.addEventListener("click", () => {
    const newName = $("#assemblyModalName").value.trim();
    const newContent = $(".large-editor").value;
    saveAssemblyModuleDirect(group, item, newName, newContent);
    closeModal();
  });
}
function saveAssemblyModuleDirect(group, item, title, content) {
  if (group === "world") {
    item.title = title || item.title;
    item.content = content;
    send("worldbook:save", { owner: selectedOwner, entries: (worldBooks[selectedOwner]?.entries || []) });
    setStatus("正在保存世界书模块…");
  } else if (group.startsWith("preset")) {
    const raw = item.raw;
    if (raw) {
      if (Object.prototype.hasOwnProperty.call(raw, "Name")) raw.Name = title || raw.Name;
      else raw.name = title || raw.name;
      if (Object.prototype.hasOwnProperty.call(raw, "Content")) raw.Content = content;
      else raw.content = content;
      send("presets:save", { name: selectedPreset, preset: presetData[selectedPreset] });
      setStatus("正在保存预设片段…");
    }
  } else if (item.id) {
    if (guardTruncatedItem(item)) return;
    const payload = { id: item.id, content };
    // archive 条目需要额外传递 path 和 sourceKey 以便后端在重建快照找不到时仍能操作
    if (item.path) payload.path = item.path;
    if (item.sourceKey) payload.sourceKey = item.sourceKey;
    if (item.owner) payload.owner = item.owner;
    send("item:update", payload);
    setStatus("正在保存上下文模块…");
  }
  render();
}
function addAssemblyModule(group) {
  if (group === "world") {
    const book = worldBooks[selectedOwner] || { entries: [] };
    book.entries.push({ id: moduleId(), title: "新世界书条目", keywords: [], content: "", enabled: true, constant: false, insertionOrder: 100 });
    worldBooks[selectedOwner] = book;
    send("worldbook:save", { owner: selectedOwner, entries: book.entries });
    setStatus("已新增世界书条目，正在保存…");
    render();
    return;
  }
  if (group.startsWith("preset")) {
    if (!selectedPreset || !presetData[selectedPreset]) { alertError("请先选择一个预设"); return; }
    const data = presetData[selectedPreset];
    const entries = data.Entries || data.entries || [];
    const role = group === "presetSystem" ? "system" : group === "presetUser" ? "user" : "assistant";
    entries.push({ Name: "新预设片段", Content: "", Role: role, Order: entries.length + 1, Enabled: true });
    data.Entries = entries;
    send("presets:save", { name: selectedPreset, preset: data });
    setStatus("已新增预设片段，正在保存…");
    render();
    return;
  }
  if (group === "character") {
    send("item:create", { owner: selectedOwner, role: "system", content: "新模块功能说明" });
    setStatus("已新增上下文模块…");
  }
}

function buildAssemblyPreview() {
  const parts = [];
  assemblyGroups(selectedOwner).forEach(([group, title, list]) => {
    if (!assemblyToggles[group] || !list.length) return;
    parts.push(`# ${title}\n` + list.map(item => item.content || item.Content || "").filter(Boolean).join("\n\n"));
  });
  $("#assemblyPreview").textContent = parts.join("\n\n━━━━━━━━━━\n\n") || "当前没有启用且包含文本的模块。";
}

function worldEntryTriggered(entry, owner) {
  if (!entry.enabled) return false;
  if (entry.constant) return true;
  const haystack = itemsBy(i => i.owner === owner && ["实时上下文","历史对话"].includes(i.category)).map(i=>i.content).join("\n").toLowerCase();
  return (entry.keywords || []).some(k => k && haystack.includes(String(k).toLowerCase()));
}
function activeWorldEntries(owner) {
  return (worldBooks[owner]?.entries || []).filter(e => worldEntryTriggered(e, owner)).sort((a,b)=>(a.insertionOrder||100)-(b.insertionOrder||100));
}

function presetAssemblyEntries() {
  if (!selectedPreset || !presetData[selectedPreset]) return [];
  const entries = presetData[selectedPreset].Entries || presetData[selectedPreset].entries || [];
  return entries.map((entry, sourceIndex) => ({ raw:entry, sourceIndex, id:`preset-${sourceIndex}`, title:entry.Name || entry.name || "预设片段", content:entry.Content || entry.content || "", source:"预设库 / " + selectedPreset, role:entry.Role || entry.role || "system", Order:entry.Order || entry.order || 0, enabled:entry.Enabled !== false && entry.enabled !== false, estimatedTokens:Math.ceil(String(entry.Content || entry.content || "").length / 1.7) }))
    .filter(entry => entry.enabled).sort((a,b) => a.Order - b.Order);
}

// ---------------------------------------------------------------------------
// 角色预设：每个角色在插件里保留的一份上下文记录。
//   Storage/ContextManager/CharacterPresets/<角色>.json          ← 自动快照（应用/保存方案时自动更新）
//   Storage/ContextManager/CharacterPresets/<角色>__<名称>.json   ← 用户手动保存的命名快照
// ---------------------------------------------------------------------------
const planModeLabels = { Temporary:"插件覆盖", Permanent:"本地覆盖", Off:"关闭" };
function planModeLabel(mode) { return planModeLabels[mode] || mode || "关闭"; }
function charPresetSlots(owner) { return characterPresets[owner] || []; }
// 同一个角色只自动请求一次列表，避免 render → 请求 → 回复 → render 的死循环。
// 切换角色时 render() 会把 charPresetOwner 清掉，于是新角色会重新拉一次。
let charPresetOwner = "";
function requestCharacterPresets(owner) {
  if (!owner || charPresetOwner === owner) return;
  charPresetOwner = owner;
  send("charpreset:list", { owner });
}
// 快照里到底存了什么 —— 写清楚，否则用户不知道导出的文件是什么、能不能搬去别的角色。
function characterPresetDoc() {
  return `<details class="preset-doc"><summary>一份角色预设（快照）里到底存了什么？</summary>
    <div class="preset-doc-body">
      <p>一个 JSON 文件 = <b>一个角色的一整份上下文记录</b>。字段：</p>
      <ul>
        <li><code>owner</code>：这份记录原本属于哪个角色。导入到别的角色时会被改写成当前角色。</li>
        <li><code>name</code> / <code>auto</code>：快照名称；<code>auto=true</code> 表示「自动快照」槽（应用/保存方案时自动更新）。</li>
        <li><code>updatedAt</code>：最后一次写入时间。</li>
        <li><code>plan.mode</code>：覆盖方式 —— <b>Off</b> 关闭 / <b>Temporary</b> 插件覆盖（插件启动或角色激活时自动侧装）/ <b>Permanent</b> 本地覆盖（改写 index.json 的 Prompt）。</li>
        <li><code>plan.applyMacros</code>：是否把 {{char}}、{{user}}、{{description}} 等酒馆宏替换成当前角色数据。</li>
        <li><code>plan.userName</code> / <code>plan.customMacros</code>：{{user}} 的取值，以及自定义宏表。</li>
        <li><code>plan.modules[]</code>：<b>全部模块</b>，每个含 <code>name / role / content（完整正文）/ enabled / group / source / constant / keywords</code>；框架注入的模块还带 <code>targetIndex / originalContent</code>，用来还原原始消息。</li>
        <li><code>sources.tavernCard</code>：<b>酒馆角色卡原文</b>（描述 / 性格 / 场景 / 开场白 / 作者注释等）。{{description}}、{{char}} 这些宏就是从这里取值的。</li>
        <li><code>sources.worldBook</code>：<b>该角色的世界书原文</b>（条目、关键词、常驻标记、注入顺序）。</li>
        <li><code>sources.preset</code> + <code>sources.activePreset</code>：当时选中的<b>酒馆预设原文</b>及其名称。</li>
      </ul>
      <p><b>为什么要把设定原文也存进来：</b>模块正文里大多是 <code>{{description}}</code>、<code>{{worldbook}}</code> 这类宏，取值来自角色目录里的 TavernCard.json / WorldBook.json 和 Presets 目录。只存「配方」不存「原料」，换角色或换机器后宏会展开成空 —— 所以现在两者一起打包，一份文件就是完整的一套设定。</p>
      <p>仍然不包含：聊天记录、记忆归档、头像图片本体。导入到别的角色时会<b>只补缺失的文件</b>，不会覆盖该角色已有的世界书 / 角色卡。</p>
    </div></details>`;
}
function charPresetCard(slot) {
  const extras = [];
  if (Number(slot.worldbookEntries) > 0) extras.push(`世界书 ${Number(slot.worldbookEntries)} 条`);
  if (slot.hasTavernCard) extras.push("酒馆卡");
  if (slot.activePreset) extras.push(`预设 ${slot.activePreset}`);
  return `<article class="preset-card"><div class="preset-card-main"><div><span class="module-type preset">${slot.auto ? "自动快照" : "命名快照"}</span><h3>${esc(slot.name)}</h3><p>${Number(slot.modules) || 0} 个模块 · ${esc(planModeLabel(slot.mode))} · ${esc(slot.updatedAt || "未记录时间")}</p><p class="preset-card-sources">${extras.length ? `含设定来源：${esc(extras.join(" / "))}` : "只含装配配方（未抓到设定来源）"}</p></div></div><footer><span>${slot.auto ? "应用方案时自动更新" : "手动保存"}</span><div><button class="link-btn" data-charpreset-load="${esc(slot.name)}">读取到编辑器</button><button class="link-btn" data-charpreset-apply="${esc(slot.name)}">立即生效</button><button class="link-btn" data-charpreset-export="${esc(slot.name)}">导出</button>${slot.auto ? "" : `<button class="link-btn danger-link" data-charpreset-delete="${esc(slot.name)}">删除</button>`}</div></footer></article>`;
}
function importCharacterPreset() {
  if (!selectedOwner) return alertError("请先选择角色");
  setStatus("正在选择要导入的角色记录…");
  send("charpreset:import", { owner:selectedOwner });
}
function bindCharPresetActions(root) {
  $$("[data-charpreset-load]", root).forEach(b => b.onclick = () => {
    if (planDirty && !confirm("读取该角色预设会覆盖当前装配方案里未保存的修改。继续吗？")) return;
    planDirty = false;
    setStatus(`正在读取角色预设 ${b.dataset.charpresetLoad}…`);
    send("charpreset:load", { owner:selectedOwner, name:b.dataset.charpresetLoad });
  });
  $$("[data-charpreset-apply]", root).forEach(b => b.onclick = () => {
    if (!confirm(`让角色预设「${b.dataset.charpresetApply}」立即生效？\n\n会用它替换当前装配方案，并按记录里的覆盖方式重新应用。`)) return;
    planDirty = false;
    setStatus(`正在应用角色预设 ${b.dataset.charpresetApply}…`);
    send("charpreset:apply", { owner:selectedOwner, name:b.dataset.charpresetApply });
  });
  $$("[data-charpreset-export]", root).forEach(b => b.onclick = () => {
    setStatus(`正在导出角色预设 ${b.dataset.charpresetExport}…`);
    send("charpreset:export", { owner:selectedOwner, name:b.dataset.charpresetExport });
  });
  $$("[data-charpreset-delete]", root).forEach(b => b.onclick = () => { if (confirm(`删除角色预设「${b.dataset.charpresetDelete}」？`)) send("charpreset:delete", { owner:selectedOwner, name:b.dataset.charpresetDelete }); });
}
function renderCharPresetModalList() {
  const box = $("#charPresetModalList");
  if (!box) return;
  box.innerHTML = charPresetSlots(selectedOwner).map(charPresetCard).join("") || emptyMini("暂无记录。先在装配页配置好方案，再点上面的保存。");
  bindCharPresetActions(box);
}
let charPresetPanelOpen = false;
function openCharacterPresetPanel() {
  if (!selectedOwner) return alertError("请先选择角色");
  charPresetPanelOpen = true;
  requestCharacterPresets(selectedOwner);
  openModal(`角色预设 · ${selectedOwner}`, `
    <p class="plain-text">这里保存的是该角色的整份上下文记录（覆盖方式 + 全部模块）。插件在应用/保存方案时会自动更新「自动快照」，你也可以另存多份命名快照。选择「插件覆盖」的记录会在插件启动时自动侧装，直接对话即可生效。</p>
    ${characterPresetDoc()}
    <div class="toolbar"><button class="btn primary" id="charPresetSaveAs">把当前方案存为角色预设</button><button class="btn" id="charPresetExportCurrent">导出当前方案</button><button class="btn" id="charPresetImport">导入角色记录</button><button class="btn" id="charPresetRefresh">刷新</button></div>
    <section class="preset-catalog modal-preset-list" id="charPresetModalList">${charPresetSlots(selectedOwner).map(charPresetCard).join("") || emptyMini("暂无记录。先在装配页配置好方案，再点上面的保存。")}</section>`);
  $("#charPresetRefresh").onclick = () => { send("charpreset:list", { owner:selectedOwner }); setStatus("正在刷新角色预设…"); };
  $("#charPresetSaveAs").onclick = () => saveCharacterPresetAs();
  $("#charPresetExportCurrent").onclick = () => exportCurrentPlanAsPreset();
  $("#charPresetImport").onclick = () => importCharacterPreset();
  bindCharPresetActions($("#modalRoot"));
}
// 导出「编辑器里这一份」：不要求先存成快照，后端直接用 payload 里的 plan 落盘。
function exportCurrentPlanAsPreset() {
  if (!planState) return alertError("请先打开「上下文装配」加载该角色的方案");
  commitSidebar(); cachePlan(selectedOwner, planState);
  setStatus("正在导出当前方案…");
  send("charpreset:export", { owner:selectedOwner, name:"", plan:planState });
}
function saveCharacterPresetAs() {
  if (!planState) return alertError("请先打开「上下文装配」加载该角色的方案");
  if (!ownerInfo(selectedOwner).active) return alertError("请先激活角色再保存角色预设");
  commitSidebar(); cachePlan(selectedOwner, planState);
  openModal("保存为角色预设", `<div class="field"><label>预设名称</label><input id="charPresetName" value="${esc(selectedOwner)}" placeholder="留空 = 写入自动快照槽" /></div>
    <p class="plain-text" id="charPresetHint"></p>
    <div class="toolbar modal-actions"><button class="btn" id="charPresetCancel">取消</button><button class="btn primary" id="charPresetConfirm">保存</button></div>`);
  const nameInput = $("#charPresetName");
  // 后端规则（见 CharacterPresetPath）：名称为空、或名称正好等于角色名 → 写入「自动快照」槽，
  // 每次应用/保存方案都会自动覆盖它；只有**别的**名字才新建命名快照。
  // 以前只在说明文字里写一句，用户填完还以为是命名快照，保存完发现列表里没多出东西
  // —— 现在把「这个名字会存到哪里」实时显示在输入框下面。
  const refreshHint = () => {
    const name = nameInput.value.trim();
    const auto = !name || name.toLowerCase() === selectedOwner.toLowerCase();
    $("#charPresetHint").innerHTML = auto
      ? `将写入 <b>自动快照槽</b>（${esc(selectedOwner)}）：插件在应用/保存方案时会自动更新它，<b>不会</b>新建命名快照 —— 想另存一份请换个名字。`
      : `将新建命名快照「<b>${esc(name)}</b>」：可随时读取、应用、删除，也能导出成文件搬去别的角色。`;
  };
  refreshHint();
  nameInput.oninput = refreshHint;
  // 打开就聚焦 + 全选预填的角色名：想改名直接打字即可，不用先手动拖选。
  nameInput.focus();
  nameInput.select();
  $("#charPresetCancel").onclick = closeModal;
  $("#charPresetConfirm").onclick = () => {
    const name = nameInput.value.trim();
    closeModal();
    setStatus(`正在保存角色预设 ${name || selectedOwner}…`);
    send("charpreset:save", { owner:selectedOwner, name, plan:planState });
  };
}
function renderCharacterPresets() {
  head("角色预设", "每个角色在这里保留一份上下文记录：插件会在应用/保存装配方案时自动更新同名快照，你也可以另存多份命名快照，随时读取、一键生效，或导出成文件搬去别的角色。",
    `<button class="btn primary" id="saveCharPresetPage">保存当前方案</button><button class="btn" id="exportCharPresetPage">导出当前方案</button><button class="btn" id="importCharPresetPage">导入角色记录</button><button class="btn" id="refreshCharPresetPage">刷新</button>`);
  requestCharacterPresets(selectedOwner);
  const slots = charPresetSlots(selectedOwner);
  $("#pageBody").innerHTML = `<div class="preset-library-intro"><div><div class="eyebrow">CHARACTER CONTEXT RECORDS</div><h3>${esc(selectedOwner || "未选择角色")}</h3><p>存放位置：Storage/ContextManager/CharacterPresets/&lt;角色&gt;.json（自动快照）与 &lt;角色&gt;__&lt;名称&gt;.json（命名快照）。「插件覆盖」的记录会在插件启动/角色激活时自动侧装。</p></div></div>
    ${characterPresetDoc()}
    <section class="preset-catalog">${slots.map(charPresetCard).join("") || emptyMini("还没有角色预设。先在「上下文装配」里配置好方案，再点右上角保存。")}</section>`;
  $("#saveCharPresetPage").onclick = () => saveCharacterPresetAs();
  $("#exportCharPresetPage").onclick = () => exportCurrentPlanAsPreset();
  $("#importCharPresetPage").onclick = () => importCharacterPreset();
  $("#refreshCharPresetPage").onclick = () => { send("charpreset:list", { owner:selectedOwner }); setStatus("正在刷新角色预设…"); };
  bindCharPresetActions($("#pageBody"));
}

function renderPresets() {
  head("酒馆预设库", "酒馆预设不是一段难以理解的 JSON，而是一套可反复调用的上下文配方。选择一个酒馆预设后，仍可在装配台编辑具体模块。",
    `<button class="btn primary" id="importPresetBtn">导入酒馆预设</button><button class="btn" id="refreshPresetBtn">刷新列表</button>`);
  $("#pageBody").innerHTML = `<div class="preset-library-intro"><div><div class="eyebrow">REUSABLE RECIPES</div><h3>把成熟社区酒馆预设变成可看见、可微调的装配方案</h3><p>导入 SillyTavern 预设后不必面对原始结构：在装配台中按顺序查看、开关和修改每个提示词片段。</p></div><button class="btn" id="openPresetDrawerFromLibrary">以抽屉方式浏览</button></div>
    <section class="preset-catalog">${presetNames.map(presetCard).join("") || emptyMini("暂无酒馆预设。可从酒馆导入一个预设，或先创建角色并开始搭建。")}</section>`;
  $("#importPresetBtn").onclick = () => send("tavern:import-preset");
  $("#refreshPresetBtn").onclick = () => send("presets:list");
  $("#openPresetDrawerFromLibrary").onclick = openPresetDrawer;
  $$("[data-preset]").forEach(button => button.onclick = () => send("presets:get", { name:button.dataset.preset }));
  $$("[data-preset-edit]").forEach(button => button.onclick = event => { event.stopPropagation(); selectedPreset = button.dataset.presetEdit; editCurrentPreset(); });
  $$("[data-preset-delete]").forEach(button => button.onclick = event => { event.stopPropagation(); send("presets:delete", { name:button.dataset.presetDelete }); });
}
function presetCard(name) {
  const entries = presetData[name]?.Entries || presetData[name]?.entries || [];
  const selected = name === selectedPreset;
  return `<article class="preset-card ${selected ? "selected" : ""}"><button class="preset-card-main" data-preset="${esc(name)}"><div><span class="module-type preset">酒馆预设</span><h3>${esc(name)}</h3><p>${entries.length ? `包含 ${entries.length} 个提示词片段，可在装配台逐个查看。` : "选择后读取预设结构与可用片段。"}</p></div><span class="preset-card-arrow">→</span></button><footer><span>${selected ? "当前已选中" : "点击设为当前酒馆预设"}</span><div><button class="link-btn" data-preset-edit="${esc(name)}">编辑 JSON</button><button class="link-btn danger-link" data-preset-delete="${esc(name)}">删除</button></div></footer></article>`;
}
function renderTavernCompatibility() {
  head("酒馆兼容与自由配方", "这里是和 SillyTavern 社区资源接轨的入口：导入角色卡或酒馆预设后，它们会被转换为当前工作台可见、可编辑的上下文模块。",
    `<button class="btn" id="tavernRefresh">刷新酒馆预设列表</button><button class="btn primary" id="tavernImportCard">导入酒馆角色卡</button>`);
  const presetCount = presetNames.length;
  const worldCount = (worldBooks[selectedOwner]?.entries || []).length;
  $("#pageBody").innerHTML = `<div class="tavern-hub"><section class="tavern-hero"><div><div class="eyebrow">SILLYTAVERN COMPATIBILITY</div><h3>导入成熟资源，再按自己的规则装配</h3><p>兼容不是复制酒馆的界面。角色卡、世界书和酒馆预设进入后，会拆成可理解的模块，进入当前角色的上下文流。</p></div><div class="tavern-hero-stat"><b>${presetCount}</b><span>已发现酒馆预设</span><b>${worldCount}</b><span>当前角色世界书条目</span></div></section>
  <section class="tavern-actions"><article><span class="module-type character">角色卡</span><h3>导入角色与设定</h3><p>读取酒馆角色卡，转换为 Alife 角色、基础 Prompt 与可用世界书数据。</p><button class="btn primary" id="importTavernCardMain">导入角色卡</button></article><article><span class="module-type preset">酒馆预设</span><h3>导入提示词配方</h3><p>把酒馆预设转换为顺序明确的提示词片段，可在装配台逐个开关、编辑。</p><button class="btn primary" id="importTavernPresetMain">导入酒馆预设</button></article><article><span class="module-type world">世界书</span><h3>管理当前角色世界书</h3><p>世界书将保留关键词、常驻和注入顺序；点击后进入条目管理。</p><button class="btn" id="goWorldbookFromTavern">打开世界书</button></article></section>
  <section class="tavern-options"><div class="section-title"><div><div class="eyebrow">IMPORT OPTIONS</div><h3>导入选项</h3></div></div>
    <div class="tavern-option-row"><div><b>卡内世界书</b><p>角色卡里内嵌的 character_book（剧情设定 / 人物关系 / 状态规则）要不要一起导入。选「每次询问」时，导入角色卡读到世界书就弹窗让你逐张决定，弹窗里可以勾「记住我的选择」。</p></div><select id="cardWorldbookMode">${[["ask","每次询问"],["always","总是导入"],["never","从不导入"]].map(([v,n])=>`<option value="${v}" ${importOptions.cardWorldbook===v?"selected":""}>${n}</option>`).join("")}</select></div>
    <p class="tavern-option-note">当前：<b>${esc(IMPORT_CARD_WORLDBOOK_LABEL[importOptions.cardWorldbook] || "每次询问")}</b> · 这里改的是「装配页 → 角色卡」和本页「导入角色卡」共用的策略。</p></section>
  <section class="tavern-recipe-list"><div class="section-title"><div><div class="eyebrow">IMPORTED RECIPES</div><h3>已发现的酒馆预设配方</h3></div><button class="btn" id="openTavernPresetDrawer">从抽屉选择</button></div>${presetNames.length ? `<div class="tavern-preset-grid">${presetNames.map(name => { const entries = presetData[name]?.Entries || presetData[name]?.entries || []; return `<button class="tavern-preset-row ${name === selectedPreset ? "selected" : ""}" data-tavern-preset="${esc(name)}"><span class="module-type preset">酒馆预设</span><b>${esc(name)}</b><small>${entries.length ? `${entries.length} 个已读取片段` : "点击读取片段"}</small><span>${name === selectedPreset ? "当前使用中" : "选择 →"}</span></button>`; }).join("")}</div>` : `<div class="empty">尚未导入酒馆预设。可以从上方导入，或点击右上角“导入酒馆资源”。</div>`}</section></div>`;
  $("#tavernRefresh").onclick = () => send("presets:list");
  // 导入选项只拉一次：拉回来只更新下拉框本身，不触发 render()，避免「渲染 → 请求 → 回包 → 渲染」的循环。
  if (!importOptionsLoaded) { importOptionsLoaded = true; send("import:options", { owner:selectedOwner }); }
  const modeSelect = $("#cardWorldbookMode");
  if (modeSelect) modeSelect.onchange = e => {
    const value = e.target.value;
    importOptions.cardWorldbook = value;
    send("import:options-save", { owner:selectedOwner, cardWorldbook:value });
    setStatus("卡内世界书策略：" + (IMPORT_CARD_WORLDBOOK_LABEL[value] || value));
    const note = $(".tavern-option-note");
    if (note) note.innerHTML = `当前：<b>${esc(IMPORT_CARD_WORLDBOOK_LABEL[value] || value)}</b> · 这里改的是「装配页 → 角色卡」和本页「导入角色卡」共用的策略。`;
  };
  const importCard = () => send("tavern:import"); const importPreset = () => send("tavern:import-preset");
  $("#tavernImportCard").onclick = importCard; $("#importTavernCardMain").onclick = importCard; $("#importTavernPresetMain").onclick = importPreset;
  $("#goWorldbookFromTavern").onclick = () => { currentView = "worldbook"; render(); };
  $("#openTavernPresetDrawer").onclick = openPresetDrawer;
  $$("[data-tavern-preset]").forEach(button => button.onclick = () => { selectedPreset = button.dataset.tavernPreset; send("presets:get", { name:selectedPreset }); currentView = "assembly"; render(); });
}

function openPresetDrawer() {
  presetDrawerOpen = true;
  renderPresetDrawer();
  if (!presetNames.length) send("presets:list");
}
function closePresetDrawer() {
  presetDrawerOpen = false;
  const drawer = $("#presetDrawer");
  if (drawer) { drawer.classList.remove("open"); drawer.innerHTML = ""; }
}
function renderPresetDrawer() {
  const drawer = $("#presetDrawer");
  if (!drawer) return;
  drawer.classList.toggle("open", presetDrawerOpen);
  if (!presetDrawerOpen) { drawer.innerHTML = ""; return; }
  drawer.innerHTML = `<div class="drawer-scrim" id="presetDrawerScrim"></div><section class="preset-drawer-panel"><header><div><div class="eyebrow">TAVERN PRESET LIBRARY</div><h2>选择一套酒馆预设配方</h2><p>切换酒馆预设不会覆盖角色或世界书，只改变“酒馆预设片段”这一层。</p></div><button class="icon-quiet drawer-close" id="closePresetDrawer">×</button></header><div class="drawer-preset-list">${presetNames.map(name => { const entries = presetData[name]?.Entries || presetData[name]?.entries || []; return `<button class="drawer-preset ${name === selectedPreset ? "selected" : ""}" data-drawer-preset="${esc(name)}"><span class="module-type preset">酒馆预设</span><b>${esc(name)}</b><small>${entries.length ? `${entries.length} 个片段` : "点击读取详细结构"}</small><span>→</span></button>`; }).join("") || `<div class="empty">暂无酒馆预设</div>`}</div><footer><button class="btn primary" id="drawerImportPreset">导入酒馆预设</button><button class="btn" id="drawerManagePresets">管理酒馆预设库</button></footer></section>`;
  $("#closePresetDrawer").onclick = closePresetDrawer;
  $("#presetDrawerScrim").onclick = closePresetDrawer;
  $("#drawerImportPreset").onclick = () => send("tavern:import-preset");
  $("#drawerManagePresets").onclick = () => { closePresetDrawer(); currentView = "presets"; render(); };
  $$("[data-drawer-preset]").forEach(button => button.onclick = () => { selectedPreset = button.dataset.drawerPreset; send("presets:get", { name:selectedPreset }); closePresetDrawer(); if (currentView === "assembly") render(); });
}

function editCurrentPreset() {
  if (!selectedPreset || !presetData[selectedPreset]) return alertError("请先选择酒馆预设");
  openModal("编辑酒馆预设：" + selectedPreset, `<textarea class="large-editor">${esc(JSON.stringify(presetData[selectedPreset], null, 2))}</textarea>
    <div class="toolbar modal-actions"><button class="btn primary" id="savePresetJson">保存</button></div>`);
  $("#savePresetJson").onclick = () => {
    try {
      const preset = JSON.parse($(".large-editor").value);
      send("presets:save", { name:selectedPreset, preset });
      closeModal();
    } catch (ex) { alertError("JSON 格式错误：" + ex.message); }
  };
}

function openHistoryBrowser() {
  historyPageOffset = 0;
  historyPageQuery = "";
  openModal("历史搜索 / 分页", `<div class="history-browser-controls">
      <input id="historyQueryInput" placeholder="搜索角色名、内容关键词，留空显示全部"/>
      <button class="btn primary" id="historySearchBtn">搜索</button>
      <button class="btn" id="historyPrevBtn">上一页</button>
      <button class="btn" id="historyNextBtn">下一页</button>
    </div><div id="historyPageBox" class="history-page-box">正在加载…</div>`);
  $("#historySearchBtn").onclick = () => { historyPageOffset=0; historyPageQuery=$("#historyQueryInput").value; requestHistoryPage(); };
  $("#historyPrevBtn").onclick = () => { historyPageOffset=Math.max(0,historyPageOffset-50); requestHistoryPage(); };
  $("#historyNextBtn").onclick = () => { historyPageOffset+=50; requestHistoryPage(); };
  requestHistoryPage();
}
function requestHistoryPage() { send("history:page", { owner:selectedOwner, offset:historyPageOffset, count:50, query:historyPageQuery }); }
function renderHistoryPage(msg) {
  const box=$("#historyPageBox");
  if (!box) return;
  box.innerHTML=`<div class="history-page-meta">总计 ${msg.total} 条，当前 ${msg.offset + 1}-${Math.min(msg.total,msg.offset+msg.count)}</div>
    <div class="history-result-list">${(msg.items||[]).map(i=>`<div class="history-result" data-id="${i.id}"><span class="role-pill ${roleLabel(i)}">${roleLabel(i)}</span><b>${esc(i.title)}</b><p>${formatText(i.content)}</p></div>`).join("") || emptyMini("没有匹配结果")}</div>`;
  $$(".history-result", box).forEach(r=>r.onclick=()=>openMessage(r.dataset.id));
}

function renderConfigs() {
  head("系统配置", "Storage/Configuration 中的文件属于模块配置，不再混进角色上下文提示词；需要检查时在这里单独查看。");
  const rows = itemsBy(i => i.owner === "全局" || i.sourceKey === "global-config");
  $("#pageBody").innerHTML = `<div class="config-list">${rows.map(r=>`<div class="config-row" data-id="${r.id}"><b>${esc(r.title)}</b><span>${esc(r.path)}</span><em>${fmtTokens(r.estimatedTokens)}</em></div>`).join("") || emptyMini("暂无配置文件")}</div>`;
  $$(".config-row").forEach(r=>r.ondblclick=()=>openMessage(r.dataset.id));
}

function formatText(text) { return esc(text || "").replace(/\n/g,"<br>"); }
function emptyMini(text) { return `<div class="empty">${esc(text)}</div>`; }
function alertError(text) { openModal("提示", `<p class="plain-text">${esc(text)}</p>`); }

function openMessage(id) {
  if (loadingItems.has(id)) return;
  loadingItems.add(id);
  const old = snapshot.items.find(i=>i.id===id);
  openModal(old?.title || "条目", `<div class="modal-loading">正在读取完整内容…</div>`);
  send("item:load", { id });
}

function openLargeEditor(item, value) {
  openModal("大窗口编辑：" + (item?.title || ""), `
    <textarea class="large-editor">${esc(value ?? item?.content ?? "")}</textarea>
    <div class="toolbar modal-actions"><button class="btn primary" id="modalSave">保存并关闭</button><button class="btn" id="modalCopy">复制全文</button></div>`);
  $("#modalSave").onclick = () => {
    if (!item) return closeModal();
    if (guardTruncatedItem(item)) return;
    send("item:update", { id:item.id, content:$(".large-editor").value });
    closeModal();
  };
  $("#modalCopy").onclick = () => navigator.clipboard.writeText($(".large-editor").value);
}

// options.narrow   —— 用窄弹窗（问一句话别铺满 970px）
// options.onDismiss —— 点 × / 点遮罩时的回调。默认就是关掉；
//                      「后端在等我回答」这类弹窗必须传，否则关掉就没人回答后端了。
function openModal(title, html, options = {}) {
  $("#modalRoot").innerHTML = `<div class="modal-mask"><div class="modal ${options.narrow ? "narrow-modal" : "wide-modal"}">
    <div class="modal-head"><h3>${esc(title)}</h3><button class="x-btn" id="modalClose">×</button></div>
    <div class="modal-body">${html}</div>
  </div></div>`;
  const dismiss = typeof options.onDismiss === "function" ? options.onDismiss : closeModal;
  $("#modalClose").onclick = dismiss;
  $(".modal-mask").onclick = e => { if (e.target.classList.contains("modal-mask")) dismiss(); };
}
function closeModal() { $("#modalRoot").innerHTML = ""; charPresetPanelOpen = false; }

// ── 后端问、前端答：这张角色卡内嵌了世界书，要不要一起导入？ ────────────────
//
// 以前这是 Electron 的原生 MessageBox —— Windows 系统对话框：灰底、系统字体、
// 系统按钮布局，标题栏还写的是应用名。和插件界面完全不是一套，一眼就看得出「外来的东西」。
// 现在后端发一条 card-worldbook-ask，这里用插件自己的 openModal 渲染，
// 用户点完把答案通过 card-worldbook-answer 送回后端（后端在 await 这个答案）。
function showCardWorldbookAsk(msg) {
  // 用户在弹窗里思考期间，把待回请求的看门狗往后推：
  // 「导入角色卡」这条请求正卡在后端等答案上，用户想两分钟不该被判成「后端无响应」。
  postponePendingRequests(1800000);
  const titles = Array.isArray(msg.preview) ? msg.preview : [];
  const total = Math.max(0, Number(msg.bookCount) || 0);
  const list = titles.length ? `<ul class="ask-list">${titles.map(t => `<li>${esc(t)}</li>`).join("")}</ul>` : "";
  const more = total > titles.length ? `<p class="ask-more">…还有 ${total - titles.length} 条</p>` : "";
  openModal("这张角色卡内嵌了世界书", `
    <p class="plain-text">「<b>${esc(msg.displayName || "这张卡")}</b>」内嵌了 <b>${total}</b> 条世界书条目。</p>
    ${list}${more}
    <p class="plain-text">这些条目通常是剧情设定、人物关系或状态规则。要一起导入到当前角色的世界书吗？</p>
    <p class="plain-text ask-hint">选择「只要角色卡」不会动你已有的世界书。以后可以在「酒馆兼容」页 →「导入选项」里改成总是导入 / 从不导入。</p>
    <label class="ask-remember"><input type="checkbox" id="cardWbRemember"/>记住我的选择（以后不再询问）</label>
    <div class="toolbar modal-actions">
      <button class="btn" id="cardWbSkip">只要角色卡</button>
      <button class="btn primary" id="cardWbImport">一起导入</button>
    </div>`, { narrow: true, onDismiss: () => answerCardWorldbookAsk(msg, false) });
  $("#cardWbImport").onclick = () => answerCardWorldbookAsk(msg, true);
  $("#cardWbSkip").onclick = () => answerCardWorldbookAsk(msg, false);
}
// 关掉弹窗（× / 点遮罩 / 两个按钮）都算「只要角色卡」——
// 用户已经把文件选好了，把角色卡导进来才是他的本意；后端会照常继续导入。
function answerCardWorldbookAsk(msg, accepted) {
  const remember = !!document.querySelector("#cardWbRemember")?.checked;
  closeModal();
  send("card-worldbook-answer", { requestId: msg.requestId, accepted, remember });
}

function ensureWorldBook(owner) {
  if (ownerInfo(owner).active && !worldBooks[owner]) send("worldbook:get", { owner });
}

window.require("electron").ipcRenderer.on(ipcChannel, (event, raw) => {
  let msg;
  try { msg = JSON.parse(raw); } catch { return; }
  trace("<- " + msg.type + (msg.owner ? " owner=" + msg.owner : ""));
  noteReply(msg.type);
  if (msg.type === "state") {
    snapshot = { ...msg, items: msg.items || [], owners: msg.owners || [], categories: msg.categories || [] };
    // 若窗口加载的是旧缓存的 app.js，明确告知，而不是让按钮看起来“点了没反应”。
    const backendBuild = msg.diagnostics?.appBuild;
    if (backendBuild && backendBuild !== APP_BUILD) {
      trace("BUILD MISMATCH backend=" + backendBuild + " renderer=" + APP_BUILD);
      setStatus("前端为旧版本，请重载插件后重开窗口", false);
      alertError("检测到本窗口加载的仍是旧版前端（后端 " + backendBuild + " / 页面 " + APP_BUILD + "）。\n请重载插件并重新打开本窗口，否则界面操作不会进入新的后端逻辑。");
    }
    if (selectedOwner) { delete nativePrompts[selectedOwner]; delete nativeSystems[selectedOwner]; }
    // 每次 state 都会用新的一份 items 覆盖旧的；正文可能又变回预览，所以已加载标记要清空。
    loadedItems.clear();
    if (msg.degraded || msg.hiddenItems) trace("state degraded steps=" + (msg.degradeSteps || []).join(",") + " hiddenItems=" + (msg.hiddenItems || 0));
    if (!selectedOwner && snapshot.owners.length) selectedOwner = snapshot.owners.find(o => o.active)?.name || snapshot.owners[0].name;
    const initialPlan = snapshot.planJsonByOwner?.[selectedOwner];
    if (initialPlan && (planOwner !== selectedOwner || !planState || !planDirty)) {
      try {
        planState = JSON.parse(initialPlan);
        planOwner = selectedOwner;
        planDirty = false;
        planLoading = false;
        planError = "";
        normalizePlan(planState);
        cachePlan(selectedOwner, planState);
      } catch (_) { /* local draft remains the safe fallback */ }
    }
    // ⚠️ 不要无条件清空 loadedOfflineOwner。
    // state 已经带上「当前查看角色」的完整条目（后端 BuildOwnerItems + fullContent:true），
    // 而无条件清空会让**每一次** state 推送都重拉一遍 character-bundle（300+ KB）和
    // worldbook:get —— 2026-09-29 现场：出站队列被这些大回包顶住（限速 512 KB/600ms，
    // 一个 state+bundle 周期要 1.4 秒），结果两次 worldbook:get 的小回包在队列里撞在一起
    // 被后端合并成一条，前端 15 秒后误报「worldbook:get 没有收到后端响应」。
    // 只有当 state 里确实没有这个角色的条目时（切到一个未被内联的角色），才回退到按需加载。
    if (selectedOwner && !(snapshot.items || []).some(i => i.owner === selectedOwner)) loadedOfflineOwner = "";
    send("presets:list");

    setStatus(planNotice && Date.now() - planNotice.at < 8000 ? planNotice.text : "就绪");
    render();
    if (pendingChatAutoScroll) {
      requestAnimationFrame(() => {
        const log = $(".chat-log");
        if (log) { log.scrollTop = log.scrollHeight; pendingChatAutoScroll = false; }
      });
    }
  } else if (msg.type === "native-prompt") {
    if (!ownerInfo(msg.owner).active) return;
    nativePrompts[msg.owner] = String(msg.prompt ?? "");
    // system = 完整官方系统消息（名称/生日/简介/设定/私人文件夹），装配页的「角色设定 #0」用它。
    if (typeof msg.system === "string" && msg.system) nativeSystems[msg.owner] = msg.system;
    if (snapshot) {
      snapshot.characterPromptByOwner ||= {};
      snapshot.characterPromptByOwner[msg.owner] = nativePrompts[msg.owner];
    }
    // 这里以前会在「角色设定 #0」内容为空时回填磁盘 Prompt / 官方系统消息。
    // 用户在装配页主动「重置为空白」之后，任何一次 state 推送都会让空白复活，所以回填已移除：
    // 本地草稿由 localDefaultPlan 用 nativeSystemContent 填好，不需要事后补。
    if (msg.owner === selectedOwner && !planDirty) {
      if (currentView === "assembly" && document.activeElement?.id !== "nativeCoreContent") render();
    }
  } else if (msg.type === "charpreset-list") {
    characterPresets[msg.owner] = Array.isArray(msg.slots) ? msg.slots : [];
    if (charPresetPanelOpen && msg.owner === selectedOwner) renderCharPresetModalList();
    if (currentView === "charpresets" && msg.owner === selectedOwner) render();
  } else if (msg.type === "charpreset-saved") {
    planNotice = { text:`已保存角色预设「${msg.name}」`, at:Date.now() };
    setStatus(planNotice.text);
  } else if (msg.type === "charpreset-loaded") {
    planNotice = { text: (msg.applied ? `已应用角色预设「${msg.name}」（${planModeLabel(msg.mode)}）` : `已读取角色预设「${msg.name}」到编辑器，请检查后应用`) + (msg.sources ? `；${msg.sources}` : ""), at:Date.now() };
    setStatus(planNotice.text);
    if (!msg.applied && currentView !== "assembly") { currentView = "assembly"; render(); }
  } else if (msg.type === "charpreset-deleted") {
    setStatus(`已删除角色预设「${msg.name}」`);
  } else if (msg.type === "charpreset-exported") {
    const extras = [];
    if (Number(msg.worldbook) > 0) extras.push(`世界书 ${Number(msg.worldbook)} 条`);
    if (msg.card) extras.push("酒馆角色卡");
    if (msg.activePreset) extras.push(`酒馆预设「${msg.activePreset}」`);
    planNotice = { text:`已导出角色预设「${msg.name}」（${Number(msg.modules) || 0} 个模块${extras.length ? " + " + extras.join("、") : ""}）`, at:Date.now() };
    setStatus(planNotice.text);
    openModal("导出完成", `<p class="plain-text">已写出 ${Number(msg.modules) || 0} 个模块的角色上下文记录：</p><pre class="preview-code">${esc(msg.path)}</pre><p class="plain-text">${extras.length ? `文件里还带上了设定来源：${esc(extras.join("、"))}。` : "这份记录只含装配配方，没有抓到设定来源（角色目录下没有 TavernCard.json / WorldBook.json）。"}换到别的角色后，在「角色预设」页点「导入角色记录」选这个文件即可；缺失的世界书 / 角色卡会自动补上，已有的不会被覆盖。</p>`);
  } else if (msg.type === "charpreset-export-cancelled") {
    setStatus("已取消导出");
  } else if (msg.type === "charpreset-imported") {
    const from = msg.from && msg.from !== msg.owner ? `（原本属于「${msg.from}」）` : "";
    planNotice = { text:`已导入角色预设「${msg.name}」到 ${msg.owner}${from}${msg.sources ? `；${msg.sources}` : ""}`, at:Date.now() };
    setStatus(planNotice.text);
    if (currentView !== "charpresets") { currentView = "charpresets"; }
    render();
  } else if (msg.type === "charpreset-import-cancelled") {
    setStatus("已取消导入");
  } else if (msg.type === "plan-state") {
    if (msg.owner !== selectedOwner) return;
    if (planDirty) return;
    clearTimeout(planTimer); planLoading = false; planOwner = msg.owner;
    const previous = planState;
    planState = msg.plan || null;
    planDirty = false;
    if (planState) { normalizePlan(planState); keepLoadedModuleContent(planState, previous); cachePlan(msg.owner, planState); }
    setStatus("计划已就绪");
    if (currentView === "assembly") render();
    // 把「只有预览」的模块排进后台队列。放在 render 之后：先让用户看到结构，再慢慢补正文。
    autoFetchPlanModules();
  } else if (msg.type === "plan-error") {
    if(msg.owner!==selectedOwner)return;
    clearTimeout(planTimer);planLoading=false;planError=msg.message;planDirty=true;setStatus(msg.message,false);alertError(msg.message);
  } else if (msg.type === "plan-saved") {
    if (msg.owner !== selectedOwner) return;
    planNotice = { text:`草稿已保存（${msg.count} 个模块）`, at:Date.now() };
    setStatus(planNotice.text);
  } else if (msg.type === "plan-applied") {
    if (msg.owner !== selectedOwner) return;
    const label = ({Temporary:"插件覆盖",Permanent:"本地覆盖",Off:"关闭覆盖"})[msg.mode] || msg.mode;
    const list = unresolvedMacros(msg);
    planNotice = { text:`已应用：${label}${macroNoticeText(msg)}`, at:Date.now() };
    setStatus(planNotice.text);
    if (list.length) openModal("应用完成，但有未识别的模板宏", `<p class="plain-text">以下宏没有对应的定义，已按原文保留在发送内容里：</p><pre class="preview-code">${esc(list.join("\n"))}</pre><p class="plain-text">可在装配页“宏设置”里为它们定义取值，或取消勾选“启用 Handlebars 宏”改为按原文发送。</p>`);
  } else if (msg.type === "plan-imported") {
    if (msg.owner !== selectedOwner) return;
    if (msg.kind === 'preset' && msg.name) selectedPreset = msg.name;
    planNotice = { text:`已导入 ${msg.count} 个模块${msg.note ? '；' + msg.note : ''}，请检查后应用`, at:Date.now() };
    setStatus(planNotice.text);
    // 注意：note 里可能同时有「字段被跳过」和「正文只下发了预览」两种信息，
    // 所以标题不能写死成「有字段被跳过」。顺序上这条永远晚于 plan-state，
    // 不会再出现「错误弹窗被导入完成顶掉」。
    if (msg.note) openModal("导入完成，请注意以下几点", `<p class="plain-text">${esc(msg.note)}</p>`);
  } else if (msg.type === "plan-import-cancelled") {
    if (msg.owner === selectedOwner) setStatus("已取消导入");
  } else if (msg.type === "plan-loading") {
    if (msg.owner === selectedOwner) setStatus("后端已收到计划请求…");
  } else if (msg.type === "plan-unavailable") {
    if (msg.owner !== selectedOwner) return;
    clearTimeout(planTimer); planLoading = false; planState = null; planError="角色未激活，计划不可用"; setStatus(planError, false);
  } else if (msg.type === "owner-inactive") {
    if (msg.owner === selectedOwner) setStatus("角色未激活，未读取本地上下文", false);
  } else if (msg.type === "plan-preview") {
    if(msg.owner!==selectedOwner)return;
    const list=unresolvedMacros(msg);
    const banner=list.length?`<div class="preview-warn"><b>未识别的模板宏（已按原文保留）：</b>${list.map(k=>`<code>${esc(k)}</code>`).join(' ')}<br/>可在装配页“宏设置”中为它们定义取值，或取消勾选“启用 Handlebars 宏”。</div>`:'';
    const warns=Array.isArray(msg.warnings)?msg.warnings:[];
    const warnBanner=warns.length?`<div class="preview-warn"><b>装配降级提示（相关模块已保留原文）：</b><br/>${warns.map(esc).join('<br/>')}</div>`:'';
    openModal(msg.mode==='Off' ? "装配草稿预览（覆盖尚未启用）" : "装配发送顺序预览",`${banner}${warnBanner}<pre class="preview-code">${esc(msg.messages.map(m=>`[${m.index}] ${m.role}\n${m.content}`).join('\n\n'))}</pre>`);
  } else if (msg.type === "item-state") {
    loadingItems.delete(msg.item.id);
    // 这是全文（后端 ReadFullContent 的结果），记下来，之后保存就不会被 guardTruncatedItem 拦。
    loadedItems.add(msg.item.id);
    const idx = snapshot.items.findIndex(i=>i.id===msg.item.id);
    if (idx >= 0) snapshot.items[idx] = msg.item; else snapshot.items.push(msg.item);
    renderFullInModal(msg.item); renderNav();
  } else if (msg.type === "character-bundle") {
    if (loadedOfflineOwner !== msg.owner || msg.requestId !== bundleRequest) return;
    bundleLoading = false;
    // 只移除该角色之前按需加载的离线条目，保留角色卡与全局条目
    snapshot.items = snapshot.items.filter(i => i.owner !== msg.owner);
    snapshot.items.push(...(msg.items || []));
    snapshot.omittedByOwner ||= {};
    snapshot.omittedByOwner[msg.owner] = Math.max(0, Number(msg.omitted) || 0);
    // 分页 offset 从「后端这次实际发出来多少条」起步，而不是数前端持有的条数 ——
    // 后者会被去重、被 ensureOwnerLoaded 的过滤影响，算错就会翻页漏条。
    snapshot.pageOffsetByOwner ||= {};
    snapshot.pageOffsetByOwner[msg.owner] = (msg.items || []).length;
    trace(`character-bundle owner=${msg.owner} got=${(msg.items || []).length} omitted=${snapshot.omittedByOwner[msg.owner]}`);
    setStatus(msg.inactive ? "角色未激活，未加载本地上下文" : "已加载 " + msg.owner + " 的实时上下文");
    render();
  } else if (msg.type === "items-page") {
    const incoming = msg.items || [];
    const known = new Set((snapshot.items || []).map(i => i.id));
    const fresh = incoming.filter(i => !known.has(i.id));
    snapshot.items.push(...fresh);
    // offset 用后端回传的 nextOffset：它按 OrderForFill 往后推进，不受去重影响。
    snapshot.pageOffsetByOwner ||= {};
    snapshot.pageOffsetByOwner[msg.owner] = Number(msg.nextOffset) || ((Number(msg.offset) || 0) + incoming.length);
    // remaining 也由后端算（后端才知道这个角色一共多少条）。
    // 后端一条都没发出来时强制归零，否则按钮会一直点、一直没反应。
    snapshot.omittedByOwner ||= {};
    snapshot.omittedByOwner[msg.owner] = incoming.length === 0 ? 0 : Math.max(0, Number(msg.remaining) || 0);
    trace(`items-page owner=${msg.owner} offset=${msg.offset} got=${incoming.length} fresh=${fresh.length} remaining=${snapshot.omittedByOwner[msg.owner]}`);
    setStatus(incoming.length ? `已加载 ${fresh.length} 条更早的条目` : "没有更多条目了");
    render();
  } else if (msg.type === "character-fields") {
    if (currentView === "prompts" && msg.name === selectedOwner) {
      if (msg.birthday) $("#pfBirthday").value = String(msg.birthday).replace(" ","T").slice(0,16);
      if (typeof msg.description === "string") $("#pfDescription").value = msg.description;
      $("#officialSystemPreview").textContent = buildOfficialSystemPreview(msg.name, $("#pfBirthday").value, $("#pfDescription").value, $("#pfPrompt").value);
    }
  } else if (msg.type === "character-saved") {
    setStatus("角色已保存"); send("context:refresh");
  } else if (msg.type === "busy") {
    setStatus(msg.message || "处理中…");
  } else if (msg.type === "chat-finished") {
    setStatus("API 回复完成"); send("context:refresh");
  } else if (msg.type === "chat-pruned") {
    planNotice = { text:`已删除 ${msg.removed || 0} 条未参与装配的对话`, at:Date.now() };
    setStatus(planNotice.text);
  } else if (msg.type === "history-page") {
    renderHistoryPage(msg);
  } else if (msg.type === "presets-list") {
    presetNames = msg.names || [];
    if (currentView === "presets") renderPresets();
    if (currentView === "assembly" && !$("#planPreset")?.matches(":focus")) { const select=$("#planPreset"); if(select)select.innerHTML=`<option value="">选择酒馆预设</option>`+presetNames.map(n=>`<option value="${esc(n)}" ${n===selectedPreset?"selected":""}>${esc(n)}</option>`).join(""); }
    if (presetDrawerOpen) renderPresetDrawer();
  } else if (msg.type === "preset-state") {
    presetData[msg.name] = msg.preset;
    selectedPreset = msg.name;
    if (currentView === "presets") renderPresets();
    if (currentView === "assembly") render();
    if (presetDrawerOpen) renderPresetDrawer();
  } else if (msg.type === "preset-deleted") {
    delete presetData[msg.name];
    presetNames = presetNames.filter(name => name !== msg.name);
    if (selectedPreset === msg.name) selectedPreset = "";
    if (presetDrawerOpen) renderPresetDrawer();
    if (currentView === "presets" || currentView === "assembly") render();
  } else if (msg.type === "worldbook-state") {
    if(msg.owner!==selectedOwner)return;
    const incoming = msg.entries || [];
    const offset = Math.max(0, Number(msg.offset) || 0);
    worldBookMeta[msg.owner] = {
      offset,
      nextOffset: Number(msg.nextOffset) || 0,
      remaining: Math.max(0, Number(msg.remaining) || 0),
      total: Number(msg.total) || incoming.length,
      truncatedCount: Number(msg.truncatedCount) || 0
    };
    if (offset > 0) {
      // 分页续读（worldbook:page）：每页都是完整正文，按 id 合并进已有列表。
      const existing = worldBooks[msg.owner]?.entries || [];
      const byId = new Map(existing.map(e => [e.id, e]));
      incoming.forEach(e => { byId.set(e.id, e); loadedWorldEntries.add(e.id); });
      worldBooks[msg.owner] = { entries: Array.from(byId.values()) };
    } else {
      // 首次下发同样是「整份替换」。但用户刚读过全文、或刚编辑过的条目，
      // 这一轮又会以预览出现 —— 把本地那份贴回去，别让界面退回去。
      const prevById = new Map((worldBooks[msg.owner]?.entries || []).map(e => [e.id, e]));
      incoming.forEach(e => {
        if (!e) return;
        if (!e.truncated) { loadedWorldEntries.add(e.id); return; }
        const old = prevById.get(e.id);
        if (old && loadedWorldEntries.has(e.id) && typeof old.content === "string") {
          e.content = old.content;
          e.contentLength = Number(old.contentLength) || old.content.length;
          e.truncated = false;
          return;
        }
        loadedWorldEntries.delete(e.id);
      });
      worldBooks[msg.owner] = { entries: incoming };
    }
    if (currentView === "worldbook" || currentView === "assembly") render();
    if (worldLoadingAll) continueWorldPaging();
    // 首次下发里只要有「只有预览」的条目、或还有没读回来的条目，就自动开始分页读全文。
    // 没有截断也没有剩余时（内容全都在预算内）什么都不做 —— 避免每次保存都白跑一轮。
    else if (autoFullText && (worldBookMeta[msg.owner].truncatedCount > 0 || worldBookMeta[msg.owner].remaining > 0)) {
      loadAllWorldContent();
    }
  } else if (msg.type === "worldbook-entry") {
    if (msg.owner !== selectedOwner) return;
    if (msg.error) { setStatus(msg.error, false); return; }
    const entry = (worldBooks[msg.owner]?.entries || []).find(e => e.id === msg.id);
    if (!entry) { setStatus("这条世界书条目已经不在列表里了，请刷新", false); return; }
    loadedWorldEntries.add(msg.id);
    entry.content = msg.content || "";
    entry.contentLength = Number(msg.contentLength) || (msg.content || "").length;
    entry.truncated = false;
    setStatus(`已读取「${entry.title || msg.title || "该条目"}」的完整正文（${Number(entry.contentLength).toLocaleString()} 字）`);
    if (currentView === "worldbook" || currentView === "assembly") render();
  } else if (msg.type === "plan-module") {
    if (msg.owner !== selectedOwner) return;
    // 自动队列推进：无论这一条成功还是失败，都要把下一条发出去（否则一条出错就断链）。
    autoPlanFetching = false;
    clearTimeout(autoPlanTimer);
    if (msg.error) { setStatus(msg.error, false); alertError(msg.error); pumpAutoPlanFetch(); return; }
    const m = planState?.modules.find(x => x.id === msg.id);
    if (!m) { setStatus("这个模块已经不在装配计划里了，请刷新", false); pumpAutoPlanFetch(); return; }
    loadedPlanModules.add(msg.id);
    m.content = msg.content || "";
    m.contentLength = Number(msg.contentLength) || (msg.content || "").length;
    m.truncated = false;
    if (planState) { normalizePlan(planState); cachePlan(msg.owner, planState); }
    if (autoPlanTotal > 0) autoPlanDone++;
    render();
    // 队列还有 → pump 继续发下一条（状态栏显示进度）；
    // 队列空了但这是自动拉取的一轮 → pump 会给出「已自动读取全部模块正文（N 条）」；
    // 用户手点的「读取全文」（total 为 0）→ pump 什么都不做，这里自己说清读回的是哪一条。
    const manualFetch = autoPlanTotal === 0;
    pumpAutoPlanFetch();
    if (manualFetch) setStatus(`已读取「${msg.name || m.name}」的完整正文（${Number(m.contentLength).toLocaleString()} 字）`);
  } else if (msg.type === "tavern-imported") {
    setStatus("酒馆角色卡已导入"); send("context:refresh");
    openModal("导入完成", `<p class="plain-text">已导入角色：<b>${esc(msg.name)}</b></p>`
      + (msg.note ? `<p class="plain-text">${esc(msg.note)}</p>` : ""));
  } else if (msg.type === "card-worldbook-ask") {
    // 后端在 await 这个答案：一定要回答（两个按钮 / × / 点遮罩都会回答）。
    showCardWorldbookAsk(msg);
  } else if (msg.type === "import-options") {
    // 只更新下拉框与说明，不 render()：这个回包可能是页面渲染时发出的请求触发的。
    importOptions = { cardWorldbook: msg.cardWorldbook || "ask" };
    const select = $("#cardWorldbookMode");
    if (select) select.value = importOptions.cardWorldbook;
    const note = $(".tavern-option-note");
    if (note) note.innerHTML = `当前：<b>${esc(IMPORT_CARD_WORLDBOOK_LABEL[importOptions.cardWorldbook] || importOptions.cardWorldbook)}</b> · 这里改的是「装配页 → 角色卡」和本页「导入角色卡」共用的策略。`;
  } else if (msg.type === "error") {
    setStatus("有错误", false);
    openModal("错误", `<p class="plain-text">${esc(msg.message)}</p>`);
  }
});

function renderFullInModal(item) {
  let body = "";
  if ((item.attachments || []).length) body += `<div class="media-grid modal-media">${[item].map(mediaCard).join("")}</div>`;
  body += `<textarea class="large-editor">${esc(item.content || "")}</textarea>
    <div class="toolbar modal-actions">
      ${item.readOnly ? "" : `<button class="btn primary" id="modalSaveItem">保存修改</button>`}
      <button class="btn" id="modalCopyItem">复制全文</button>
    </div>`;
  openModal(item.title, body);
  $("#modalCopyItem")?.addEventListener("click", () => navigator.clipboard.writeText(item.content || ""));
  $("#modalSaveItem")?.addEventListener("click", () => {
    if (guardTruncatedItem(item)) return;
    send("item:update", { id:item.id, content:$(".large-editor").value }); closeModal();
  });
}


document.addEventListener("click", (event) => {
  // 「读取全文」按钮出现在装配卡片、右侧编辑区、编辑大窗口、世界书面板等多个位置，
  // 统一在这里用捕获阶段处理：stopPropagation 之后元素自己的 onclick 不会再触发，
  // 也就不会出现「点一次发两个请求」。
  const planLoad = event.target.closest?.("[data-plan-load]");
  if (planLoad) {
    event.preventDefault();
    event.stopPropagation();
    loadPlanModule(planLoad.dataset.planLoad);
    return;
  }
  const worldLoad = event.target.closest?.("[data-world-load]");
  if (worldLoad) {
    event.preventDefault();
    event.stopPropagation();
    loadWorldEntry(worldLoad.dataset.worldLoad);
    return;
  }
  const worldLoadAll = event.target.closest?.("[data-world-load-all]");
  if (worldLoadAll) {
    event.preventDefault();
    event.stopPropagation();
    loadAllWorldContent();
    return;
  }

  // 右栏搜索结果的「跳转」：选中该模块并把中间列的卡片滚进视野。
  const moduleJump = event.target.closest?.("[data-module-jump]");
  if (moduleJump) {
    event.preventDefault();
    event.stopPropagation();
    jumpToModule(moduleJump.dataset.moduleJump);
    return;
  }

  const ownerSwitch = event.target.closest?.("[data-owner-switch]");
  if (ownerSwitch) {
    event.preventDefault();
    selectedOwner = ownerSwitch.dataset.ownerSwitch;
    render();
    ensureOwnerLoaded(selectedOwner);
    return;
  }

  const cardAction = event.target.closest?.("[data-card-action]");
  if (cardAction && snapshot) {
    event.preventDefault();
    event.stopPropagation();
    const owner = cardAction.dataset.owner || cardAction.closest("[data-owner]")?.dataset.owner;
    if (!owner) return;
    selectedOwner = owner;
    const action = cardAction.dataset.cardAction;
    const info = ownerInfo(owner);
    if (action === "activate") send(info.active ? "character:deactivate" : "character:activate", { owner });
    else {
      currentView = { prompt:"prompts", chat:"chat", memory:"memory" }[action] || "prompts";
      render();
      ensureOwnerLoaded(owner);
    }
  }
}, true);
$("#minBtn").onclick = () => send("window:minimize");
$("#maxBtn").onclick = () => send("window:toggle-maximize");
$("#closeBtn").onclick = () => send("window:close");
$("#importTavernBtn").onclick = () => send("tavern:import");
$("#newCharacterBtn").onclick = () => {
  openModal("新建角色", `<div class="field"><label>角色名</label><input id="newCharacterName" placeholder="输入角色名称" /></div>
    <div class="toolbar modal-actions"><button class="btn primary" id="createCharacterConfirm">创建</button></div>`);
  // 不用 HTML 的 autofocus：动态插入的节点在 Electron 窗口里不一定拿到焦点，
  // 显式 focus() 更可靠（否则用户点进输入框才发现没聚焦）。
  $("#newCharacterName").focus();
  $("#createCharacterConfirm").onclick = () => {
    const name = $("#newCharacterName").value.trim();
    if (!name) return alertError("请输入角色名");
    send("character:create", { name });
    closeModal();
  };
};

setStatus("等待后端数据…", false);
renderNav();

send("context:refresh");
setTimeout(() => {
  if (!snapshot) {
    setStatus("未收到后端数据：请重载插件并重新打开本窗口", false);
    trace("boot: no state reply within 15s");
  }
}, 15000);
