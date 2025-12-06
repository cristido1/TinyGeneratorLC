// datatable-extensions.js
// Auto-initialize tables with class 'datatable'
(function () {
    function tryReloadTable(dt) {
        try {
            if (dt && dt.ajax && typeof dt.ajax.reload === "function") {
                dt.ajax.reload(null, false);
            } else if (dt) {
                dt.draw(false);
            }
        } catch (e) {
            console.warn("Reload table failed:", e);
        }
    }

    function enhanceButtonsForInstance(dt, $table) {
        try {
            var refreshSvg = '<svg class="dt-btn-icon" xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="23 4 23 10 17 10"></polyline><polyline points="1 20 1 14 7 14"></polyline><path d="M3.51 9a9 9 0 0114.13-3.36L23 10"></path><path d="M20.49 15a9 9 0 01-14.13 3.36L1 14"></path></svg>';
            var columnsSvg = '<svg class="dt-btn-icon" xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7"></rect><rect x="14" y="3" width="7" height="7"></rect><rect x="3" y="14" width="7" height="7"></rect><rect x="14" y="14" width="7" height="7"></rect></svg>';

            // If buttons already exist, avoid duplicating; but ensure a refresh button is present
            var existing = dt.buttons ? dt.buttons().count() : 0;
            if (existing && existing > 0) {
                try {
                    var container = dt.buttons().container();
                    if (container && container.length) {
                        // if refresh button missing, inject a simple button element into the container
                        if ($(container).find('.dt-btn-refresh').length === 0) {
                            $(container).prepend('<button type="button" class="dt-action-btn dt-btn-refresh" title="Refresh">' + refreshSvg + '</button>');
                        }
                    }
                } catch (e) { /* ignore */ }

                // attach refresh handler if not present
                $table.closest('.dataTables_wrapper').off('click', '.dt-btn-refresh').on('click', '.dt-btn-refresh', function () { tryReloadTable(dt); });
                return;
            }

            var btns = [
                {
                    text: '<button type="button" class="dt-action-btn dt-btn-refresh" title="Refresh">' + refreshSvg + '</button>',
                    className: 'dt-btn-refresh',
                    action: function (e, dtLocal, node, config) { tryReloadTable(dtLocal); }
                },
                {
                    extend: 'colvis',
                    text: '<button type="button" class="dt-action-btn dt-btn-colvis" title="Columns">' + columnsSvg + '</button>',
                    className: 'dt-btn-colvis'
                }
            ];

            new $.fn.dataTable.Buttons(dt, { buttons: btns });
            var container = dt.buttons().container();
            if (container && container.length) {
                // Move buttons into the wrapper top area if present
                var wrapper = $table.closest('.dataTables_wrapper').find('.dt-top');
                if (wrapper && wrapper.length) {
                    $(container).prependTo(wrapper);
                } else {
                    $(container).prependTo($table.closest('.dataTables_wrapper'));
                }
            }

            // ensure refresh click handled
            $table.closest('.dataTables_wrapper').off('click', '.dt-btn-refresh').on('click', '.dt-btn-refresh', function () { tryReloadTable(dt); });
        }
        catch (e) { console.warn('enhanceButtonsForInstance error', e); }
    }

    function initDataTableAuto($table) {
        // If already initialized, attach buttons only
        if ($.fn.dataTable.isDataTable($table)) {
            try { var dtExisting = $table.DataTable(); enhanceButtonsForInstance(dtExisting, $table); } catch (e) { console.warn(e); }
            return;
        }

        // Wait for any other initializer on this table
        var initialized = false;
        var onInit = function (e, settings) {
            try {
                initialized = true;
                var dt = new $.fn.dataTable.Api(settings);
                enhanceButtonsForInstance(dt, $table);
            } catch (ex) { console.warn('init.dt handler error', ex); }
        };

        $table.one('init.dt', onInit);

        // As a fallback, initialize ourselves after a short delay if nothing else did
        setTimeout(function () {
            try {
                if (!initialized && !$.fn.dataTable.isDataTable($table)) {
                    // create a minimal DataTable instance preserving user options if present
                    var instanceName = $table.attr('id') || ('dt_' + Math.random().toString(36).substr(2, 9));
                    var defaultOpts = { dom: "<'dt-top'Bf>rt<'dt-bottom'lip>", stateSave: true };
                    var userOpts = {};
                    try { var attr = $table.attr('data-dt-options'); if (attr) userOpts = JSON.parse(attr); } catch (e) { }
                    var opts = $.extend(true, {}, defaultOpts, userOpts);
                    opts.stateSaveParams = function (settings, data) { settings.sInstance = instanceName; };
                    if (!$table.attr('id')) $table.attr('id', instanceName);
                    var dt = $table.DataTable(opts);
                    enhanceButtonsForInstance(dt, $table);
                }
            } catch (e) { console.warn('fallback init error', e); }
        }, 700);
    }

    $(function () {
        $('table.datatable').each(function () { initDataTableAuto($(this)); });

        window.TG = window.TG || {};
        window.TG.refreshDataTable = function (selector) {
            try {
                var $t = $(selector);
                if ($t.length === 0) return;
                var dt = $t.DataTable();
                tryReloadTable(dt);
            } catch (e) { console.warn(e); }
        };
    });
})();
