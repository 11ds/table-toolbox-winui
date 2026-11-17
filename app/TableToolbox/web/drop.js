/* ============================================================
   文件拖放导入（新增交互，不改动原有导入流程）
   做法：把拖进来的文件塞进该表格卡片原本隐藏的
   <input type="file" multiple>，再派发一次 change 事件，
   页面原有的 handleFile() 就会照常处理 —— 解析、工作表选择、
   多表追加、错误提示，全部复用原逻辑，没有第二套实现。
   ============================================================ */
(function () {
  'use strict';

  var EXT = /\.(xlsx|xls|xlsm|xlsb|et|csv|txt|tsv|tab)$/i;
  var ARROW = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#0067C0" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v10m0 0l-3.5-3.5M12 14l3.5-3.5"/><path d="M4 16v2a2 2 0 002 2h12a2 2 0 002-2v-2"/></svg>';

  var mask = null;
  var activeCard = null;
  var dragDepth = 0;

  /* ---------- 小提示（与页面 toast 风格一致，独立实现，互不影响） ---------- */
  function toast(msg, ms) {
    var el = document.createElement('div');
    el.className = 'nw-toast';
    el.textContent = msg;
    document.body.appendChild(el);
    var life = ms || 2600;
    setTimeout(function () { el.classList.add('out'); }, life - 220);
    setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, life + 60);
  }

  /* ---------- 拖拽遮罩 ---------- */
  function ensureMask() {
    if (mask) return mask;
    mask = document.createElement('div');
    mask.id = 'nwDropMask';
    mask.innerHTML =
      '<div id="nwDropBox">' +
        '<div class="nw-drop-icon">' + ARROW + '</div>' +
        '<div class="nw-drop-title">松开即可导入</div>' +
        '<div class="nw-drop-sub">Excel / WPS / CSV / TXT</div>' +
        '<div class="nw-drop-tip">可一次拖入多个文件 · 对着某张表松手就导入那一张</div>' +
      '</div>';
    document.body.appendChild(mask);
    return mask;
  }

  function showMask() {
    ensureMask().classList.add('show');
  }

  function hideMask() {
    dragDepth = 0;
    if (mask) mask.classList.remove('show');
    highlight(null);
  }

  function highlight(card) {
    if (activeCard === card) return;
    if (activeCard) activeCard.classList.remove('nw-target');
    activeCard = card;
    if (activeCard) activeCard.classList.add('nw-target');
  }

  /* ---------- 工具 ---------- */
  function hasFiles(e) {
    var dt = e.dataTransfer;
    if (!dt || !dt.types) return false;
    for (var i = 0; i < dt.types.length; i++) {
      if (dt.types[i] === 'Files') return true;
    }
    return false;
  }

  function cardAt(x, y) {
    var el = document.elementFromPoint(x, y);
    if (!el || !el.closest) return null;
    return el.closest('.table-card');
  }

  /* 没落在某张卡片上时：优先给第一张空表，否则给第一张表
     （与原「上传」按钮的落点规则一致：空表填进去，非空则新增） */
  function fallbackCard() {
    var cards = [].slice.call(document.querySelectorAll('.table-card'));
    if (!cards.length) return null;
    for (var i = 0; i < cards.length; i++) {
      var ta = cards[i].querySelector('textarea');
      if (ta && !ta.value.trim()) return cards[i];
    }
    return cards[0];
  }

  function importInto(card, files) {
    if (!card || !files.length) return;

    var ok = [], bad = [];
    files.forEach(function (f) { (EXT.test(f.name) ? ok : bad).push(f); });

    if (bad.length) {
      toast('这些文件类型不支持：' + bad.map(function (f) { return f.name; }).join('、') +
            '（可用 .xlsx .xls .xlsm .xlsb .et .csv .txt）', 4800);
    }
    if (!ok.length) return;

    var input = card.querySelector('input[type=file]');
    if (!input) { toast('这张表没有可用的导入入口，请刷新页面重试'); return; }

    try {
      var dt = new DataTransfer();
      ok.forEach(function (f) { dt.items.add(f); });
      input.files = dt.files;
      input.dispatchEvent(new Event('change', { bubbles: true }));
      toast(ok.length > 1 ? ('正在导入 ' + ok.length + ' 个文件…') : ('正在导入「' + ok[0].name + '」…'), 2000);
    } catch (err) {
      toast('导入失败：' + (err && err.message ? err.message : err), 4800);
    }
  }

  /* ---------- 事件 ---------- */
  document.addEventListener('dragenter', function (e) {
    if (!hasFiles(e)) return;          // 拖文字、拖选区时完全不插手
    e.preventDefault();
    dragDepth++;
    showMask();
    highlight(cardAt(e.clientX, e.clientY));
  }, true);

  document.addEventListener('dragover', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    try { e.dataTransfer.dropEffect = 'copy'; } catch (ignored) {}
    if (!mask || !mask.classList.contains('show')) showMask();
    highlight(cardAt(e.clientX, e.clientY));
  }, true);

  document.addEventListener('dragleave', function (e) {
    if (!hasFiles(e)) return;
    dragDepth = Math.max(0, dragDepth - 1);
    if (dragDepth === 0) hideMask();
  }, true);

  document.addEventListener('drop', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.stopPropagation();

    var card = cardAt(e.clientX, e.clientY) || fallbackCard();
    var files = [].slice.call(e.dataTransfer.files || []);
    hideMask();
    importInto(card, files);
  }, true);

  window.addEventListener('dragend', hideMask);
  window.addEventListener('blur', hideMask);

  /* ---------- 页头加一句拖放提示（纯装饰，不改原有文字） ---------- */
  (function addHint() {
    var sub = document.querySelector('.sub');
    if (!sub || sub.querySelector('.nw-drag-hint')) return;
    var hint = document.createElement('span');
    hint.className = 'nw-drag-hint';
    hint.textContent = '文件可直接拖进窗口';
    sub.appendChild(hint);
  })();
})();
