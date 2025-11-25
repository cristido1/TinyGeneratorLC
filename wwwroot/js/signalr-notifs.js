// SignalR notifications client: connect to /progressHub and show Bootstrap toasts
(() => {
    function createToast(message, title, level){
        const container = document.getElementById('toast-container');
        if(!container) return;
        const wrapper = document.createElement('div');
        // Use a compact toast class to apply smaller padding/margins
        wrapper.className = 'toast align-items-center text-bg-light border-0 shadow-sm tg-toast-sm';
        wrapper.setAttribute('role', 'alert');
        wrapper.setAttribute('aria-live', 'polite');
        wrapper.setAttribute('aria-atomic', 'true');
        wrapper.style.minWidth = '220px';
        wrapper.style.marginTop = '4px';

        // Check message content for passed/failed keywords and apply pastel colors
        const msgLower = (message || '').toLowerCase();
        if(msgLower.includes('passed')){
            wrapper.style.backgroundColor = '#d4edda'; // pastel green
            wrapper.style.borderColor = '#c3e6cb';
            wrapper.style.borderWidth = '1px';
            wrapper.style.borderStyle = 'solid';
        } else if(msgLower.includes('failed')){
            wrapper.style.backgroundColor = '#f8d7da'; // pastel red
            wrapper.style.borderColor = '#f5c6cb';
            wrapper.style.borderWidth = '1px';
            wrapper.style.borderStyle = 'solid';
        }

        // color by level (fallback for non-passed/failed messages)
        if(level === 'success') wrapper.classList.add('border-success');
        if(level === 'warning') wrapper.classList.add('border-warning');
        if(level === 'error') wrapper.classList.add('border-danger');

        const body = document.createElement('div');
        body.className = 'd-flex';
        body.innerHTML = `<div class="toast-body"><strong>${title ? title + ' — ' : ''}</strong>${escapeHtml(message)}</div>`;
        const btn = document.createElement('button');
        btn.className = 'btn-close me-2 m-auto';
        btn.type = 'button';
        btn.setAttribute('data-bs-dismiss', 'toast');
        btn.setAttribute('aria-label', 'Close');
        btn.style.marginLeft = '8px';

        body.appendChild(btn);
        wrapper.appendChild(body);
        container.appendChild(wrapper);

        const toast = new bootstrap.Toast(wrapper, { autohide: true, delay: 5000 });
        toast.show();
        wrapper.addEventListener('hidden.bs.toast', () => wrapper.remove());
    }

    function escapeHtml(unsafe) {
        return String(unsafe)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    document.addEventListener('DOMContentLoaded', function(){
        // ensure container exists
        let container = document.getElementById('toast-container');
        if(!container){
            container = document.createElement('div');
            container.id = 'toast-container';
            container.className = 'toast-container position-fixed top-0 end-0 p-3';
            container.style.zIndex = 1060; // over modals
            document.body.appendChild(container);
        }

        if (!window.signalR) {
            console.warn('SignalR client is not available (window.signalR).');
            return;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/progressHub')
            .withAutomaticReconnect()
            .build();

        connection.on('AppNotification', function(payload) {
            // payload might be object or string args
            try{
                if(typeof payload === 'object'){
                    createToast(payload.message || '', payload.title || '', payload.level || 'info');
                } else {
                    // older codepath: args (title, message, level)
                    createToast(arguments[1] || '', arguments[0] || '', arguments[2] || 'info');
                }
            } catch(e) { console.warn('Error handling AppNotification', e); }
        });

        // Also listen to ProgressService events if available
        // Keep a simple runtime map for runId -> modelName (registered by the client)
        const runIdToModel = {};
        // No throttling: show all progress messages as requested

        connection.on('ProgressAppended', function(genId, message){
            try {
                // Update per-row progress if we have mapping
                var modelName = runIdToModel[genId + ''] || null;
                if (modelName) {
                    try {
                        // Per-row progress removed: don't update per-row DOM element anymore.
                        // If needed, a side panel or dedicated status area should be used for model-specific logs.
                    } catch (e) { /* ignore DOM errors */ }
                }
                // Show every progress message as a toast
                createToast(message, 'Progress', 'info');
            } catch(e) { console.warn('ProgressAppended handler failed', e); }
        });

        connection.on('ProgressCompleted', function(genId, result){
            try {
                // Completed progress: display toasts only (per-row element removed)
                createToast(result || 'Completed', 'Progress completed', 'success');
            } catch(e) { console.warn('ProgressCompleted handler failed', e); }
        });

        connection.start().then(function(){
            console.debug('SignalR connected: progressHub');
        }).catch(function(err){
            console.warn('SignalR connect failed', err);
        });
        // expose the connection helper for other scripts to join groups and register run maps
        window.appSignalR = {
            connection: connection,
            joinGroup: async function(group){
                try { await connection.invoke('JoinGroup', group); return true; } catch(e){ console.warn('joinGroup failed', e); return false; }
            },
            leaveGroup: async function(group){
                try { await connection.invoke('LeaveGroup', group); return true; } catch(e){ console.warn('leaveGroup failed', e); return false; }
            }
            ,
            registerRunMap: function(map) {
                try {
                    if (!map || !map.forEach) return;
                    map.forEach(function(pair) {
                        try {
                            if (!pair || !pair.runId) return;
                            runIdToModel['' + pair.runId] = pair.model;
                        } catch(e) {}
                    });
                } catch(e) { console.warn('registerRunMap failed', e); }
            }
        };
        // Also provide a global appNotify API (local quick toasts)
        window.appNotify = {
            show: function(title, message, level){ createToast(message, title, level || 'info'); },
            info: function(title, message){ createToast(message, title, 'info'); },
            success: function(title, message){ createToast(message, title, 'success'); },
            warn: function(title, message){ createToast(message, title, 'warning'); },
            error: function(title, message){ createToast(message, title, 'error'); }
        };
    });
})();
