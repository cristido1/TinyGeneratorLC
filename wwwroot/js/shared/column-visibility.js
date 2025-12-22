(function(){
    function init() {
        const menu = document.getElementById('colVisMenu');
        if (!menu) return;
        const table = document.getElementById('storiesTable');
        if (!table) return;
        const ths = Array.from(table.querySelectorAll('th[data-col]'));
        menu.innerHTML = '';
        const key = 'stories_cols_v1';
        const saved = (() => { try { return JSON.parse(localStorage.getItem(key) || 'null'); } catch { return null; }})();
        ths.forEach(th => {
            const col = th.getAttribute('data-col');
            const label = th.textContent.trim();
            const checked = saved ? (saved[col] !== false) : true;
            const div = document.createElement('div'); div.className='form-check';
            div.innerHTML = `<input class="form-check-input col-vis-check" type="checkbox" data-col="${col}" id="colvis_${col}" ${checked? 'checked':''}><label class="form-check-label" for="colvis_${col}">${label}</label>`;
            menu.appendChild(div);
            toggleCol(col, checked);
        });
        menu.querySelectorAll('.col-vis-check').forEach(cb => cb.addEventListener('change', function(){
            const col = this.dataset.col; toggleCol(col, this.checked); save();
        }));
        function toggleCol(col, show){
            const th = table.querySelector(`th[data-col="${col}"]`);
            const tds = table.querySelectorAll(`td[data-col="${col}"]`);
            if (th) th.style.display = show ? '' : 'none';
            tds.forEach(td => td.style.display = show ? '' : 'none');
        }
        function save(){
            const obj = {};
            menu.querySelectorAll('.col-vis-check').forEach(cb => obj[cb.dataset.col]=cb.checked);
            try { localStorage.setItem(key, JSON.stringify(obj)); } catch {}
        }
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
