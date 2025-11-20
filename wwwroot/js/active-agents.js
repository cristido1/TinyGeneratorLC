/**
 * Active Agents Badge Manager
 * Mostra badge volanti per gli agenti attivi
 */

class ActiveAgentsManager {
    constructor() {
        this.container = document.getElementById('active-agents-container');
        this.activeBadges = new Map(); // agentId -> badge element
        
        if (!this.container) {
            console.error('Active agents container not found');
            return;
        }
        
        // Connessione SignalR per ricevere aggiornamenti
        this.setupSignalR();
    }
    
    setupSignalR() {
        if (!window.signalR) {
            console.warn('SignalR not available, agent badges will not work');
            return;
        }
        
        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/progressHub')
            .withAutomaticReconnect()
            .build();
        
        // Ascolta eventi specifici per agenti
        connection.on('AgentActivityStarted', (agentId, agentName, status, testType) => {
            console.log(`Badge START: ${agentId} | ${agentName} | ${status} | ${testType}`);
            this.addOrUpdateBadge(agentId, agentName, status, testType || 'question');
        });
        
        connection.on('AgentActivityEnded', (agentId) => {
            console.log(`Badge END: ${agentId}`);
            this.removeBadge(agentId);
        });
        
        // Fallback: ascolta anche i messaggi di progress generici
        connection.on('ProgressAppended', (id, message, extraClass) => {
            this.handleProgressMessage(id, message, extraClass);
        });
        
        connection.start()
            .then(() => {
                console.log('Active agents manager connected to SignalR');
                console.log('Container element:', this.container);
            })
            .catch(err => {
                console.error('SignalR connection error:', err);
            });
    }
    
    handleProgressMessage(id, message, extraClass) {
        // Identifica se il messaggio indica un agente attivo
        // Formato atteso: "[model] Agent: agentName - status"
        const agentMatch = message.match(/\[([^\]]+)\]\s+Agent:\s+([^-]+)\s+-\s+(.+)/i);
        
        if (agentMatch) {
            const model = agentMatch[1].trim();
            const agentName = agentMatch[2].trim();
            const status = agentMatch[3].trim();
            const agentId = `${model}_${agentName}`;
            
            // Se il messaggio indica completamento, rimuovi il badge
            if (extraClass === 'success' || extraClass === 'error' || 
                status.toLowerCase().includes('completed') || 
                status.toLowerCase().includes('failed')) {
                this.removeBadge(agentId);
            } else {
                // Altrimenti aggiungi o aggiorna il badge
                this.addOrUpdateBadge(agentId, agentName, status);
            }
        }
        
        // Gestisci anche messaggi di completamento generici
        if (extraClass === 'success' || extraClass === 'error') {
            // Cerca badge con ID che contiene questo runId
            this.activeBadges.forEach((badge, badgeId) => {
                if (badgeId.includes(id)) {
                    this.removeBadge(badgeId);
                }
            });
        }
    }
    
    addOrUpdateBadge(agentId, agentName, status, testType = 'question') {
        console.log(`Adding/updating badge: ${agentId} | ${agentName} | ${status} | ${testType}`);
        let badge = this.activeBadges.get(agentId);
        
        // Seleziona icona ed animazione basata sul tipo di test
        let icon, animationClass;
        switch(testType.toLowerCase()) {
            case 'writer':
                icon = '✍️';
                animationClass = 'icon-writing';
                break;
            case 'tts':
                icon = '👄';
                animationClass = 'icon-talking';
                break;
            case 'evaluator':
            case 'evaluation':
                icon = '✓';
                animationClass = 'icon-checking';
                break;
            case 'music':
                icon = '♪';
                animationClass = 'icon-music';
                break;
            case 'functioncall':
                icon = '⚙️';
                animationClass = 'icon-thinking';
                break;
            case 'warmup':
                icon = '🔥';
                animationClass = 'icon-warmup';
                break;
            case 'question':
            default:
                icon = '🧠';
                animationClass = 'icon-thinking';
                break;
        }
        
        if (badge) {
            // Aggiorna lo status del badge esistente
            const statusEl = badge.querySelector('.agent-badge-status');
            if (statusEl) {
                statusEl.textContent = status;
            }
            // Aggiorna icona se il tipo è cambiato
            const iconEl = badge.querySelector('.agent-badge-icon');
            if (iconEl) {
                iconEl.textContent = icon;
                iconEl.className = `agent-badge-icon ${animationClass}`;
            }
        } else {
            // Crea nuovo badge
            badge = document.createElement('div');
            badge.className = 'agent-badge';
            badge.innerHTML = `
                <div class="agent-badge-icon ${animationClass}">${icon}</div>
                <div class="agent-badge-content">
                    <div class="agent-badge-name">${this.escapeHtml(agentName)}</div>
                    <div class="agent-badge-status">${this.escapeHtml(status)}</div>
                </div>
            `;
            
            console.log('Appending badge to container:', badge);
            this.container.appendChild(badge);
            this.activeBadges.set(agentId, badge);
            console.log('Active badges count:', this.activeBadges.size);
        }
    }
    
    removeBadge(agentId) {
        const badge = this.activeBadges.get(agentId);
        if (!badge) return;
        
        // Aggiungi classe di animazione per rimozione
        badge.classList.add('removing');
        
        // Rimuovi dopo l'animazione
        setTimeout(() => {
            if (badge.parentNode) {
                badge.parentNode.removeChild(badge);
            }
            this.activeBadges.delete(agentId);
        }, 300);
    }
    
    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
    
    // API pubblica per uso manuale
    showAgent(agentName, status, agentId = null) {
        const id = agentId || `manual_${Date.now()}`;
        this.addOrUpdateBadge(id, agentName, status);
        return id;
    }
    
    hideAgent(agentId) {
        this.removeBadge(agentId);
    }
    
    clearAll() {
        this.activeBadges.forEach((badge, id) => {
            this.removeBadge(id);
        });
    }
}

// Inizializzazione automatica quando il DOM è pronto
document.addEventListener('DOMContentLoaded', function() {
    window.activeAgentsManager = new ActiveAgentsManager();
});
