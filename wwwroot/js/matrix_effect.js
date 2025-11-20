/**
 * Matrix Rain Effect
 * Effetto "pioggia" in stile Matrix con caratteri verdi che cadono
 */

class MatrixRain {
    constructor(canvasId = 'matrix-canvas') {
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) {
            console.error(`Canvas with id "${canvasId}" not found`);
            return;
        }
        
        this.ctx = this.canvas.getContext('2d');
        this.characters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789@#$%^&*()_+-=[]{}|;:,.<>?';
        this.fontSize = 14;
        this.drops = [];
        
        // Effetto testo "cristiano donaggio"
        this.scrollingTextActive = false;
        this.scrollingTextY = 0;
        this.scrollingTextX = 0;
        this.scrollingTextSpeed = 2;
        
        this.init();
    }
    
    init() {
        // Imposta le dimensioni del canvas
        this.resizeCanvas();
        
        // Inizializza le gocce
        this.initDrops();
        
        // Avvia l'animazione
        this.animate();
        
        // Gestisce il ridimensionamento della finestra
        window.addEventListener('resize', () => this.handleResize());
        
        // Avvia il testo scorrevole ogni minuto (60 secondi)
        setInterval(() => this.startScrollingText(), 60000);
    }
    
    resizeCanvas() {
        this.canvas.width = window.innerWidth;
        this.canvas.height = window.innerHeight;
        this.columns = Math.floor(this.canvas.width / this.fontSize);
    }
    
    initDrops() {
        this.drops = [];
        for (let i = 0; i < this.columns; i++) {
            this.drops[i] = Math.random() * -100;
        }
    }
    
    draw() {
        // Sfondo semi-trasparente nero per effetto scia
        this.ctx.fillStyle = 'rgba(0, 0, 0, 0.05)';
        this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);
        
        // Testo verde
        this.ctx.fillStyle = '#0F0';
        this.ctx.font = this.fontSize + 'px monospace';
        
        // Disegna i caratteri
        for (let i = 0; i < this.drops.length; i++) {
            const text = this.characters[Math.floor(Math.random() * this.characters.length)];
            this.ctx.fillText(text, i * this.fontSize, this.drops[i] * this.fontSize);
            
            // Reset casuale della goccia
            if (this.drops[i] * this.fontSize > this.canvas.height && Math.random() > 0.975) {
                this.drops[i] = 0;
            }
            
            this.drops[i]++;
        }
        
        // Disegna il testo scorrevole se attivo
        if (this.scrollingTextActive) {
            this.drawScrollingText();
        }
    }
    
    drawScrollingText() {
        const text = 'cristiano donaggio';
        const fontSize = 32;
        this.ctx.font = `bold ${fontSize}px monospace`;
        this.ctx.fillStyle = 'rgba(255, 128, 0, 0.8)'; // Arancione acceso
        this.ctx.shadowColor = 'rgba(255, 128, 0, 0.6)';
        this.ctx.shadowBlur = 10;
        this.ctx.shadowOffsetX = 0;
        this.ctx.shadowOffsetY = 0;
        
        // Calcola la posizione del testo al centro orizzontale
        const textWidth = this.ctx.measureText(text).width;
        const x = (this.canvas.width - textWidth) / 2;
        
        // Disegna il testo
        this.ctx.fillText(text, x, this.scrollingTextY);
        
        // Incrementa la posizione verticale
        this.scrollingTextY += this.scrollingTextSpeed;
        
        // Ferma l'animazione quando il testo esce dallo schermo
        if (this.scrollingTextY > this.canvas.height) {
            this.scrollingTextActive = false;
            this.ctx.shadowColor = 'rgba(0, 0, 0, 0)';
            this.ctx.shadowBlur = 0;
        }
    }
    
    startScrollingText() {
        this.scrollingTextActive = true;
        this.scrollingTextY = -50; // Inizia sopra lo schermo
    }
    
    animate() {
        this.draw();
        requestAnimationFrame(() => this.animate());
    }
    
    handleResize() {
        this.resizeCanvas();
        this.initDrops();
    }
    
    // Metodi pubblici per controllo
    setFontSize(size) {
        this.fontSize = size;
        this.resizeCanvas();
        this.initDrops();
    }
    
    setCharacters(chars) {
        this.characters = chars;
    }
    
    setColor(color) {
        this.ctx.fillStyle = color;
    }
}

// Inizializzazione automatica quando il DOM è pronto
document.addEventListener('DOMContentLoaded', function() {
    // Crea l'istanza dell'effetto Matrix
    const matrixEffect = new MatrixRain('matrix-canvas');
    
    // Opzionale: esponi l'istanza globalmente per controllo esterno
    window.matrixEffect = matrixEffect;
});