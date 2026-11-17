/* ============================================================
   应用外壳（左侧导航 + 顶栏 + 内容区）
   ------------------------------------------------------------
   只搬动容器位置，不改任何逻辑：
   - 原 .wrap 里的面板整块移到内容区（DOM 节点被移动，事件监听依旧有效）
   - 顶部两个分段按钮仍是页面原来那两个（由外壳替用户点击）
   - 步骤列表由面板标题自动生成，点击只是滚动定位
   ============================================================ */
(function () {
  'use strict';

  if (document.getElementById('nwShell')) return;

  function q(s, r) { return (r || document).querySelector(s); }
  function qa(s, r) { return [].slice.call((r || document).querySelectorAll(s)); }

  function build() {
    var wrap = q('.wrap');
    if (!wrap) return false;

    var h1 = q('.wrap h1');
    var sub = q('.wrap .sub');
    var tabs = q('.wrap .toolbox-tabs');
    var panels = qa('.wrap > section.panel');
    if (!panels.length) return false;

    /* ---------- 骨架 ---------- */
    var shell = document.createElement('div');
    shell.id = 'nwShell';

    var rail = document.createElement('aside');
    rail.id = 'nwRail';

    var brand = document.createElement('div');
    brand.className = 'nw-brand';
    brand.innerHTML = '<span class="nw-brand-mark">表</span><span class="nw-brand-text">表格工具箱</span>';

    var nav = document.createElement('nav');
    nav.className = 'nw-nav';
    nav.id = 'nwNav';

    var stepsTitle = document.createElement('div');
    stepsTitle.className = 'nw-rail-title';
    stepsTitle.textContent = '步骤';

    var steps = document.createElement('div');
    steps.className = 'nw-steps';
    steps.id = 'nwSteps';

    var foot = document.createElement('div');
    foot.className = 'nw-rail-foot';
    foot.innerHTML = '<span class="nw-dot"></span>本地离线 · 数据不出电脑';

    rail.appendChild(brand);
    rail.appendChild(nav);
    rail.appendChild(stepsTitle);
    rail.appendChild(steps);
    rail.appendChild(foot);

    var main = document.createElement('main');
    main.id = 'nwMain';

    var top = document.createElement('header');
    top.className = 'nw-topbar';
    var topLeft = document.createElement('div');
    topLeft.className = 'nw-topbar-left';
    var topRight = document.createElement('div');
    topRight.className = 'nw-topbar-right';
    top.appendChild(topLeft);
    top.appendChild(topRight);

    var content = document.createElement('div');
    content.id = 'nwContent';

    main.appendChild(top);
    main.appendChild(content);
    shell.appendChild(rail);
    shell.appendChild(main);
    document.body.appendChild(shell);

    /* ---------- 搬家：顶栏显示当前分区名，面板进内容区 ---------- */
    var titleEl = document.createElement('div');
    titleEl.className = 'nw-topbar-title';
    titleEl.textContent = '表格对比';
    topLeft.appendChild(titleEl);
    if (h1) { h1.className += ' nw-hide'; topLeft.appendChild(h1); }   // 品牌已在左栏，避免重复
    if (sub) topLeft.appendChild(sub);

    panels.forEach(function (p) {
      content.appendChild(p);      // 移动节点，页面原有监听随之保留
    });

    /* 原 .wrap 里若还有别的块（提示、脚注），一并搬过去，别丢东西 */
    qa('.wrap > *').forEach(function (el) {
      if (el === h1 || el === sub || el === tabs) return;
      if (el.tagName === 'SECTION') return;
      content.appendChild(el);
    });

    /* ---------- 导航：复用原分段按钮 ---------- */
    var btnCmp = q('#tabCompare');
    var btnDer = q('#tabDerive');
    if (tabs) tabs.style.display = 'none';   // 原分段条已由左侧导航替代

    var navItems = [];
    function addNav(label, btn, badgeText) {
      if (!btn) return;
      var b = document.createElement('button');
      b.className = 'nw-nav-item';
      b.type = 'button';
      b.innerHTML = '<span class="nw-nav-label">' + label + '</span>' +
                    (badgeText ? '<em class="nw-nav-badge">' + badgeText + '</em>' : '');
      b.addEventListener('click', function () {
        btn.click();
        setTimeout(sync, 30);
      });
      nav.appendChild(b);
      navItems.push({ el: b, btn: btn });
    }
    addNav('表格对比', btnCmp, '');
    addNav('派生表格生成', btnDer, '新');

    function sync() {
      var activeLabel = '表格对比';
      navItems.forEach(function (it) {
        var on = it.btn.classList.contains('active');
        it.el.classList.toggle('on', on);
        if (on) activeLabel = it.el.querySelector('.nw-nav-label').textContent;
      });
      titleEl.textContent = activeLabel;
      renderSteps();
    }

    /* ---------- 步骤：由面板标题自动生成 ---------- */
    function panelTitle(p) {
      var h2 = p.querySelector('h2');
      if (!h2) return '';
      var clone = h2.cloneNode(true);
      qa('.num, .muted, em, i', clone).forEach(function (x) { x.remove(); });
      return (clone.textContent || '').replace(/\s+/g, ' ').trim();
    }

    function visiblePanels() {
      return qa('#nwContent > section.panel').filter(function (p) {
        return p.offsetParent !== null && p.style.display !== 'none';
      });
    }

    function renderSteps() {
      var list = visiblePanels();
      steps.innerHTML = '';
      list.forEach(function (p, i) {
        var a = document.createElement('a');
        a.className = 'nw-step';
        a.href = 'javascript:void(0)';
        a.innerHTML = '<span class="nw-step-num">' + (i + 1) + '</span>' +
                      '<span class="nw-step-text">' + panelTitle(p) + '</span>';
        a.addEventListener('click', function () {
          content.scrollTo({ top: p.offsetTop - 12, behavior: 'smooth' });
        });
        steps.appendChild(a);
        p.__nwStep = a;
      });
      spy();
    }

    /* ---------- 滚动高亮 ---------- */
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) {
        if (en.isIntersecting) spy(en.target);
      });
    }, { root: content, rootMargin: '-10% 0px -70% 0px', threshold: 0 });

    function observePanels() {
      io.disconnect();
      visiblePanels().forEach(function (p) { io.observe(p); });
    }

    function spy(target) {
      var list = visiblePanels();
      var current = target;
      if (!current) {
        for (var i = list.length - 1; i >= 0; i--) {
          if (list[i].offsetTop - 40 <= content.scrollTop) { current = list[i]; break; }
        }
      }
      list.forEach(function (p) {
        if (p.__nwStep) p.__nwStep.classList.toggle('on', p === current);
      });
    }

    content.addEventListener('scroll', function () { spy(null); }, { passive: true });

    /* ---------- 宽屏两栏：录入整行 / 配置在左 / 预览与结果在右 ---------- */
    function layout() {
      var list = visiblePanels();
      var wide = window.innerWidth >= 1200;
      content.classList.toggle('nw-wide', wide);
      list.forEach(function (p, i) {
        if (!wide) {
          p.style.gridColumn = '';
          p.style.gridRow = '';
          return;
        }
        if (i === 0) { p.style.gridColumn = '1 / -1'; p.style.gridRow = '1'; }
        else if (i === 1) { p.style.gridColumn = '1'; p.style.gridRow = '2'; }
        else if (i === 2) { p.style.gridColumn = '2'; p.style.gridRow = '2'; }
        else if (i === 3) { p.style.gridColumn = '1 / -1'; p.style.gridRow = '3'; }
      });
    }

    var lastList = '';
    function refresh() {
      var key = visiblePanels().map(panelTitle).join('|');
      if (key !== lastList) {
        lastList = key;
        renderSteps();
        observePanels();
      }
      layout();
      sync();
    }

    // 页面自己会切换面板显隐：监听面板上 style/class 的变化
    var mo = new MutationObserver(function () {
      clearTimeout(mo.__t);
      mo.__t = setTimeout(refresh, 80);
    });
    panels.forEach(function (p) {
      mo.observe(p, { attributes: true, attributeFilter: ['style', 'class'] });
    });
    if (btnCmp) mo.observe(btnCmp, { attributes: true, attributeFilter: ['class'] });
    if (btnDer) mo.observe(btnDer, { attributes: true, attributeFilter: ['class'] });

    sync();
    renderSteps();
    observePanels();
    layout();
    window.addEventListener('resize', function () { layout(); });
    setTimeout(refresh, 200);

    return true;
  }

  if (!build()) {
    // 页面结构还没就绪时再试一次
    document.addEventListener('DOMContentLoaded', build);
    window.addEventListener('load', function () { build(); });
  }
})();
