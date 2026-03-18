// Import Vue core from existing bundle
import {
    h,
    l as ref,
    p as onMounted,
    z as createApp
} from '../vue-roles/chunks/primeicons-CDhzlABi.js';

// Import PrimeVue components and plugins from existing bundle
import {
    j as Button,
    n as Column,
    o as DataTable,
    P as PrimeVue,
    Q as Aura,
    w as ConfirmationService
} from '../vue-roles/chunks/index-pttFU7Tr.js';

// ---- Utility helpers ----

function decodeEscapes(s) {
    if (!s) return '';
    let out = s.replace(/\\r\\n/g, '\n').replace(/\\n/g, '\n').replace(/\\r/g, '\n').replace(/\\t/g, '\t');
    out = out.replace(/\\u([0-9a-fA-F]{4})/g, (_, g) => String.fromCharCode(parseInt(g, 16)));
    out = out.replace(/\\x([0-9a-fA-F]{2})/g, (_, g) => String.fromCharCode(parseInt(g, 16)));
    return out;
}

// ---- App component using setup() returning a render function ----

const LogsApp = {
    setup() {
        const rows = ref([]);
        const loading = ref(false);
        const total = ref(0);
        const currentPage = ref(0);
        const pageSize = ref(25);
        const sortField = ref('timestamp');
        const sortOrder = ref(-1);
        const onlyModel = ref(true);
        const onlyResult = ref(false);
        const selectedThreadId = ref(null);
        const expandedRows = ref({});
        const expandedContent = ref({});
        const statusMsg = ref('');
        const statusType = ref('info');

        async function loadData() {
            loading.value = true;
            try {
                const params = new URLSearchParams({
                    start: currentPage.value * pageSize.value,
                    length: pageSize.value,
                    sortBy: sortField.value || 'timestamp',
                    sortDesc: sortOrder.value === -1 ? 'true' : 'false',
                    onlyModel: onlyModel.value ? 'true' : 'false',
                    onlyResult: onlyResult.value ? 'true' : 'false',
                });

                const res = await fetch('/api/logs/paged?' + params);
                if (!res.ok) throw new Error('HTTP ' + res.status);
                const data = await res.json();
                rows.value = data.items || [];
                total.value = data.total || 0;
            } catch (e) {
                showStatus('Errore caricamento dati: ' + e.message, 'error');
            } finally {
                loading.value = false;
            }
        }

        function showStatus(msg, type = 'info') {
            statusMsg.value = msg;
            statusType.value = type;
            setTimeout(() => { statusMsg.value = ''; }, 5000);
        }

        function handlePage(ev) {
            currentPage.value = ev.page;
            pageSize.value = ev.rows;
            loadData();
        }

        function handleSort(ev) {
            sortField.value = ev.sortField;
            sortOrder.value = ev.sortOrder;
            currentPage.value = 0;
            loadData();
        }

        function getRowClass(data) {
            const src = String(data.source || '');
            let cls = '';
            if (src === 'ModelRequest' || src === 'ModelPrompt') cls = 'log-model-request';
            else if (src === 'ModelResponse' || src === 'ModelCompletion') cls = 'log-model-response';
            if (src.toLowerCase().indexOf('responsechecker') >= 0) {
                cls = cls ? (cls + ' log-response-checker') : 'log-response-checker';
            }
            return cls;
        }

        async function fetchAndCacheMessage(data) {
            const id = data.id;
            if (expandedContent.value[id] !== undefined) return;
            // Mark as loading
            expandedContent.value = { ...expandedContent.value, [id]: null };
            try {
                const res = await fetch('/api/logs/message/' + id);
                if (!res.ok) throw new Error('HTTP ' + res.status);
                const json = await res.json();
                expandedContent.value = { ...expandedContent.value, [id]: json.message || '' };
            } catch (e) {
                expandedContent.value = { ...expandedContent.value, [id]: 'Impossibile caricare il messaggio' };
            }
        }

        function handleRowExpand(ev) {
            fetchAndCacheMessage(ev.data);
        }

        function selectThread(data) {
            const tid = data.threadIdRaw;
            if (!tid || tid === 0) return;
            selectedThreadId.value = selectedThreadId.value === tid ? null : tid;
        }

        async function analyzeThread(data) {
            try {
                const res = await fetch('/api/logs/analyze', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ threadId: String(data.threadIdRaw), threadScope: data.threadScope })
                });
                const json = await res.json();
                showStatus(json.message || 'Analisi avviata', 'success');
            } catch (e) {
                showStatus('Errore analisi: ' + e.message, 'error');
            }
        }

        async function analyzeMissing() {
            try {
                const res = await fetch('/api/logs/analyze-missing', { method: 'POST' });
                const json = await res.json();
                showStatus(json.message || 'Analisi avviata', 'success');
            } catch (e) {
                showStatus('Errore analisi: ' + e.message, 'error');
            }
        }

        async function clearAllLogs() {
            if (!confirm('Sei sicuro di voler cancellare TUTTI i log e le relative analisi? Questa operazione è irreversibile.')) return;
            loading.value = true;
            try {
                const res = await fetch('/api/logs/clear', { method: 'POST' });
                const json = await res.json();
                if (!res.ok) {
                    showStatus(json.message || json.statsError || 'Errore cancellazione', 'error');
                } else {
                    showStatus(json.message || 'Log cancellati', 'success');
                    currentPage.value = 0;
                    selectedThreadId.value = null;
                    expandedRows.value = {};
                    expandedContent.value = {};
                    await loadData();
                }
            } catch (e) {
                showStatus('Errore cancellazione: ' + e.message, 'error');
            } finally {
                loading.value = false;
            }
        }

        function exportSelected() {
            if (!selectedThreadId.value) return;
            window.location.href = '/api/logs/export?threadId=' + selectedThreadId.value;
        }

        function exportAll() {
            window.location.href = '/api/logs/export';
        }

        onMounted(() => loadData());

        // ---- Render function (closure over reactive state) ----
        return () => {
            // Source badge
            function renderSource(source) {
                const src = String(source || '');
                if (src === 'ModelResponse' || src === 'ModelCompletion') {
                    return h('span', { class: 'badge bg-warning text-dark' }, [
                        h('i', { class: 'bi bi-chevron-left me-1' }),
                        'Response'
                    ]);
                }
                if (src === 'ModelRequest' || src === 'ModelPrompt') {
                    return h('span', { class: 'badge bg-success' }, [
                        'Request ',
                        h('i', { class: 'bi bi-chevron-right' })
                    ]);
                }
                return h('span', {}, src || '-');
            }

            // Result badge
            function renderResult(result) {
                const res = String(result || '').trim().toUpperCase();
                if (!res) return h('span', { class: 'text-muted' }, '-');
                const badge =
                    res === 'SUCCESS' ? 'bg-success' :
                    res === 'FAILED' ? 'bg-warning text-dark' :
                    res === 'ERROR' ? 'bg-danger' : 'bg-secondary';
                return h('span', { class: 'badge ' + badge }, res);
            }

            // Status message
            const statusEl = statusMsg.value
                ? h('div', {
                    class: 'alert py-2 mb-2 ' + (statusType.value === 'error' ? 'alert-danger' : statusType.value === 'success' ? 'alert-success' : 'alert-info')
                  }, statusMsg.value)
                : null;

            // Selected thread info
            const selectionEl = selectedThreadId.value
                ? h('div', { class: 'alert alert-info py-1 px-2 mb-2 d-flex align-items-center gap-2' }, [
                    'Selezionato ThreadId ',
                    h('strong', {}, String(selectedThreadId.value)),
                    h(Button, {
                        label: 'Deseleziona',
                        size: 'small',
                        severity: 'secondary',
                        outlined: true,
                        onClick: () => { selectedThreadId.value = null; }
                    })
                  ])
                : null;

            // Toolbar
            const toolbar = h('div', { class: 'logs-toolbar mb-2' }, [
                h(Button, {
                    label: 'Log Chat',
                    icon: 'pi pi-comments',
                    severity: 'secondary',
                    outlined: true,
                    size: 'small',
                    onClick: () => { window.location.href = '/Logs/LogChat'; }
                }),
                h(Button, {
                    label: 'Export Selected',
                    icon: 'pi pi-download',
                    severity: 'secondary',
                    outlined: true,
                    size: 'small',
                    disabled: !selectedThreadId.value,
                    onClick: exportSelected
                }),
                h(Button, {
                    label: 'Export All',
                    icon: 'pi pi-file-export',
                    severity: 'secondary',
                    outlined: true,
                    size: 'small',
                    onClick: exportAll
                }),
                h(Button, {
                    label: 'Clear All',
                    icon: 'pi pi-trash',
                    severity: 'danger',
                    outlined: true,
                    size: 'small',
                    onClick: clearAllLogs
                }),
                h(Button, {
                    label: 'Analyze',
                    icon: 'pi pi-cog',
                    severity: 'warning',
                    outlined: true,
                    size: 'small',
                    onClick: analyzeMissing
                }),
            ]);

            // Filters
            const filters = h('div', { class: 'logs-filters mb-2 d-flex gap-3 align-items-center flex-wrap' }, [
                h('div', { class: 'form-check' }, [
                    h('input', {
                        class: 'form-check-input',
                        type: 'checkbox',
                        id: 'onlyResultCheck',
                        checked: onlyResult.value,
                        onChange: (e) => { onlyResult.value = e.target.checked; currentPage.value = 0; loadData(); }
                    }),
                    h('label', { class: 'form-check-label', for: 'onlyResultCheck' }, 'Solo log con result')
                ]),
                h('div', { class: 'form-check' }, [
                    h('input', {
                        class: 'form-check-input',
                        type: 'checkbox',
                        id: 'onlyModelCheck',
                        checked: onlyModel.value,
                        onChange: (e) => { onlyModel.value = e.target.checked; currentPage.value = 0; loadData(); }
                    }),
                    h('label', { class: 'form-check-label', for: 'onlyModelCheck' }, 'Solo request/response')
                ]),
            ]);

            // DataTable
            const table = h(DataTable, {
                value: rows.value,
                lazy: true,
                paginator: true,
                rows: pageSize.value,
                totalRecords: total.value,
                loading: loading.value,
                sortField: sortField.value,
                sortOrder: sortOrder.value,
                onPage: handlePage,
                onSort: handleSort,
                expandedRows: expandedRows.value,
                'onUpdate:expandedRows': (v) => { expandedRows.value = v; },
                onRowExpand: handleRowExpand,
                rowClass: getRowClass,
                dataKey: 'id',
                size: 'small',
                scrollable: true,
                rowsPerPageOptions: [10, 25, 50, 100],
                paginatorTemplate: 'FirstPageLink PrevPageLink PageLinks NextPageLink LastPageLink RowsPerPageDropdown CurrentPageReport',
                currentPageReportTemplate: '{first} - {last} di {totalRecords}',
                class: 'logs-datatable',
                onRowClick: (ev) => {
                    const t = ev.originalEvent?.target;
                    if (t && typeof t.closest === 'function' && t.closest('button, a, .p-row-toggler')) return;
                    selectThread(ev.data);
                }
            }, {
                default: () => [
                    // Expander column
                    h(Column, { expander: true, style: 'width: 3.5rem; text-align: center;' }),

                    h(Column, { field: 'timestamp', header: 'Timestamp', sortable: true, style: 'min-width: 10rem' }),
                    h(Column, { field: 'level', header: 'Level', sortable: true, style: 'min-width: 5rem' }),

                    h(Column, {
                        field: 'source',
                        header: 'Source',
                        sortable: true,
                        style: 'min-width: 7rem'
                    }, {
                        body: (slotProps) => renderSource(slotProps.data.source)
                    }),

                    h(Column, {
                        field: 'result',
                        header: 'Result',
                        sortable: true,
                        style: 'min-width: 6rem'
                    }, {
                        body: (slotProps) => renderResult(slotProps.data.result)
                    }),

                    h(Column, { field: 'failReason', header: 'Fail Reason', style: 'min-width: 8rem' }),
                    h(Column, { field: 'operation', header: 'Operazione', style: 'min-width: 8rem' }),
                    h(Column, { field: 'duration', header: 'Durata (s)', sortable: true, style: 'min-width: 6rem' }),
                    h(Column, { field: 'threadId', header: 'ThreadId', sortable: true, style: 'min-width: 5rem' }),
                    h(Column, { field: 'storyId', header: 'StoryId', sortable: true, style: 'min-width: 5rem' }),
                    h(Column, { field: 'agent', header: 'Agent', sortable: true, style: 'min-width: 7rem' }),
                    h(Column, { field: 'model', header: 'Model', sortable: true, style: 'min-width: 7rem' }),

                    h(Column, {
                        header: 'Azioni',
                        style: 'min-width: 10rem'
                    }, {
                        body: (slotProps) => {
                            const data = slotProps.data;
                            const nodes = [
                                h(Button, {
                                    label: 'Analizza',
                                    size: 'small',
                                    severity: 'secondary',
                                    outlined: true,
                                    class: 'me-1',
                                    onClick: (e) => { e.stopPropagation(); analyzeThread(data); }
                                })
                            ];
                            if (data.analized && data.threadIdRaw && data.threadIdRaw > 0) {
                                nodes.push(
                                    h('a', {
                                        class: 'btn btn-sm btn-outline-primary',
                                        href: '/Logs/Analysis?threadId=' + encodeURIComponent(data.threadIdRaw),
                                        onClick: (e) => e.stopPropagation()
                                    }, 'Vedi analisi')
                                );
                            }
                            return h('div', { class: 'd-flex align-items-center gap-1 flex-wrap' }, nodes);
                        }
                    }),
                ],

                // Row expansion: lazy-loaded message (no innerHTML - safe text rendering)
                expansion: (slotProps) => {
                    const id = slotProps.data.id;
                    const content = expandedContent.value[id];
                    if (content === undefined) {
                        fetchAndCacheMessage(slotProps.data);
                    }
                    const text = (content === undefined || content === null)
                        ? 'Caricamento...'
                        : decodeEscapes(String(content));

                    // Split on "content" keyword and render as text nodes + highlighted spans
                    const parts = text.split(/(\bcontent\b)/gi);
                    const children = parts.map((part) =>
                        /^\bcontent\b$/i.test(part)
                            ? h('span', { class: 'log-content-highlight' }, part)
                            : part
                    );

                    return h('div', { class: 'p-3' }, [
                        h('pre', {
                            style: 'white-space:pre-wrap;word-break:break-word;background:#fff;border:1px solid #ddd;padding:8px;margin:0;border-radius:3px;max-height:420px;overflow:auto;font-size:.92rem;'
                        }, children)
                    ]);
                }
            });

            return h('div', { class: 'logs-page' }, [
                statusEl,
                toolbar,
                filters,
                selectionEl,
                h('div', { class: 'logs-grid-card' }, [table])
            ]);
        };
    }
};

// ---- Mount ----
createApp(LogsApp)
    .use(PrimeVue, { theme: { preset: Aura, options: { darkModeSelector: false } } })
    .use(ConfirmationService)
    .component('DataTable', DataTable)
    .component('Column', Column)
    .component('Button', Button)
    .mount('#logs-vue-app');
