// Models Index2 - Vue 3 + PrimeVue page
// All data loaded from API, all commands executed via API
import { j as h, l as ref, y as computed, p as onMounted, z as createApp, u as unref }
    from './chunks/primeicons-CDhzlABi.js';
import { j as Button, n as Column, o as DataTable, P as PrimeVue, Q as Aura }
    from './chunks/index-pttFU7Tr.js';

// ─── API helpers ─────────────────────────────────────────────────────────────

async function apiPost(url, data) {
    const resp = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data)
    });
    const json = await resp.json().catch(() => ({}));
    if (!resp.ok) throw new Error(json?.error || json?.message || resp.statusText);
    return json;
}

async function execCommand(code, payload) {
    return apiPost(`/api/crud/models/commands/${code}`, payload || {});
}

// ─── Format helpers ───────────────────────────────────────────────────────────

function fmtScore(val) {
    if (val == null || val === 0) return '-';
    const n = Number(val);
    return isFinite(n) ? n.toFixed(1) : '-';
}

function fmtDate(val) {
    if (!val) return '-';
    try {
        return new Date(val).toLocaleString('it-IT', {
            day: '2-digit', month: '2-digit', year: '2-digit',
            hour: '2-digit', minute: '2-digit'
        });
    } catch { return val; }
}

function fmtCost(val) {
    const n = Number(val);
    if (!n || !isFinite(n)) return '-';
    return n.toFixed(5);
}

// ─── Role stats table (expansion row) ────────────────────────────────────────

function renderRoleStats(data, cache) {
    const stats = cache[data.Id];
    if (!stats) {
        return h('div', { class: 'p-2 text-muted small' }, 'Caricamento statistiche ruolo…');
    }
    if (stats.error) {
        return h('div', { class: 'p-2 text-danger small' }, 'Errore: ' + stats.error);
    }
    if (!stats.rows || stats.rows.length === 0) {
        return h('div', { class: 'p-2 text-muted small' }, 'Nessuna statistica disponibile.');
    }
    const rows = stats.rows.map(r => {
        const isPrimBadge = r.isPrimary
            ? h('span', { class: 'badge bg-success' }, 'Primary')
            : h('span', { class: 'badge bg-secondary' }, 'Fallback');
        const enabledBadge = r.enabled
            ? h('span', { class: 'badge bg-success' }, 'Yes')
            : h('span', { class: 'badge bg-danger' }, 'No');
        return h('tr', { key: r.role }, [
            h('td', {}, r.role || '-'),
            h('td', {}, isPrimBadge),
            h('td', {}, enabledBadge),
            h('td', {}, String(r.useCount ?? '-')),
            h('td', {}, String(r.totalPromptTokens ?? '-')),
            h('td', {}, String(r.totalOutputTokens ?? '-')),
            h('td', {}, fmtScore(r.avgPromptTps)),
            h('td', {}, fmtScore(r.avgGenTps)),
            h('td', {}, fmtScore(r.avgE2eTps)),
            h('td', {}, r.successRatePct != null ? r.successRatePct.toFixed(1) + '%' : '-'),
        ]);
    });
    return h('div', { class: 'p-2' }, [
        h('h6', { class: 'mb-2 small fw-bold' }, 'Statistiche per ruolo – ' + (stats.modelName || '')),
        h('div', { class: 'table-responsive' }, [
            h('table', { class: 'table table-sm table-hover mb-0' }, [
                h('thead', { class: 'table-light' }, [
                    h('tr', {}, [
                        h('th', {}, 'Ruolo'), h('th', {}, 'Tipo'), h('th', {}, 'Enabled'),
                        h('th', {}, 'Use'), h('th', {}, 'P.Tok'), h('th', {}, 'O.Tok'),
                        h('th', {}, 'Prompt TPS'), h('th', {}, 'Gen TPS'), h('th', {}, 'E2E TPS'),
                        h('th', {}, 'Succ %'),
                    ])
                ]),
                h('tbody', {}, rows)
            ])
        ])
    ]);
}

// ─── Actions dropdown (Bootstrap 5) ──────────────────────────────────────────

function renderActionsDropdown(data, groups, runCmdFn) {
    const mid = data.Id;
    const mname = data.Name;
    const groupItems = (groups || []).map(g =>
        h('li', { key: g }, [
            h('button', {
                class: 'dropdown-item',
                type: 'button',
                onClick: (e) => { e.preventDefault(); runCmdFn('models_run_group', { modelId: mid, modelName: mname, group: g }); }
            }, '▶ ' + g)
        ])
    );
    return h('div', { class: 'dropdown' }, [
        h('button', {
            class: 'btn btn-sm btn-outline-secondary dropdown-toggle',
            type: 'button',
            'data-bs-toggle': 'dropdown'
        }, 'Azioni'),
        h('ul', { class: 'dropdown-menu dropdown-menu-end' }, [
            h('li', {}, [h('a', { class: 'dropdown-item', href: `/Models/Edit?id=${mid}` }, 'Edit')]),
            h('li', {}, [h('a', { class: 'dropdown-item', href: `/Chat?model=${encodeURIComponent(mname)}` }, '💬 Chat')]),
            h('li', {}, [
                h('button', {
                    class: 'dropdown-item', type: 'button',
                    onClick: (e) => { e.preventDefault(); runCmdFn('models_run_json_score_model', { modelId: mid, modelName: mname }); }
                }, '🧪 JSON score')
            ]),
            h('li', {}, [
                h('button', {
                    class: 'dropdown-item', type: 'button',
                    onClick: (e) => { e.preventDefault(); runCmdFn('models_run_instruction_score_model', { modelId: mid, modelName: mname }); }
                }, 'Instruction score')
            ]),
            h('li', {}, [
                h('button', {
                    class: 'dropdown-item', type: 'button',
                    onClick: (e) => { e.preventDefault(); runCmdFn('models_run_intelligence_test_model', { modelId: mid, modelName: mname }); }
                }, 'Intelligence test')
            ]),
            h('li', {}, [h('hr', { class: 'dropdown-divider' })]),
            h('li', {}, [
                h('button', {
                    class: 'dropdown-item text-danger', type: 'button',
                    onClick: (e) => {
                        e.preventDefault();
                        if (confirm(`Eliminare il modello "${mname}"?`)) {
                            runCmdFn('models_delete_model', { modelId: mid, modelName: mname });
                        }
                    }
                }, '🗑️ Elimina')
            ]),
            groupItems.length > 0
                ? h('li', {}, [h('hr', { class: 'dropdown-divider' })])
                : null,
            groupItems.length > 0
                ? h('li', {}, [h('h6', { class: 'dropdown-header' }, 'Run Tests')])
                : null,
            ...groupItems
        ])
    ]);
}

// ─── Main component ───────────────────────────────────────────────────────────

const ModelsIndex2App = {
    __name: 'ModelsIndex2App',
    setup() {
        // ── state ────────────────────────────────────────────────────────────
        const items        = ref([]);
        const totalCount   = ref(0);
        const loading      = ref(false);
        const globalSearch = ref('');
        const showDisabled = ref(false);
        const selectedGroup = ref('');
        const testGroups   = ref([]);
        const expandedRows = ref({});
        const roleStatsCache = ref({});
        const message      = ref('');
        const messageType  = ref('success');

        // pagination / sort
        const page      = ref(1);
        const pageSize  = ref(50);
        const sortField = ref('Name');
        const sortOrder = ref(1); // 1 = asc, -1 = desc

        // ── helpers ──────────────────────────────────────────────────────────

        function showMessage(msg, type) {
            message.value = msg;
            messageType.value = type || 'success';
            setTimeout(() => { if (message.value === msg) message.value = ''; }, 4000);
        }

        async function runCommand(code, payload, confirmMsg) {
            if (confirmMsg && !confirm(confirmMsg)) return;
            try {
                const result = await execCommand(code, payload);
                const msg = result.runId  ? `Avviato (${result.runId})`
                    : result.runIds?.length ? `Avviati (${result.runIds.join(', ')})`
                    : result.message || 'Eseguito';
                showMessage(msg);
                if (code === 'models_delete_model') {
                    await loadModels();
                }
            } catch (e) {
                showMessage('Errore: ' + (e.message || e), 'error');
            }
        }

        // ── data loading ─────────────────────────────────────────────────────

        async function loadTestGroups() {
            try {
                let groups = [];
                try {
                    const r = await apiPost('/api/crud/test_prompts/query', {
                        page: 1, pageSize: 200, filters: [], sorts: [{ field: 'GroupName', dir: 'asc' }], globalSearch: null
                    });
                    groups = [...new Set((r.items || []).map(x => x.GroupName).filter(Boolean))];
                } catch { /* table may not exist */ }

                if (groups.length === 0) {
                    const r2 = await apiPost('/api/crud/test_definitions/query', {
                        page: 1, pageSize: 200, filters: [], sorts: [{ field: 'GroupName', dir: 'asc' }], globalSearch: null
                    });
                    groups = [...new Set((r2.items || []).map(x => x.GroupName).filter(Boolean))];
                }
                testGroups.value = groups;
                if (groups.length > 0 && !selectedGroup.value) {
                    selectedGroup.value = groups[0];
                }
            } catch (e) {
                console.warn('Test groups not available:', e.message);
            }
        }

        async function loadModels() {
            loading.value = true;
            try {
                const filters = [];
                if (!showDisabled.value) {
                    filters.push({ field: 'IsActive', op: 'eq', value: true });
                }
                const sorts = sortField.value
                    ? [{ field: sortField.value, dir: sortOrder.value > 0 ? 'asc' : 'desc' }]
                    : [];
                const resp = await apiPost('/api/crud/models/query', {
                    page: page.value,
                    pageSize: pageSize.value,
                    filters,
                    sorts,
                    globalSearch: globalSearch.value || null
                });
                items.value = resp.items || [];
                totalCount.value = resp.total || 0;
            } catch (e) {
                showMessage('Errore caricamento modelli: ' + (e.message || e), 'error');
            } finally {
                loading.value = false;
            }
        }

        // ── row expansion ────────────────────────────────────────────────────

        async function loadRoleStats(modelId) {
            if (roleStatsCache.value[modelId] !== undefined) return;
            // Mark as loading
            roleStatsCache.value[modelId] = null;
            try {
                const resp = await fetch(`/Models?handler=RoleStats&modelId=${modelId}`);
                if (!resp.ok) throw new Error('HTTP ' + resp.status);
                const data = await resp.json();
                roleStatsCache.value[modelId] = data;
            } catch (e) {
                roleStatsCache.value[modelId] = { error: e.message };
            }
        }

        function onRowExpand(event) {
            const id = event.data?.Id;
            if (id != null) loadRoleStats(id);
        }

        // ── sort / pagination ────────────────────────────────────────────────

        function onSort(event) {
            sortField.value = event.sortField;
            sortOrder.value = event.sortOrder;
            page.value = 1;
            loadModels();
        }

        function onPage(event) {
            page.value = (event.page || 0) + 1;
            pageSize.value = event.rows || pageSize.value;
            loadModels();
        }

        let searchTimer = null;
        function onSearch(e) {
            globalSearch.value = e.target.value;
            clearTimeout(searchTimer);
            searchTimer = setTimeout(() => { page.value = 1; loadModels(); }, 400);
        }

        // ── lifecycle ────────────────────────────────────────────────────────

        onMounted(async () => {
            await loadTestGroups();
            await loadModels();
        });

        // ── badge helpers ────────────────────────────────────────────────────

        function providerBadge(provider) {
            const p = (provider || '').toLowerCase();
            if (p === 'ollama')  return h('span', { class: 'badge bg-success' }, 'ollama');
            if (p === 'vllm')    return h('span', { class: 'badge bg-info text-dark' }, 'vllm');
            if (p === 'openai')  return h('span', { class: 'badge bg-warning text-dark' }, 'openai');
            return h('span', { class: 'badge bg-secondary' }, provider || '-');
        }

        function enabledBadge(val) {
            return val
                ? h('span', { class: 'badge bg-success' }, 'Yes')
                : h('span', { class: 'badge bg-danger' }, 'No');
        }

        // ── render function ──────────────────────────────────────────────────

        return () => {
            const modelItems = items.value;
            const groups     = testGroups.value;
            const cache      = roleStatsCache.value;

            return h('div', { class: 'page-wrapper' }, [

                // ── toolbar ──────────────────────────────────────────────────
                h('div', { class: 'd-flex flex-wrap gap-1 mb-2 align-items-center' }, [
                    h('a', { href: '/Models/Edit', class: 'btn btn-sm btn-light border text-primary' },
                        [h('i', { class: 'bi bi-plus-circle me-1' }), 'Nuovo']),
                    h('button', {
                        class: 'btn btn-sm btn-light border text-primary', type: 'button',
                        onClick: () => runCommand('models_run_json_score')
                    }, [h('i', { class: 'bi bi-code-slash me-1' }), 'JSON score']),
                    h('button', {
                        class: 'btn btn-sm btn-light border text-primary', type: 'button',
                        onClick: () => runCommand('models_run_instruction_score')
                    }, [h('i', { class: 'bi bi-list-check me-1' }), 'Instruction score']),
                    h('button', {
                        class: 'btn btn-sm btn-light border text-primary', type: 'button',
                        onClick: () => runCommand('models_run_intelligence_test')
                    }, [h('i', { class: 'bi bi-lightbulb me-1' }), 'Intelligence test']),
                    h('button', {
                        class: 'btn btn-sm btn-light border text-primary', type: 'button',
                        onClick: async () => {
                            try {
                                const r = await apiPost('/api/commands/validate-agent-json-examples', {});
                                showMessage(r.runId ? `Avviato (${r.runId})` : (r.message || 'Avviato'));
                            } catch (e) { showMessage('Errore: ' + e.message, 'error'); }
                        }
                    }, [h('i', { class: 'bi bi-shield-check me-1' }), 'Validate JSON']),
                    // group selector
                    h('select', {
                        class: 'form-select form-select-sm',
                        style: { width: '130px', display: 'inline-block' },
                        value: selectedGroup.value,
                        onChange: (e) => { selectedGroup.value = e.target.value; }
                    }, groups.map(g => h('option', { value: g, key: g }, g))),
                    h('button', {
                        class: 'btn btn-sm btn-light border', type: 'button',
                        onClick: () => runCommand('models_run_all', { group: selectedGroup.value })
                    }, [h('i', { class: 'bi bi-play-fill me-1' }), 'Group']),
                    h('button', {
                        class: 'btn btn-sm btn-light border', type: 'button',
                        title: 'Avvia tutti i modelli abilitati con il primo gruppo disponibile',
                        onClick: () => runCommand('models_run_all', { group: '' })
                    }, [h('i', { class: 'bi bi-collection-play me-1' }), 'All']),
                    h('button', {
                        class: 'btn btn-sm btn-light border text-danger', type: 'button',
                        onClick: () => runCommand('models_purge_disabled_ollama', {}, 'Purge disabled Ollama models?')
                    }, [h('i', { class: 'bi bi-trash3 me-1' }), 'Purge']),
                    h('button', {
                        class: 'btn btn-sm btn-light border', type: 'button',
                        onClick: () => runCommand('models_refresh_contexts')
                    }, [h('i', { class: 'bi bi-arrow-repeat me-1' }), 'Ctx']),
                    h('button', {
                        class: 'btn btn-sm btn-light border', type: 'button',
                        onClick: () => runCommand('models_add_ollama_models')
                    }, [h('i', { class: 'bi bi-search me-1' }), 'Discover']),
                    h('button', {
                        class: 'btn btn-sm btn-light border', type: 'button',
                        onClick: () => runCommand('models_recalculate_scores')
                    }, [h('i', { class: 'bi bi-calculator me-1' }), 'Recalc']),
                    h('button', {
                        class: 'btn btn-sm btn-light border', type: 'button',
                        onClick: loadModels
                    }, [h('i', { class: 'bi bi-arrow-clockwise me-1' }), 'Refresh']),
                    // show disabled toggle
                    h('div', { class: 'form-check form-switch d-inline-flex align-items-center ms-1' }, [
                        h('input', {
                            class: 'form-check-input', type: 'checkbox',
                            id: 'showDisabledV2',
                            checked: showDisabled.value,
                            onChange: (e) => { showDisabled.value = e.target.checked; page.value = 1; loadModels(); }
                        }),
                        h('label', { class: 'form-check-label small ms-1', for: 'showDisabledV2' }, 'Disabilitati')
                    ]),
                    // search
                    h('input', {
                        type: 'text', class: 'form-control form-control-sm ms-auto',
                        style: { maxWidth: '200px' },
                        placeholder: 'Cerca…',
                        value: globalSearch.value,
                        onInput: onSearch
                    })
                ]),

                // ── message alert ─────────────────────────────────────────────
                message.value
                    ? h('div', { class: `alert alert-${messageType.value === 'error' ? 'danger' : 'success'} py-1 px-2 mb-2 small` }, message.value)
                    : null,

                // ── data table ────────────────────────────────────────────────
                h(DataTable, {
                    value: modelItems,
                    loading: loading.value,
                    dataKey: 'Id',
                    stripedRows: true,
                    size: 'small',
                    scrollable: true,
                    scrollHeight: 'calc(100vh - 210px)',
                    sortField: sortField.value,
                    sortOrder: sortOrder.value,
                    onSort: onSort,
                    expandedRows: expandedRows.value,
                    'onUpdate:expandedRows': v => { expandedRows.value = v; },
                    onRowExpand: onRowExpand,
                    paginator: true,
                    rows: pageSize.value,
                    totalRecords: totalCount.value,
                    lazy: true,
                    onPage: onPage,
                    rowsPerPageOptions: [25, 50, 100, 200],
                }, {
                    default: () => [
                        // expander
                        h(Column, { expander: true, style: { width: '3rem', flex: '0 0 3rem' } }),
                        // Name
                        h(Column, { field: 'Name', header: 'Name', sortable: true, style: { minWidth: '180px' } }, {
                            body: ({ data }) => {
                                const imgEl = data.Image
                                    ? h('img', { src: '/' + data.Image, style: { width: '18px', height: '18px', objectFit: 'contain', borderRadius: '3px' }, alt: data.Provider })
                                    : null;
                                return h('span', { class: 'd-flex align-items-center gap-1' }, [imgEl, h('span', {}, data.Name)]);
                            }
                        }),
                        // Size
                        h(Column, { field: 'SizeText', header: 'Size', sortable: true, style: { minWidth: '80px' } }, {
                            body: ({ data }) => data.SizeText || '-'
                        }),
                        // Provider
                        h(Column, { field: 'Provider', header: 'Provider', sortable: true, style: { minWidth: '100px' } }, {
                            body: ({ data }) => providerBadge(data.Provider)
                        }),
                        // Note
                        h(Column, { field: 'Note', header: 'Note', sortable: false, style: { minWidth: '120px', maxWidth: '200px' } }, {
                            body: ({ data }) => data.Note
                                ? h('span', { title: data.Note, style: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', display: 'block' } }, data.Note)
                                : ''
                        }),
                        // Enabled
                        h(Column, { field: 'IsActive', header: 'Enabled', sortable: true, style: { minWidth: '80px' } }, {
                            body: ({ data }) => enabledBadge(data.IsActive)
                        }),
                        // Local
                        h(Column, { field: 'IsLocal', header: 'Local', sortable: true, style: { minWidth: '60px' } }, {
                            body: ({ data }) => data.IsLocal ? 'Yes' : 'No'
                        }),
                        // MaxCtx
                        h(Column, { field: 'MaxContext', header: 'MaxCtx', sortable: true, style: { minWidth: '80px' } }),
                        // CtxToUse
                        h(Column, { field: 'ContextToUse', header: 'CtxToUse', sortable: true, style: { minWidth: '80px' } }),
                        // Total Score
                        h(Column, { field: 'TotalScore', header: 'Total', sortable: true, style: { minWidth: '65px' } }, {
                            body: ({ data }) => fmtScore(data.TotalScore)
                        }),
                        // Base / Fn Call
                        h(Column, { field: 'BaseScore', header: 'Fn Call', sortable: true, style: { minWidth: '70px' } }, {
                            body: ({ data }) => fmtScore(data.BaseScore)
                        }),
                        // TextEval
                        h(Column, { field: 'TextEvalScore', header: 'TextEval', sortable: true, style: { minWidth: '80px' } }, {
                            body: ({ data }) => fmtScore(data.TextEvalScore)
                        }),
                        // Writer
                        h(Column, { field: 'WriterScore', header: 'Writer', sortable: true, style: { minWidth: '70px' } }, {
                            body: ({ data }) => fmtScore(data.WriterScore)
                        }),
                        // TTS
                        h(Column, { field: 'TtsScore', header: 'TTS', sortable: true, style: { minWidth: '60px' } }, {
                            body: ({ data }) => fmtScore(data.TtsScore)
                        }),
                        // Music
                        h(Column, { field: 'MusicScore', header: 'Music', sortable: true, style: { minWidth: '70px' } }, {
                            body: ({ data }) => fmtScore(data.MusicScore)
                        }),
                        // FX
                        h(Column, { field: 'FxScore', header: 'FX', sortable: true, style: { minWidth: '60px' } }, {
                            body: ({ data }) => fmtScore(data.FxScore)
                        }),
                        // Ambient
                        h(Column, { field: 'AmbientScore', header: 'Ambient', sortable: true, style: { minWidth: '80px' } }, {
                            body: ({ data }) => fmtScore(data.AmbientScore)
                        }),
                        // Json Score
                        h(Column, { field: 'JsonScore', header: 'Json', sortable: true, style: { minWidth: '60px' } }, {
                            body: ({ data }) => fmtScore(data.JsonScore)
                        }),
                        // Instruction Score
                        h(Column, { field: 'InstructionScore', header: 'Instr', sortable: true, style: { minWidth: '60px' } }, {
                            body: ({ data }) => fmtScore(data.InstructionScore)
                        }),
                        // Intelligence Score
                        h(Column, { field: 'IntelliScore', header: 'Intel', sortable: true, style: { minWidth: '60px' } }, {
                            body: ({ data }) => data.IntelliScore != null ? String(data.IntelliScore) : '-'
                        }),
                        // Intelligence Time
                        h(Column, { field: 'IntelliTime', header: 'IntelTime(s)', sortable: true, style: { minWidth: '100px' } }, {
                            body: ({ data }) => data.IntelliTime != null ? String(data.IntelliTime) : '-'
                        }),
                        // NoTools
                        h(Column, { field: 'NoTools', header: 'NoTools', sortable: true, style: { minWidth: '80px' } }, {
                            body: ({ data }) => data.NoTools ? '✓' : ''
                        }),
                        // Promotions
                        h(Column, { field: 'Promotions', header: 'Promotions', sortable: true, style: { minWidth: '90px' } }),
                        // Demotions
                        h(Column, { field: 'Demotions', header: 'Demotions', sortable: true, style: { minWidth: '90px' } }),
                        // Rank Gain
                        h(Column, { header: 'Rank Gain', sortable: false, style: { minWidth: '90px' } }, {
                            body: ({ data }) => String((data.Promotions || 0) - (data.Demotions || 0))
                        }),
                        // Fn Call Time
                        h(Column, { field: 'TestDurationSeconds', header: 'Fn Call Time', sortable: true, style: { minWidth: '110px' } }, {
                            body: ({ data }) => data.TestDurationSeconds != null ? Number(data.TestDurationSeconds).toFixed(2) : '-'
                        }),
                        // Cost In
                        h(Column, { field: 'CostInPerToken', header: 'Cost In', sortable: true, style: { minWidth: '90px' } }, {
                            body: ({ data }) => fmtCost(data.CostInPerToken)
                        }),
                        // Cost Out
                        h(Column, { field: 'CostOutPerToken', header: 'Cost Out', sortable: true, style: { minWidth: '90px' } }, {
                            body: ({ data }) => fmtCost(data.CostOutPerToken)
                        }),
                        // Created
                        h(Column, { field: 'CreatedAt', header: 'Created', sortable: true, style: { minWidth: '110px' } }, {
                            body: ({ data }) => fmtDate(data.CreatedAt)
                        }),
                        // Actions
                        h(Column, { header: 'Actions', sortable: false, frozen: true, alignFrozen: 'right', style: { minWidth: '100px', flex: '0 0 100px' } }, {
                            body: ({ data }) => renderActionsDropdown(data, groups, runCommand)
                        }),
                    ],
                    expansion: ({ data }) => renderRoleStats(data, cache),
                }),
            ]);
        };
    }
};

// ─── Mount ────────────────────────────────────────────────────────────────────

createApp(ModelsIndex2App)
    .use(PrimeVue, { theme: { preset: Aura, options: { darkModeSelector: false } } })
    .mount('#models-index2-app');
