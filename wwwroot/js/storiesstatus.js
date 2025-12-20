// AG Grid initialization for StoriesStatus page
document.addEventListener('DOMContentLoaded', () => {
    const STORAGE_KEY = 'storiesStatus_colVisibility';
    const eGridDiv = document.querySelector('#storiesGrid');
    if (!eGridDiv) {
        console.error('StoriesStatus: grid container #storiesGrid not found');
        return;
    }
    
    // Read initial data serialized by Razor
    const rowData = window.__stories_initial_data || [];
    console.log('StoriesStatus: initializing grid with', rowData.length, 'rows');

    // Load saved column visibility
    function loadColVisibility() {
        try {
            const saved = localStorage.getItem(STORAGE_KEY);
            return saved ? JSON.parse(saved) : null;
        } catch { return null; }
    }
    
    function saveColVisibility(state) {
        try {
            localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
        } catch {}
    }
    
    const savedVisibility = loadColVisibility();

    const columnDefs = [
        { headerName: 'ID', field: 'Id', width: 90 },
        { headerName: 'Code', field: 'Code', flex: 1 },
        { headerName: 'Description', field: 'Description', flex: 2 },
        { headerName: 'Step', field: 'Step', width: 100 },
        { headerName: 'Color', field: 'Color', width: 140 },
        { headerName: 'Operation Type', field: 'OperationType', width: 160 },
        { headerName: 'Agent Type', field: 'AgentType', width: 140 },
        { headerName: 'Function Name', field: 'FunctionName', width: 180 },
        { headerName: 'Caption', field: 'CaptionToExecute', flex: 1 },
        {
            headerName: 'Actions', field: 'Id', colId: 'actions', width: 180, 
            suppressColumnsToolPanel: true,
            cellRenderer: function(params) {
                const id = params.value;
                const editUrl = './Edit?id=' + encodeURIComponent(id);
                const deleteUrl = './Delete?id=' + encodeURIComponent(id);
                return `<a class="btn btn-sm btn-outline-primary me-1" href="${editUrl}">Edit</a>` +
                       `<a class="btn btn-sm btn-outline-danger me-1" href="${deleteUrl}">Delete</a>` +
                       `<button class="btn btn-sm btn-outline-secondary btn-details" data-id="${id}">Details</button>`;
            }
        }
    ];
    
    // Apply saved visibility
    if (savedVisibility) {
        columnDefs.forEach(col => {
            const field = col.colId || col.field;
            if (field && savedVisibility[field] !== undefined) {
                col.hide = !savedVisibility[field];
            }
        });
    }

    const gridOptions = {
        columnDefs: columnDefs,
        defaultColDef: {
            sortable: true,
            filter: true,
            resizable: true,
            minWidth: 80
        },
        rowData: rowData,
        pagination: true,
        paginationPageSize: 10,
        rowSelection: 'single',
        suppressRowClickSelection: true,
        onGridReady: params => {
            window.storiesGridApi = params.api;
            buildColVisMenu();
            console.log('StoriesStatus: grid ready');
        },
        onColumnVisible: () => {
            // Save visibility state when columns change
            const state = {};
            window.storiesGridApi.getAllGridColumns().forEach(col => {
                const id = col.getColId();
                if (id !== 'actions') {
                    state[id] = col.isVisible();
                }
            });
            saveColVisibility(state);
            buildColVisMenu();
        }
    };
    
    // Build column visibility dropdown menu
    function buildColVisMenu() {
        const menu = document.getElementById('colVisMenu');
        if (!menu || !window.storiesGridApi) return;
        
        const cols = window.storiesGridApi.getAllGridColumns().filter(c => c.getColId() !== 'actions');
        menu.innerHTML = cols.map(col => {
            const id = col.getColId();
            const name = col.getColDef().headerName || id;
            const checked = col.isVisible() ? 'checked' : '';
            return `<li><label class="dropdown-item d-flex align-items-center gap-2" style="cursor:pointer;">
                <input type="checkbox" class="form-check-input m-0" data-col="${id}" ${checked}>
                <span>${name}</span>
            </label></li>`;
        }).join('');
        
        // Add show all / hide all
        menu.innerHTML += `<li><hr class="dropdown-divider"></li>
            <li><a class="dropdown-item text-primary" href="#" id="colVisShowAll">Mostra tutte</a></li>
            <li><a class="dropdown-item text-secondary" href="#" id="colVisReset">Reset default</a></li>`;
    }

    // Initialize AG Grid - v31 uses agGrid.createGrid
    try {
        if (typeof agGrid !== 'undefined' && typeof agGrid.createGrid === 'function') {
            console.log('StoriesStatus: using agGrid.createGrid');
            window.storiesGridInstance = agGrid.createGrid(eGridDiv, gridOptions);
        } else if (typeof agGrid !== 'undefined' && typeof agGrid.Grid === 'function') {
            console.log('StoriesStatus: using new agGrid.Grid');
            new agGrid.Grid(eGridDiv, gridOptions);
        } else {
            console.error('AG Grid not found or not recognized. typeof agGrid:', typeof agGrid);
            return;
        }
    }
    catch (err) {
        console.error('Failed to initialize AG Grid:', err);
    }

    // Toolbar bindings
    document.getElementById('globalFilter')?.addEventListener('input', (e) => {
        window.storiesGridApi.setQuickFilter(e.target.value);
    });
    document.getElementById('refreshBtn')?.addEventListener('click', () => location.reload());

    // Column visibility menu handlers (delegated)
    document.getElementById('colVisMenu')?.addEventListener('change', (e) => {
        if (e.target.matches('input[data-col]')) {
            const colId = e.target.dataset.col;
            const visible = e.target.checked;
            window.storiesGridApi.setColumnsVisible([colId], visible);
        }
    });
    
    document.getElementById('colVisMenu')?.addEventListener('click', (e) => {
        if (e.target.id === 'colVisShowAll') {
            e.preventDefault();
            const cols = window.storiesGridApi.getAllGridColumns().filter(c => c.getColId() !== 'actions').map(c => c.getColId());
            window.storiesGridApi.setColumnsVisible(cols, true);
        }
        if (e.target.id === 'colVisReset') {
            e.preventDefault();
            localStorage.removeItem(STORAGE_KEY);
            location.reload();
        }
    });

    // Delegate click handler for details buttons (uses Bootstrap modal present in page)
    eGridDiv.addEventListener('click', function(e) {
        const btn = e.target.closest('.btn-details');
        if (!btn) return;
        const id = btn.getAttribute('data-id');
        if (!id) return;
        let data = null;
        // Try fast lookup by forEachNode
        window.storiesGridApi.forEachNode(n => { if (n.data && n.data.Id && n.data.Id.toString() === id.toString()) data = n.data; });
        if (!data) return;
        const modal = document.getElementById('statusDetailsModal');
        if (!modal) return;
        modal.querySelector('.modal-body').innerHTML = `
            <p><strong>Code:</strong> ${escapeHtml(data.Code ?? '')}</p>
            <p><strong>Description:</strong> ${escapeHtml(data.Description ?? '')}</p>
            <p><strong>Step:</strong> ${escapeHtml(data.Step ?? '')}</p>
            <p><strong>Color:</strong> ${escapeHtml(data.Color ?? '')}</p>
            <p><strong>Operation Type:</strong> ${escapeHtml(data.OperationType ?? '')}</p>
            <p><strong>Agent Type:</strong> ${escapeHtml(data.AgentType ?? '')}</p>
            <p><strong>Function Name:</strong> ${escapeHtml(data.FunctionName ?? '')}</p>
            <p><strong>Caption:</strong> ${escapeHtml(data.CaptionToExecute ?? '')}</p>
        `;
        const bsModal = new bootstrap.Modal(modal);
        bsModal.show();
    });

    function escapeHtml(unsafe) {
        if (!unsafe) return '';
        return String(unsafe).replace(/[&<>"'`]/g, function (s) {
            return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;', '`': '&#96;' })[s];
        });
    }

});
