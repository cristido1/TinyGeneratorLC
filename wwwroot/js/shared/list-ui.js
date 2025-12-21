(function(){
    // Lightweight list UI initializer for pages following usage_index_standard
    function init(options){
        options = options || {};
        // sensible defaults but allow auto-detection for pages like Stories index
        let storageKey = options.storageKey || null;
        let colsMenuId = options.colsMenuId || null;
        const pagerId = options.pagerId || 'pager';

        // prefer explicit selector, otherwise try to find a nearby table/menu used by pages
        let table = null;
        if (options.tableSelector) table = document.querySelector(options.tableSelector);
        if (!table) table = document.querySelector('#storiesTable') || document.querySelector('.card table') || document.querySelector('table');

        // detect menu id if not provided
        if (!colsMenuId) {
            if (document.getElementById('colVisMenu')) colsMenuId = 'colVisMenu';
            else if (document.getElementById('ttsVoicesColsMenu')) colsMenuId = 'ttsVoicesColsMenu';
        }

        // derive a storage key if not given
        if (!storageKey) storageKey = (colsMenuId === 'colVisMenu') ? 'stories_cols_v1' : (colsMenuId === 'ttsVoicesColsMenu' ? 'ttsvoices_cols_v1' : 'list_cols_v1');

        const menu = colsMenuId ? document.getElementById(colsMenuId) : null;
        if (table && menu) buildColsMenu(table, menu, storageKey);

        // If a global Pager API exists, call it. Otherwise rely on pagination.js auto-init.
        if (window.Pager && typeof window.Pager.init === 'function'){
            try { window.Pager.init(pagerId); } catch(e) { console.warn('Pager.init failed', e); }
        }
    }

    function buildColsMenu(table, menu, storageKey){
        const ths = Array.from(table.querySelectorAll('th[data-col]'));
        if (ths.length===0) return;
        menu.innerHTML = '';
        const saved = (()=>{ try{ return JSON.parse(localStorage.getItem(storageKey)||'null'); }catch{ return null; }})();
        ths.forEach(th=>{
            const col = th.getAttribute('data-col');
            const label = th.textContent.trim();
            const checked = saved ? (saved[col] !== false) : true;
            const li = document.createElement('li'); li.className='px-2';
            li.innerHTML = `<div class="form-check"><input class="form-check-input" type="checkbox" id="colvis_${col}" data-col="${col}" ${checked? 'checked':''} /> <label class="form-check-label" for="colvis_${col}">${label}</label></div>`;
            menu.appendChild(li);
            setColVisible(table, col, checked);
        });
        menu.addEventListener('change', e=>{
            const cb = e.target.closest('input[type=checkbox]'); if(!cb) return; const col = cb.dataset.col; const vis = cb.checked; setColVisible(table,col,vis); persist();
        });
        function persist(){ const obj={}; menu.querySelectorAll('input[data-col]').forEach(i=> obj[i.dataset.col]=i.checked); try{ localStorage.setItem(storageKey, JSON.stringify(obj)); }catch{} }
    }
    function setColVisible(table, col, visible){
        const th = table.querySelector(`th[data-col="${col}"]`);
        if (th) th.style.display = visible? '': 'none';
        const idx = Array.from(table.querySelectorAll('th')).indexOf(th);
        if (idx>=0){ Array.from(table.querySelectorAll('tbody tr')).forEach(tr=>{ const td = tr.children[idx]; if(td) td.style.display = visible? '':'none'; }); }
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ()=>init()); else init();
    window.ListUI = { init };
})();
