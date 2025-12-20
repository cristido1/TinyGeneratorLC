(function(window){
    function initGrid() {
        const gridDiv = document.querySelector('#storiesGrid');
        if (!gridDiv) return;

        fetch(window.location.pathname + '?handler=Data')
            .then(r => r.json())
            .then(payload => {
                window.__stories_payload = payload;
                const stories = payload.stories || [];

                const columnDefs = [
                    { field: 'Id', headerName: 'Id', width: 70 },
                    { field: 'Timestamp', headerName: 'Timestamp', width: 130 },
                    { field: 'Prompt', headerName: 'Prompt', flex: 2, tooltipField: 'FullPrompt' },
                    { field: 'StatusDescription', headerName: 'Status', width: 120, valueGetter: params => params.data.StatusDescription },
                    { field: 'Model', headerName: 'Modello', width: 180 },
                    { field: 'Folder', headerName: 'Cartella', width: 140 },
                    { headerName: 'Assets', field: 'Assets', width: 140, sortable: false, filter: false, cellRenderer: params => buildAssetsIcons(params.data) },
                    { field: 'CharCount', headerName: 'Caratteri', width: 100, cellClass: 'text-end' },
                    { field: 'EvalScore', headerName: 'Valutazione', width: 100 },
                    { field: 'Approved', headerName: 'Approved', width: 90, valueGetter: p => p.data.Approved ? '✅' : '❌' },
                    { headerName: '', width: 100, sortable: false, filter: false, suppressMenu: true, cellRenderer: p => renderActionsButton(p.data) }
                ];

                const gridOptions = {
                    columnDefs,
                    rowData: stories,
                    defaultColDef: { sortable: true, filter: true, resizable: true },
                    rowSelection: 'single',
                    animateRows: true,
                    pagination: true,
                    paginationPageSize: 25,
                    getRowId: params => String(params.data.Id),
                    onFirstDataRendered: params => params.api.sizeColumnsToFit(),
                    suppressRowClickSelection: true,
                    rowHeight: 48,
                    fullWidthCellRenderer: params => {
                        // not used
                        return '';
                    }
                };

                agGrid.createGrid(gridDiv, gridOptions);

                // Quick filter wiring
                const q = document.getElementById('quickFilter');
                if (q) q.addEventListener('input', e => gridDiv.__agGrid.api.setQuickFilter(e.target.value));
            })
            .catch(err => console.error('Failed to load stories data', err));
    }

    function buildAssetsIcons(data) {
        if (!data) return '-';
        let icons = '';
        if (data.GeneratedTtsJson) icons += `<span title="TTS JSON" class="text-primary me-2"><i class="bi bi-file-earmark-text-fill"></i></span>`;
        if (data.GeneratedTts) icons += `<span title="TTS audio" class="text-success me-2"><i class="bi bi-volume-up-fill"></i></span>`;
        if (data.GeneratedAmbient) icons += `<span title="Ambience" class="text-info me-2"><i class="bi bi-cloud-fill"></i></span>`;
        if (data.GeneratedEffects) icons += `<span title="FX" class="text-warning me-2"><i class="bi bi-lightning-charge-fill"></i></span>`;
        if (data.GeneratedMusic) icons += `<span title="Music" class="text-muted me-2"><i class="bi bi-music-note-beamed"></i></span>`;
        if (data.GeneratedMixedAudio) icons += `<span title="Final mix" class="text-danger me-2"><i class="bi bi-play-circle-fill"></i></span>`;
        return icons || '<span class="text-muted">-</span>';
    }

    function renderActionsButton(data) {
        const id = data.Id;
        return `<button class="btn btn-sm btn-outline-primary row-actions-btn" data-story-id="${id}">Azioni</button>`;
    }

    // expose
    window.StoriesApp = window.StoriesApp || {};
    window.StoriesApp.initGrid = initGrid;
    window.StoriesApp.buildAssetsIcons = buildAssetsIcons;

})(window);
