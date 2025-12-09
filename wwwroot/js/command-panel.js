// Global command panel subscribed to ProgressHub (SignalR)
(function () {
    if (!window.signalR) {
        console.warn("SignalR not available, command panel disabled.");
        return;
    }

    let connection = null;
    const panel = document.createElement('div');
    panel.id = 'command-panel';
    panel.innerHTML = `
        <div class="command-panel-card">
            <div class="command-panel-header">
                <span class="me-2">⚙️ Comandi in esecuzione / coda</span>
                <span class="badge bg-primary" id="cmd-count">0</span>
            </div>
            <div class="command-panel-body">
                <div id="cmd-empty" class="text-muted small">Nessuna attività</div>
                <div id="cmd-list" class="command-panel-list"></div>
            </div>
        </div>
    `;
    document.body.appendChild(panel);

    const cmdListEl = panel.querySelector('#cmd-list');
    const emptyEl = panel.querySelector('#cmd-empty');
    const countEl = panel.querySelector('#cmd-count');

    function renderCommands(cmds) {
        console.log('Rendering commands:', cmds); // DEBUG
        if (!Array.isArray(cmds)) cmds = [];
        
        // Filtra comandi attivi (queued/running) o completati da meno di 5 minuti
        const now = new Date();
        const fiveMinutesAgo = new Date(now.getTime() - 5 * 60 * 1000);
        
        const visibleCommands = cmds.filter(c => {
            const status = (c.Status || '').toLowerCase();
            // Mostra sempre queued e running
            if (status === 'queued' || status === 'running') return true;
            
            // Per completed/failed/cancelled, mostra solo se recenti (< 5 min)
            if (status === 'completed' || status === 'failed' || status === 'cancelled') {
                const completedAt = c.CompletedAt ? new Date(c.CompletedAt) : null;
                return completedAt && completedAt >= fiveMinutesAgo;
            }
            
            return false;
        });
        
        countEl.textContent = visibleCommands.length;
        if (visibleCommands.length === 0) {
            emptyEl.style.display = '';
            cmdListEl.innerHTML = '';
            return;
        }
        emptyEl.style.display = 'none';
        cmdListEl.innerHTML = visibleCommands.map(c => {
            const status = (c.Status || '').toLowerCase();
            let statusClass = 'queued';
            let statusBadgeClass = 'bg-warning text-dark';
            let statusIcon = '⏳';
            
            if (status === 'running') {
                statusClass = 'running';
                statusBadgeClass = 'bg-primary';
                statusIcon = '▶️';
            } else if (status === 'completed') {
                statusClass = 'completed';
            const stepInfo = (c.CurrentStep && c.MaxStep) ? `Step ${c.CurrentStep}/${c.MaxStep}` : '';
            const retryInfo = (c.RetryCount > 0) ? `Retry: ${c.RetryCount}` : '';
            
            // Informazioni principali sempre visibili
            const agent = c.AgentName || 'N/A';
            const op = c.OperationName || c.ThreadScope || 'N/A';
            
            // Mostra solo l'ultima parte del nome del modello dopo la barra
            let modelShort = c.ModelName || 'N/A';
            if (modelShort && modelShort !== 'N/A' && modelShort.includes('/')) {
                modelShort = modelShort.substring(modelShort.lastIndexOf('/') + 1);
            }
            
            const errorMsg = c.ErrorMessage ? `<div class="cmd-error text-danger small mt-1">⚠️ ${c.ErrorMessage}</div>` : '';
            
            // Costruisci cmd-meta con agente, modello e info opzionali
            const metaParts = [];
            metaParts.push(`<span class="me-2">👤 ${agent}</span>`);
            metaParts.push(`<span class="me-2">🧠 ${modelShort}</span>`);
            if (stepInfo) metaParts.push(`<span class="me-2">${stepInfo}</span>`);
            if (retryInfo) metaParts.push(`<span class="me-2 text-warning">${retryInfo}</span>`);
            
            const metaHtml = `<div class="cmd-meta">${metaParts.join('')}</div>`;
            
            return `
                <div class="cmd-item ${statusClass}">
                    <div class="cmd-top">
                        <span class="cmd-op">${statusIcon} ${op}</span>
                        <span class="cmd-status badge ${statusBadgeClass}">${c.Status || 'unknown'}</span>
                    </div>
                    ${metaHtml}
                    ${errorMsg}
                </div>
            `;          ${retryInfo ? `<span class="me-2 text-warning">${retryInfo}</span>` : ''}
                    </div>` : '';
            
            return `
                <div class="cmd-item ${statusClass}">
                    <div class="cmd-top">
                        <span class="cmd-op">${statusIcon} ${op}</span>
                        <span class="cmd-status badge ${statusBadgeClass}">${c.Status || ''}</span>
                    </div>
                    ${metaHtml}
                    ${errorMsg}
                </div>
            `;
        }).join('');
    }

    async function ensureConnection() {
        if (connection && connection.state === 'Connected') return;
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/progressHub')
            .withAutomaticReconnect()
            .build();

        connection.on('CommandListUpdated', cmds => {
            renderCommands(cmds);
        });

        connection.on('StepProgress', (genId, current, max, desc) => {
            // Nothing to do here directly; UpdateStep will trigger CommandListUpdated.
        });

        try {
            await connection.start();
            // On connect, ProgressHub sends initial snapshot.
        } catch (e) {
            console.error('Command panel SignalR connect error', e);
        }
    }

    ensureConnection();
})();
