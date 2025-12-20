(function(window){
    // simple loader for component scripts then initialize
    const scripts = ['/js/stories/grid.js','/js/stories/actions.js','/js/stories/evaluations.js','/js/stories/tts.js'];
    function loadNext(i){
        if (i >= scripts.length) { init(); return; }
        const s = document.createElement('script'); s.src = scripts[i]; s.onload = () => loadNext(i+1); document.head.appendChild(s);
    }

    function init(){
        // init components
        if (window.StoriesApp && window.StoriesApp.initGrid) window.StoriesApp.initGrid();
        if (window.StoriesApp && window.StoriesApp.initEvaluations) window.StoriesApp.initEvaluations();
        if (window.StoriesApp && window.StoriesApp.initTts) window.StoriesApp.initTts();

        // Global actions button handler (delegated)
        document.addEventListener('click', function(e){
            const btn = e.target.closest('.row-actions-btn');
            if (!btn) return;
            const storyId = btn.dataset.storyId;
            const payload = window.__stories_payload;
            if (!payload) return;
            const story = (payload.stories || []).find(s => String(s.Id) === String(storyId));
            if (!story) return;
            // position menu near button
            const rect = btn.getBoundingClientRect();
            const x = rect.right + window.scrollX - 8;
            const y = rect.top + window.scrollY + 8;
            // open backend-provided actions
            const actions = story.Actions || [];
            window.StoriesApp.openActionsMenu(x, y, actions, story.Id);
        });

        // ensure only one detail row open: simple toggle on double-click
        document.addEventListener('dblclick', function(e){
            const row = e.target.closest('.ag-row');
            if (!row) return;
            const gridDiv = document.getElementById('storiesGrid');
            const api = gridDiv && gridDiv.__agGrid && gridDiv.__agGrid.api;
            if (!api) return;
            const node = api.getRowNode(row.getAttribute('row-id'));
            if (!node) return;
            // collapse all
            api.forEachNode(n => n.setExpanded(false));
            node.setExpanded(true);
        });
    }

    document.addEventListener('DOMContentLoaded', function(){ loadNext(0); });

})(window);
