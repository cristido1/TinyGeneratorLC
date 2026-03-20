(function(){
    if (typeof $ === 'undefined' || !$.fn.dataTable) return;
    $(document).ready(function(){
        var table = $('#agentsTable').DataTable({
            paging: true,
            pageLength: 20,
            lengthChange: true,
            ordering: true,
            // Default order: Role (column index 2 now, since details column added)
            order: [[2, 'asc']],
            info: true,
            autoWidth: false,
            responsive: {
                details: {
                    type: 'column',
                    target: 0,
                    renderer: function (api, rowIdx, columns) {
                        try {
                            var rowData = api.row(rowIdx).data();
                            // prompt/instructions/execution_plan are stored at columns 7,8,9
                            var prompt = rowData && rowData.length > 7 ? rowData[7] : '';
                            var instructions = rowData && rowData.length > 8 ? rowData[8] : '';
                            var execPlan = rowData && rowData.length > 9 ? rowData[9] : '';

                            var $wrap = $('<div/>').addClass('tg-responsive-details').css({ padding: '8px' });

                            if (prompt && prompt.length) {
                                $wrap.append($('<h6/>').text('Prompt'));
                                $wrap.append($('<pre/>').text(prompt).css({ whiteSpace: 'pre-wrap', background: '#e9f5ff', padding: '8px', borderRadius: '6px' }));
                            }
                            if (instructions && instructions.length) {
                                $wrap.append($('<h6/>').text('Instructions'));
                                $wrap.append($('<pre/>').text(instructions).css({ whiteSpace: 'pre-wrap', background: '#f6f6f6', padding: '8px', borderRadius: '6px' }));
                            }
                            if (execPlan && execPlan.length) {
                                $wrap.append($('<h6/>').text('Execution plan'));
                                $wrap.append($('<pre/>').text(execPlan).css({ whiteSpace: 'pre-wrap', background: '#fff8e1', padding: '8px', borderRadius: '6px' }));
                            }

                            return $wrap.length ? $wrap.prop('outerHTML') : false;
                        } catch (e) {
                            console.error('responsive.renderer error', e);
                            return false;
                        }
                    }
                }
            },
            dom: "<'row'<'col-sm-6'B><'col-sm-6'f>>" +
                 "rt" +
                 "<'row'<'col-sm-6'i><'col-sm-6'p>>",
            columnDefs: [
                { className: 'control', orderable: false, targets: 0 },
                { orderable: false, targets: 6 },
                { visible: false, targets: [7,8,9] }
            ],
            buttons: [
                {
                    extend: 'colvis',
                    text: 'Columns',
                    className: 'dt-btn-white btn btn-sm me-2'
                },
                {
                    text: '<i class="bi bi-plus-lg" aria-hidden="true"></i> New',
                    className: 'btn btn-sm btn-primary btn-new-min',
                    action: function (e, dt, node, config) {
                        window.location.href = '/Agents/Create';
                    }
                }
            ]
                // Enable native state saving with a unique key and persistent duration
                stateSave: true,
                stateSaveName: 'DataTables_agents_external',
                stateDuration: -1
        });
        table.buttons().container().addClass('mb-2');

        // Use DataTables responsive details (column) to handle expand/collapse
        table.on('responsive-display', function (e, datatable, row, showHide, update) {
            // nothing extra needed here; responsive will toggle details
        });
    });
})();
