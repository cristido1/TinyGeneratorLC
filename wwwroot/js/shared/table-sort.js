// table-sort.js
// Generic small helper to make <th data-sort="name"> clickable and navigate with orderBy query
(function(window){
    window.TableSort = {
        init: function(opts){
            opts = opts || {};
            var selector = opts.selector || 'th.sortable';
            var currentOrder = opts.currentOrder || null;
            document.querySelectorAll(selector).forEach(function(th){
                th.style.cursor = 'pointer';
                th.addEventListener('click', function(){
                        var key = th.getAttribute('data-sort');
                        if (!key) return;
                        var url = new URL(window.location.href);
                        var current = url.searchParams.get('orderBy');
                        var currentDir = url.searchParams.get('orderDir') || 'asc';
                        var nextDir = 'asc';
                        if (current === key) {
                            nextDir = (currentDir === 'asc') ? 'desc' : 'asc';
                        } else {
                            nextDir = 'asc';
                        }
                        url.searchParams.set('orderBy', key);
                        url.searchParams.set('orderDir', nextDir);
                        url.searchParams.set('page', '1');
                        window.location.href = url.toString();
                    });
            });

            // Update visual indicators based on current query params
            try {
                var url = new URL(window.location.href);
                var current = url.searchParams.get('orderBy');
                var currentDir = url.searchParams.get('orderDir') || 'asc';
                if (current) {
                    var th = document.querySelector(selector + '[data-sort="' + current + '"]');
                    if (th) {
                        var ind = th.querySelector('.sort-indicator');
                        if (ind) {
                            // clear any previous icon
                            ind.innerHTML = '';
                            var i = document.createElement('i');
                            // use bootstrap icons caret
                            if (currentDir === 'asc') {
                                i.className = 'bi bi-caret-up-fill text-muted';
                            } else {
                                i.className = 'bi bi-caret-down-fill text-muted';
                            }
                            ind.appendChild(i);
                        }
                        // also add aria-sort
                        th.setAttribute('aria-sort', currentDir === 'asc' ? 'ascending' : 'descending');
                    }
                }
            } catch (e) { /* ignore */ }
        }
    };
})(window);
