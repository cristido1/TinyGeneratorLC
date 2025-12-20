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
                    <span style="flex:1;">⚙️ Comandi in esecuzione / coda</span>
                    <span class="badge" id="cmd-count" style="
                        background: rgba(255,255,255,0.25);
                        padding: 3px 8px;
                        border-radius: 10px;
                        font-size: 12px;
                    ">0</span>
                    <span id="cmd-connection-status" style="margin-left:8px; font-size:10px;">🔴</span>
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
            statusEl.textContent = connected ? '🟢' : '🔴';
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
                let statusIcon = '⏳';

                if (status === 'running') {
                    bgColor = '#cce5ff';
                    borderColor = '#007bff';
                    statusIcon = '▶️';
                } else if (status === 'completed') {
                    bgColor = '#d4edda';
                    borderColor = '#28a745';
                    statusIcon = '✅';
                } else if (status === 'failed') {
                    bgColor = '#f8d7da';
                    borderColor = '#dc3545';
                    statusIcon = '❌';
                } else if (status === 'cancelled') {
                    bgColor = '#e2e3e5';
                    borderColor = '#6c757d';
                    statusIcon = '🚫';
                }

                const currentStep = c.currentStep || c.CurrentStep;
                const maxStep = c.maxStep || c.MaxStep;
                const stepDesc = c.stepDescription || c.StepDescription || '';
                const retryCount = c.retryCount || c.RetryCount || 0;
                const stepInfo = stepDesc ? ` (${stepDesc})` : ((currentStep && maxStep) ? ` Step ${currentStep}/${maxStep}` : '');
                const retryInfo = (retryCount > 0) ? ` (Retry ${retryCount})` : '';
                const agent = c.agentName || c.AgentName || 'N/A';
                const op = c.operationName || c.OperationName || c.threadScope || c.ThreadScope || 'N/A';
                const statusDisplay = c.status || c.Status || '?';

                let modelShort = c.modelName || c.ModelName || '';
                if (modelShort && modelShort.includes('/')) {
                    modelShort = modelShort.substring(modelShort.lastIndexOf('/') + 1);
                }
                if (modelShort && modelShort.includes(':')) {
                    modelShort = modelShort.substring(0, modelShort.indexOf(':'));
                }

                const errorMessage = c.errorMessage || c.ErrorMessage;
                const errorMsg = errorMessage ? `<div style="color:#dc3545; font-size:11px; margin-top:4px;">⚠️ ${errorMessage}</div>` : '';

                return `
                    <div style="
                        background: ${bgColor};
                        border-left: 4px solid ${borderColor};
                        border-radius: 6px;
                        padding: 8px 10px;
                        margin-bottom: 6px;
                    ">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <strong>${statusIcon} ${op}</strong>
                            <span style="font-size:11px; opacity:0.8;">${statusDisplay}${stepInfo}${retryInfo}</span>
                        </div>
                        <div style="font-size:11px; color:#555; margin-top:4px;">
                            👤 ${agent} ${modelShort ? '• 🧠 ' + modelShort : ''}
                        </div>
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
