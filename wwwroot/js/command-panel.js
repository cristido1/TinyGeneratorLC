// Global command panel subscribed to ProgressHub (SignalR)
// Shows real-time command queue across all pages. Draggable.
(function () {
    // Wait for SignalR to be loaded
    function waitForSignalR(callback, maxAttempts = 20) {
        let attempts = 0;
        const check = () => {
            if (window.signalR) {
                callback();
            } else if (attempts < maxAttempts) {
                attempts++;
                setTimeout(check, 100);
            } else {
                console.warn("SignalR not available after waiting, command panel disabled.");
            }
        };
        check();
    }

    // Helper function to escape HTML
    function escapeHtml(text) {
        if (!text) return '';
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // Helper function to copy text to clipboard
    window.copyToClipboard = function(text, event) {
        event.stopPropagation();
        event.preventDefault();
        
        // Decodifica il testo se necessario
        const decodedText = text.replace(/\\'/g, "'").replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>');
        
        navigator.clipboard.writeText(decodedText).then(() => {
            // Visual feedback
            const btn = event.target;
            const originalText = btn.textContent;
            btn.textContent = '✅ Copiato!';
            setTimeout(() => {
                btn.textContent = originalText;
            }, 2000);
        }).catch(err => {
            console.error('Fallback: copia via execCommand', err);
            // Fallback per browser vecchi
            const textarea = document.createElement('textarea');
            textarea.value = decodedText;
            document.body.appendChild(textarea);
            textarea.select();
            document.execCommand('copy');
            document.body.removeChild(textarea);
            
            const btn = event.target;
            const originalText = btn.textContent;
            btn.textContent = '✅ Copiato!';
            setTimeout(() => {
                btn.textContent = originalText;
            }, 2000);
        });
    };

    window.cancelCommand = async function(runId, event) {
        event.stopPropagation();
        event.preventDefault();

        if (!runId) {
            return;
        }

        if (!confirm('Annullare il comando?')) {
            return;
        }

        try {
            const resp = await fetch(`/api/commands/cancel/${encodeURIComponent(runId)}`, { method: 'POST' });
            if (!resp.ok) {
                console.error('[CommandPanel] Cancel failed:', await resp.text());
            }
        } catch (err) {
            console.error('[CommandPanel] Cancel error:', err);
        }
    };

    waitForSignalR(initCommandPanel);

    function initCommandPanel() {
        let connection = null;
        let isConnecting = false;
        let reconnectAttempts = 0;

        // Create panel element
        const panel = document.createElement('div');
        panel.id = 'command-panel';
        panel.style.cssText = `
            position: fixed;
            bottom: 20px;
            right: 20px;
            z-index: 9999;
            min-width: 320px;
            max-width: 450px;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            font-size: 13px;
            user-select: none;
        `;
        panel.innerHTML = `
            <div class="command-panel-card" style="
                background: #fff;
                border-radius: 12px;
                box-shadow: 0 8px 32px rgba(0,0,0,0.15);
                overflow: hidden;
                border: 1px solid #e0e0e0;
            ">
                <div class="command-panel-header" id="cmd-panel-header" style="
                    background: linear-gradient(135deg, #16a34a 0%, #15803d 100%);
                    color: #fff;
                    padding: 10px 14px;
                    display: flex;
                    align-items: center;
                    cursor: move;
                ">
                    <span style="flex:1;"><i class="bi bi-gear-fill me-1"></i>Comandi in esecuzione / coda</span>
                    <span class="badge" id="cmd-count" style="
                        background: rgba(255,255,255,0.25);
                        padding: 3px 8px;
                        border-radius: 10px;
                        font-size: 12px;
                    ">0</span>
                    <span id="cmd-connection-status" style="margin-left:8px; font-size:12px; color:#dc2626;">●</span>
                </div>
                <div class="command-panel-body" style="
                    max-height: 300px;
                    overflow-y: auto;
                    padding: 10px;
                    background: #fafafa;
                ">
                    <div id="cmd-empty" class="text-muted" style="text-align:center; padding:20px; color:#888;">Nessuna attività</div>
                    <div id="cmd-list"></div>
                </div>
            </div>
        `;
        document.body.appendChild(panel);

        const cmdListEl = panel.querySelector('#cmd-list');
        const emptyEl = panel.querySelector('#cmd-empty');
        const countEl = panel.querySelector('#cmd-count');
        const statusEl = panel.querySelector('#cmd-connection-status');
        const header = panel.querySelector('#cmd-panel-header');

        // Make panel draggable
        let isDragging = false;
        let dragOffsetX = 0;
        let dragOffsetY = 0;

        header.addEventListener('mousedown', (e) => {
            isDragging = true;
            const rect = panel.getBoundingClientRect();
            dragOffsetX = e.clientX - rect.left;
            dragOffsetY = e.clientY - rect.top;
            panel.style.transition = 'none';
            e.preventDefault();
        });

        document.addEventListener('mousemove', (e) => {
            if (!isDragging) return;
            const x = e.clientX - dragOffsetX;
            const y = e.clientY - dragOffsetY;
            // Convert to position from edges
            panel.style.left = x + 'px';
            panel.style.top = y + 'px';
            panel.style.right = 'auto';
            panel.style.bottom = 'auto';
        });

        document.addEventListener('mouseup', () => {
            if (isDragging) {
                isDragging = false;
                panel.style.transition = '';
            }
        });

        // Touch support for mobile
        header.addEventListener('touchstart', (e) => {
            if (e.touches.length === 1) {
                isDragging = true;
                const touch = e.touches[0];
                const rect = panel.getBoundingClientRect();
                dragOffsetX = touch.clientX - rect.left;
                dragOffsetY = touch.clientY - rect.top;
                panel.style.transition = 'none';
            }
        }, { passive: true });

        document.addEventListener('touchmove', (e) => {
            if (!isDragging || e.touches.length !== 1) return;
            const touch = e.touches[0];
            const x = touch.clientX - dragOffsetX;
            const y = touch.clientY - dragOffsetY;
            panel.style.left = x + 'px';
            panel.style.top = y + 'px';
            panel.style.right = 'auto';
            panel.style.bottom = 'auto';
        }, { passive: true });

        document.addEventListener('touchend', () => {
            if (isDragging) {
                isDragging = false;
                panel.style.transition = '';
            }
        });

        function updateConnectionStatus(connected) {
            statusEl.textContent = '●';
            statusEl.style.color = connected ? '#16a34a' : '#dc2626';
            statusEl.title = connected ? 'Connesso' : 'Disconnesso';
        }

        function renderCommands(cmds) {
            console.log('[CommandPanel] Rendering commands:', cmds);
            if (!Array.isArray(cmds)) cmds = [];

            // Filter active commands (queued/running) or recently completed (< 5 min)
            const now = new Date();
            const fiveMinutesAgo = new Date(now.getTime() - 5 * 60 * 1000);

            const visibleCommands = cmds.filter(c => {
                // Support both camelCase (SignalR default) and PascalCase
                const status = (c.status || c.Status || '').toLowerCase();
                const meta = c.metadata || c.Metadata || {};
                const isTransparent = (meta.transparent || meta.Transparent) === '1';
                if (isTransparent && status !== 'failed') {
                    return false;
                }
                if (status === 'queued' || status === 'running') return true;
                if (status === 'completed' || status === 'failed' || status === 'cancelled') {
                    const completedAtStr = c.completedAt || c.CompletedAt;
                    const completedAt = completedAtStr ? new Date(completedAtStr) : null;
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
                // Support both camelCase (SignalR default) and PascalCase
                const status = (c.status || c.Status || '').toLowerCase();
                let bgColor = '#fff3cd'; // queued
                let borderColor = '#ffc107';
                let statusIcon = '<i class="bi bi-hourglass-split me-1"></i>';

                if (status === 'running') {
                    bgColor = '#cce5ff';
                    borderColor = '#007bff';
                    statusIcon = '<i class="bi bi-play-fill me-1"></i>';
                } else if (status === 'completed') {
                    bgColor = '#d4edda';
                    borderColor = '#28a745';
                    statusIcon = '<i class="bi bi-check-circle-fill me-1"></i>';
                } else if (status === 'failed') {
                    bgColor = '#f8d7da';
                    borderColor = '#dc3545';
                    statusIcon = '<i class="bi bi-x-circle-fill me-1"></i>';
                } else if (status === 'cancelled') {
                    bgColor = '#e2e3e5';
                    borderColor = '#6c757d';
                    statusIcon = '<i class="bi bi-slash-circle me-1"></i>';
                }

                const currentStep = c.currentStep || c.CurrentStep;
                const maxStep = c.maxStep || c.MaxStep;
                const stepDesc = c.stepDescription || c.StepDescription || '';
                const retryCount = c.retryCount || c.RetryCount || 0;
                const retryInfo = (retryCount > 0) ? ` (Retry ${retryCount})` : '';
                const metadata = c.metadata || c.Metadata || {};
                const op = metadata.operation || metadata.Operation || c.operationName || c.OperationName || c.threadScope || c.ThreadScope || 'N/A';
                const opLower = (op || '').toString().toLowerCase();
                const isScoreCommand = opLower.includes('instruction_score')
                    || opLower.includes('json_score')
                    || opLower.includes('intelligence_score')
                    || opLower.includes('intelligence_test');
                const isIntelligenceTestCommand = opLower.includes('intelligence_test') || opLower.includes('intelligence_score');
                const hasStep = currentStep !== undefined && currentStep !== null && maxStep !== undefined && maxStep !== null;
                const scoreMatch = /(?:score\s*parziale|parziale)\s+(\d+\/10)/i.exec(stepDesc);
                const runningScore = scoreMatch ? scoreMatch[1] : '';
                const stepInfo = stepDesc
                    ? ` (${stepDesc})`
                    : (hasStep ? ` ${isScoreCommand ? `Test ${currentStep}/${maxStep}` : `Step ${currentStep}/${maxStep}`}` : '');
                const agent = c.agentName || c.AgentName || metadata.agentName || metadata.AgentName || metadata.agentRole || metadata.AgentRole || 'N/A';
                const statusDisplay = c.status || c.Status || '?';
                const runId = c.runId || c.RunId || '';

                let modelShort = c.modelName || c.ModelName || metadata.modelName || metadata.ModelName || '';
                if (modelShort && modelShort.includes('/')) {
                    modelShort = modelShort.substring(modelShort.lastIndexOf('/') + 1);
                }
                if (modelShort && modelShort.includes(':')) {
                    modelShort = modelShort.substring(0, modelShort.indexOf(':'));
                }

                // Extract storyId from metadata if present
                const storyId = metadata.storyId || metadata.StoryId;
                const storyIdBadge = storyId ? ` <span style="
                    background: rgba(0,0,0,0.1);
                    padding: 1px 6px;
                    border-radius: 3px;
                    font-size: 10px;
                    font-weight: normal;
                " title="ID Storia"><i class="bi bi-journal-text me-1"></i>${storyId}</span>` : '';

                const errorMessage = c.errorMessage || c.ErrorMessage;
                const errorMsg = errorMessage ? `
                    <div style="color:#dc3545; font-size:11px; margin-top:6px; user-select: text; cursor: text;">
                        
                        <div style="display:flex; justify-content:space-between; align-items:flex-start; gap:6px;">
                        
                            <div style="flex:1;"><i class="bi bi-exclamation-triangle-fill me-1"></i>${escapeHtml(errorMessage)}</div>
                        
                        
                            <button onclick="copyToClipboard('${escapeHtml(errorMessage).replace(/'/g, "\\'")}', event)" style="
                        
                                background: #dc3545; color: #fff; border: none; padding: 2px 6px; 
                        
                                border-radius: 3px; font-size: 10px; cursor: pointer; white-space: nowrap;
                        
                                flex-shrink: 0; padding-top: 0px; padding-bottom: 0px; line-height: 1.2;
                        
                            "><i class="bi bi-clipboard me-1"></i>Copia</button>
                        
                        </div>
                    </div>
                ` : '';

                const taskName = metadata.taskName || metadata.TaskName || '';
                const failureKind = metadata.failureKind || metadata.FailureKind || '';
                const detail = metadata.detail || metadata.Detail || metadata.message || metadata.Message || '';
                const filter = metadata.filter || metadata.Filter || '';
                const candidateCount = metadata.candidateCount || metadata.CandidateCount || '';
                const storyTitle = metadata.storyTitle || metadata.StoryTitle || '';
                const autoInfoParts = [];
                if (taskName) autoInfoParts.push(`Task: ${taskName}`);
                if (failureKind) autoInfoParts.push(`Motivo: ${failureKind}`);
                if (detail) autoInfoParts.push(`Dettagli: ${detail}`);
                if (filter) autoInfoParts.push(`Filtro: ${filter}`);
                if (candidateCount) autoInfoParts.push(`Candidati: ${candidateCount}`);
                if (storyTitle) autoInfoParts.push(`Titolo: ${storyTitle}`);
                const autoInfo = autoInfoParts.length
                    ? `<div style="font-size:11px; color:#444; margin-top:4px; user-select:text;">${escapeHtml(autoInfoParts.join(' | '))}</div>`
                    : '';

                const progressParts = [];
                if (modelShort) progressParts.push(`<i class="bi bi-cpu me-1"></i>${modelShort}`);
                if (hasStep) {
                    progressParts.push(`${isIntelligenceTestCommand ? 'Domanda' : (isScoreCommand ? 'Test' : 'Step')} ${currentStep}/${maxStep}`);
                }
                if (isScoreCommand && runningScore) {
                    progressParts.push(`score running ${runningScore}`);
                } else if (stepDesc) {
                    progressParts.push(stepDesc);
                }
                const progressInfo = progressParts.length
                    ? `<div style="font-size:11px; color:#111; margin-top:4px; user-select:text;">${progressParts.join(' • ')}</div>`
                    : '';


                const canCancel = (status === 'queued' || status === 'running') && runId;
                const cancelButton = canCancel ? `
                    <button onclick="cancelCommand('${runId.replace(/'/g, "\\'")}', event)" style="
                        
                        background: #dc3545; color: #fff; border: none; padding: 2px 6px;
                        
                        border-radius: 3px; font-size: 10px; cursor: pointer; white-space: nowrap;
                        
                        line-height: 1.2;
                    ">Annulla</button>
                ` : '';

                return `
                    <div style="
                        background: ${bgColor};
                        border-left: 4px solid ${borderColor};
                        border-radius: 6px;
                        padding: 8px 10px;
                        margin-bottom: 6px;
                        user-select: text;
                    ">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <strong>${statusIcon} ${op}${storyIdBadge}</strong>
                            <div style="display:flex; align-items:center; gap:6px;">
                                <span style="font-size:11px; opacity:0.8;">${statusDisplay}${stepInfo}${retryInfo}</span>
                                ${cancelButton}
                            </div>
                        </div>
                        <div style="font-size:11px; color:#555; margin-top:4px;">
                            <span><i class="bi bi-person-fill me-1"></i>${agent || 'N/A'}</span>
                            ${modelShort ? `<span class="ms-2"><i class="bi bi-cpu me-1"></i>${modelShort}</span>` : ''}
                        </div>
                        ${progressInfo}
                        ${autoInfo}
                        ${errorMsg}
                    </div>
                `;
            }).join('');
        }

        async function ensureConnection() {
            if (isConnecting) return;
            if (connection && connection.state === signalR.HubConnectionState.Connected) {
                updateConnectionStatus(true);
                return;
            }

            isConnecting = true;
            updateConnectionStatus(false);

            try {
                connection = new signalR.HubConnectionBuilder()
                    .withUrl('/progressHub')
                    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
                    .configureLogging(signalR.LogLevel.Warning)
                    .build();

                // The server broadcasts these events to all clients.
                // This panel doesn't use them, but registering no-op handlers
                // avoids noisy "No client method with the name ..." warnings.
                connection.on('ProgressAppended', () => { });
                connection.on('BusyModelsUpdated', () => { });

                connection.on('CommandListUpdated', cmds => {
                    console.log('[CommandPanel] CommandListUpdated received');
                    renderCommands(cmds);
                });

                connection.on('StepProgress', (genId, current, max, desc) => {
                    // StepProgress usually triggers CommandListUpdated from server
                    console.log('[CommandPanel] StepProgress:', genId, current, max);
                });

                connection.onreconnecting(() => {
                    console.log('[CommandPanel] Reconnecting...');
                    updateConnectionStatus(false);
                });

                connection.onreconnected(() => {
                    console.log('[CommandPanel] Reconnected');
                    updateConnectionStatus(true);
                    reconnectAttempts = 0;
                });

                connection.onclose(() => {
                    console.log('[CommandPanel] Connection closed');
                    updateConnectionStatus(false);
                    // Try to reconnect after a delay
                    if (reconnectAttempts < 5) {
                        
                        reconnectAttempts++;
                        
                        setTimeout(() => {
                        
                            isConnecting = false;
                        
                            ensureConnection();
                        
                        }, 5000 * reconnectAttempts);
                    }
                });

                await connection.start();
                console.log('[CommandPanel] SignalR connected');
                updateConnectionStatus(true);
                reconnectAttempts = 0;

                // Server sends initial snapshot in OnConnectedAsync
            } catch (e) {
                console.error('[CommandPanel] SignalR connect error:', e);
                updateConnectionStatus(false);
                // Retry
                if (reconnectAttempts < 5) {
                    reconnectAttempts++;
                    setTimeout(() => {
                        
                        isConnecting = false;
                        
                        ensureConnection();
                    }, 3000 * reconnectAttempts);
                }
            } finally {
                isConnecting = false;
            }
        }

        // Start connection when DOM is ready
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => ensureConnection());
        } else {
            ensureConnection();
        }

        // Also try to reconnect if page becomes visible again
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden) {
                ensureConnection();
            }
        });

        // Periodic polling fallback: fetch command list every 10 seconds as backup
        async function pollCommands() {
            try {
                const resp = await fetch('/api/commands');
                if (resp.ok) {
                    const cmds = await resp.json();
                    renderCommands(cmds);
                }
            } catch (e) {
                console.log('[CommandPanel] Poll fallback error:', e);
            }
        }
        setInterval(pollCommands, 10000);
    }
})();





