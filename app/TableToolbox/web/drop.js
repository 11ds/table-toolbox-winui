/* ============================================================
   文件拖放导入（新增交互层，不改动原有导入流程）
   ------------------------------------------------------------
   两条入口，最终都汇到同一个函数 importInto()：
   1) 窗口内 HTML5 拖放（浏览器 / WebView2 允许时）
   2) 原生窗口拖放：C# 侧把文件读成 base64，调用
      window.__nwImportFiles([{name, b64}])，这里还原成 File 对象
   两条路都不碰解析逻辑：文件塞进卡片原本隐藏的
   <input type="file" multiple>，再派发 change，
   页面原有的 handleFile() 照常处理（解析、选工作表、多表追加、报错）。
   ============================================================ */
(function () {
  'use strict';

  var EXT = /\.(xlsx|xls|xlsm|xlsb|et|csv|txt|tsv|tab)$/i;
  var ARROW = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#0067C0" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v10m0 0l-3.5-3.5M12 14l3.5-3.5"/><path d="M4 16v2a2 2 0 002 2h12a2 2 0 002-2v-2"/></svg>';

  var mask = null;
  var activeCard = null;
  var dragDepth = 0;

  /* ---------- 提示条 ---------- */
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
        '<div class="nw-drop-tip">可一次拖入多个 · 对着某张表松手就进那一张</div>' +
      '</div>';
    document.body.appendChild(mask);
    return mask;
  }

  function showMask() { ensureMask().classList.add('show'); }
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

  /* 没落在卡片上：先给第一张空表，否则给第一张表
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
    if (!input) { toast('这张表没有导入入口，请刷新页面重试'); return; }

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

  /* ---------- 原生窗口调用入口（C# 拖放用） ---------- */
  /* items: [{ name: 'a.csv', b64: '...' }]
     index: 目标卡片序号（可选，-1/不传 = 按落点规则） */
  function b64ToBytes(b64) {
    var bin = atob(b64.replace(/\s+/g, ''));
    var len = bin.length;
    var bytes = new Uint8Array(len);
    for (var i = 0; i < len; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  }

  function makeFile(item) {
    var bytes = b64ToBytes(item.b64 || '');
    var type = /\.csv$/i.test(item.name) ? 'text/csv'
             : /\.(txt|tsv|tab)$/i.test(item.name) ? 'text/plain'
             : 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
    return new File([bytes], item.name, { type: type });
  }

  window.__nwImportFiles = function (items, index) {
    try {
      if (!items || !items.length) return 'no-files';
      var cards = [].slice.call(document.querySelectorAll('.table-card'));
      var card = (typeof index === 'number' && index >= 0 && cards[index]) ? cards[index] : fallbackCard();
      if (!card) return 'no-card';
      var files = items.map(makeFile);
      importInto(card, files);
      return 'ok:' + files.length;
    } catch (err) {
      toast('导入失败：' + (err && err.message ? err.message : err), 4800);
      return 'error:' + (err && err.message ? err.message : err);
    }
  };

  /* 原生拖动态：显示/隐藏遮罩（WebView2 内部不派发 HTML5 drag 时由外壳调用） */
  window.__nwDragState = function (on, index) {
    if (on) {
      showMask();
      var cards = [].slice.call(document.querySelectorAll('.table-card'));
      highlight((typeof index === 'number' && index >= 0 && cards[index]) ? cards[index] : null);
    } else {
      hideMask();
    }
  };

  /* ---------- HTML5 拖放 ---------- */
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

  /* ---------- 页头提示胶囊（纯装饰） ---------- */
  (function addHint() {
    var sub = document.querySelector('.sub');
    if (!sub || sub.querySelector('.nw-drag-hint')) return;
    var hint = document.createElement('span');
    hint.className = 'nw-drag-hint';
    hint.textContent = '文件可直接拖进窗口';
    sub.appendChild(hint);
  })();
})();
