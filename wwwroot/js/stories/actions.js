(function(window){
    // global actions menu manager
    let menuEl;

    function ensureMenu() {
        if (menuEl) return menuEl;
        menuEl = document.createElement('div');
        menuEl.id = 'global-actions-menu';
        menuEl.style.position = 'absolute';
        menuEl.style.minWidth = '220px';
        menuEl.style.zIndex = 20000;
        menuEl.className = 'card';
        menuEl.innerHTML = '<div class="card-body p-2"></div>';
        document.body.appendChild(menuEl);
        document.addEventListener('click', (e) => {
            if (!menuEl) return;
            if (!menuEl.contains(e.target) && !e.target.classList.contains('row-actions-btn')) {
                hideMenu();
            }
        });
        return menuEl;
    }

    function hideMenu() { if (menuEl) menuEl.style.display = 'none'; }

    function openMenuAt(x, y, actions, storyId) {
        const el = ensureMenu();
        const body = el.querySelector('.card-body');
        body.innerHTML = '';
        actions.forEach(a => {
            const btn = document.createElement(a.method === 'GET' ? 'a' : 'button');
            btn.className = 'btn btn-sm btn-link w-100 text-start';
            btn.textContent = a.title;
            btn.href = a.method === 'GET' ? a.url : '#';
            btn.addEventListener('click', function(ev){
                ev.preventDefault();
                if (a.confirm) {
                    if (!confirm('Confermi: ' + a.title + '?')) return;
                }
                if (a.method === 'GET') {
                    if (a.url) window.open(a.url, '_blank');
                    hideMenu();
                    return;
                }
                // POST action -> create form with antiforgery token
                const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
                const form = document.createElement('form');
                form.method = 'post';
                form.action = a.url;
                if (token) {
                    const inp = document.createElement('input'); inp.type = 'hidden'; inp.name = '__RequestVerificationToken'; inp.value = token; form.appendChild(inp);
                }
                // add story id if not present
                if (!a.url.includes('id=')) {
                    const i = document.createElement('input'); i.type='hidden'; i.name='id'; i.value=storyId; form.appendChild(i);
                }
                document.body.appendChild(form);
                form.submit();
            });
            body.appendChild(btn);
        });
        el.style.left = x + 'px';
        el.style.top = y + 'px';
        el.style.display = 'block';
    }

    window.StoriesApp = window.StoriesApp || {};
    window.StoriesApp.openActionsMenu = openMenuAt;
    window.StoriesApp.hideActionsMenu = hideMenu;

})(window);
