// Common AG Grid utilities with column visibility persistence
window.AgGridHelper = {
    // Initialize AG Grid with standard options
    init: function(containerId, columnDefs, rowData, options = {}) {
        const storageKey = options.storageKey || `agGrid_${containerId}_colVisibility`;
        const eGridDiv = document.querySelector('#' + containerId);
        if (!eGridDiv) {
            console.error('AG Grid container not found:', containerId);
            return null;
        }

        // Load saved column visibility
        const savedVisibility = this.loadColVisibility(storageKey);
        if (savedVisibility) {
            columnDefs.forEach(col => {
                const field = col.colId || col.field;
                if (field && savedVisibility[field] !== undefined && field !== 'actions') {
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
            pagination: options.pagination !== false,
            paginationPageSize: options.pageSize || 25,
            rowSelection: 'single',
            suppressRowClickSelection: true,
            onGridReady: params => {
                window[containerId + 'Api'] = params.api;
                this.buildColVisMenu(containerId, params.api, storageKey);
                if (options.onGridReady) options.onGridReady(params);
            },
            onColumnVisible: () => {
                const api = window[containerId + 'Api'];
                if (api) {
                    const state = {};
                    api.getAllGridColumns().forEach(col => {
                        const id = col.getColId();
                        if (id !== 'actions') {
                            state[id] = col.isVisible();
                        }
                    });
                    this.saveColVisibility(storageKey, state);
                    this.buildColVisMenu(containerId, api, storageKey);
                }
            },
            ...options.gridOptions
        };

        // Initialize grid
        if (typeof agGrid !== 'undefined' && typeof agGrid.createGrid === 'function') {
            return agGrid.createGrid(eGridDiv, gridOptions);
        } else {
            console.error('AG Grid not found');
            return null;
        }
    },

    loadColVisibility: function(key) {
        try {
            const saved = localStorage.getItem(key);
            return saved ? JSON.parse(saved) : null;
        } catch { return null; }
    },

    saveColVisibility: function(key, state) {
        try {
            localStorage.setItem(key, JSON.stringify(state));
        } catch {}
    },

    buildColVisMenu: function(containerId, api, storageKey) {
        const menu = document.getElementById(containerId + 'ColVisMenu');
        if (!menu || !api) return;

        const cols = api.getAllGridColumns().filter(c => c.getColId() !== 'actions');
        menu.innerHTML = cols.map(col => {
            const id = col.getColId();
            const name = col.getColDef().headerName || id;
            const checked = col.isVisible() ? 'checked' : '';
            return `<li><label class="dropdown-item d-flex align-items-center gap-2" style="cursor:pointer;">
                <input type="checkbox" class="form-check-input m-0" data-col="${id}" ${checked}>
                <span>${name}</span>
            </label></li>`;
        }).join('');

        menu.innerHTML += `<li><hr class="dropdown-divider"></li>
            <li><a class="dropdown-item text-primary" href="#" data-action="showAll">Mostra tutte</a></li>
            <li><a class="dropdown-item text-secondary" href="#" data-action="reset">Reset default</a></li>`;

        // Add event listeners
        menu.onclick = (e) => {
            if (e.target.matches('input[data-col]')) {
                const colId = e.target.dataset.col;
                api.setColumnsVisible([colId], e.target.checked);
            }
            if (e.target.dataset.action === 'showAll') {
                e.preventDefault();
                const allCols = api.getAllGridColumns().filter(c => c.getColId() !== 'actions').map(c => c.getColId());
                api.setColumnsVisible(allCols, true);
            }
            if (e.target.dataset.action === 'reset') {
                e.preventDefault();
                localStorage.removeItem(storageKey);
                location.reload();
            }
        };
    },

    // Action buttons cell renderer
    actionsCellRenderer: function(params, actions) {
        const id = params.data.Id || params.data.id || params.value;
        return actions.map(a => {
            if (a.type === 'link') {
                return `<a class="btn btn-sm btn-outline-${a.color || 'primary'} me-1" href="${a.url}?id=${encodeURIComponent(id)}">${a.label}</a>`;
            }
            if (a.type === 'button') {
                return `<button class="btn btn-sm btn-outline-${a.color || 'secondary'} me-1" data-action="${a.action}" data-id="${id}">${a.label}</button>`;
            }
            return '';
        }).join('');
    }
};
