// Space background animation: stars twinkle and occasional spaceship flyby
// Designed to be lightweight and run on a full-screen canvas behind the layout

(function () {
    const id = 'space-canvas';
    const canvas = document.getElementById(id);
    if (!canvas) return;

    const ctx = canvas.getContext('2d');
    let width = 0, height = 0;

    function resize() {
        const ratio = window.devicePixelRatio || 1;
        width = window.innerWidth;
        height = window.innerHeight;
        canvas.width = Math.floor(width * ratio);
        canvas.height = Math.floor(height * ratio);
        canvas.style.width = width + 'px';
        canvas.style.height = height + 'px';
        ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    }

    // Star model
    class Star {
        constructor() {
            this.reset();
        }

        reset() {
            this.x = Math.random() * width;
            this.y = Math.random() * height;
            this.z = Math.random() * 1.3 + 0.1; // size modifier
            this.baseAlpha = 0.25 + Math.random() * 0.75;
            this.twinkleFreq = 0.5 + Math.random() * 3; // speed of twinkle
            this.phase = Math.random() * Math.PI * 2;
        }

        draw(t) {
            const a = this.baseAlpha * (0.6 + 0.4 * Math.sin(this.phase + t * this.twinkleFreq));
            const r = Math.max(0.4, (this.z * 1.6));
            ctx.beginPath();
            const g = ctx.createRadialGradient(this.x, this.y, 0, this.x, this.y, r * 6);
            g.addColorStop(0, `rgba(255,255,255,${Math.min(1,a)})`);
            g.addColorStop(0.3, `rgba(200,220,255,${Math.min(0.6,a)})`);
            g.addColorStop(1, `rgba(120,140,160,${Math.min(0.02,a)})`);
            ctx.fillStyle = g;
            ctx.arc(this.x, this.y, r * 3, 0, Math.PI * 2);
            ctx.fill();
        }
    }

    // Spaceship model
    class Ship {
        constructor() {
            this.reset(true);
        }

        reset(initial = false) {
            this.direction = Math.random() > 0.5 ? 1 : -1; // left-to-right or right-to-left
            this.y = 60 + Math.random() * (height - 120);
            if (this.direction === 1) {
                this.x = initial ? -200 - Math.random()*400 : -200;
                this.vx = 100 + Math.random() * 200; // px/s
            } else {
                this.x = initial ? width + 200 + Math.random()*400 : width + 200;
                this.vx = -(100 + Math.random() * 200);
            }
            this.size = 26 + Math.random() * 42;
            this.rotation = this.direction === 1 ? 0.05 + Math.random()*0.15 : -0.05 - Math.random()*0.15;
            this.tailPulse = 0;
            this.ttl = 6 + Math.random() * 6; // lifespan seconds
            this.elapsed = 0;
        }

        update(dt) {
            this.x += this.vx * dt;
            this.elapsed += dt;
            this.tailPulse = Math.sin(this.elapsed * 30) * 0.6 + 0.8;
        }

        draw() {
            ctx.save();
            ctx.translate(this.x, this.y);
            ctx.rotate(this.rotation);

            // body glow
            let g = ctx.createLinearGradient(-this.size, -this.size, this.size, this.size);
            g.addColorStop(0, 'rgba(160,220,255,0.07)');
            g.addColorStop(1, 'rgba(120,160,255,0.15)');
            ctx.fillStyle = g;
            ctx.beginPath();
            ctx.ellipse(0, 0, this.size * 1.6, this.size * 0.9, 0, 0, Math.PI*2);
            ctx.fill();

            // hull
            ctx.fillStyle = 'rgba(200,230,255,0.95)';
            ctx.beginPath();
            ctx.moveTo(-this.size*0.9, 0);
            ctx.quadraticCurveTo(-this.size*0.1, -this.size*0.6, this.size*0.9, 0);
            ctx.quadraticCurveTo(-this.size*0.1, this.size*0.6, -this.size*0.9, 0);
            ctx.fill();

            // cockpit
            ctx.fillStyle = 'rgba(20,40,80,0.95)';
            ctx.beginPath();
            ctx.ellipse(this.size*0.25, -this.size*0.15, this.size*0.24, this.size*0.14, -0.3, 0, Math.PI*2);
            ctx.fill();

            // tail flame
            ctx.beginPath();
            const tailW = this.size * 0.7 * this.tailPulse;
            const tailH = this.size * 0.25 * this.tailPulse;
            const sx = -this.size*0.9;
            ctx.moveTo(sx, -tailH);
            ctx.quadraticCurveTo(sx - tailW*0.5, 0, sx, tailH);
            ctx.closePath();
            const flame = ctx.createLinearGradient(sx - tailW, 0, sx, 0);
            flame.addColorStop(0, 'rgba(255,200,40,0.0)');
            flame.addColorStop(0.3, 'rgba(255,140,40,0.25)');
            flame.addColorStop(1, 'rgba(255,100,30,0.9)');
            ctx.fillStyle = flame;
            ctx.fill();

            ctx.restore();
        }

        isOffscreen() {
            return this.direction === 1 ? (this.x - this.size > width + 200) : (this.x + this.size < -200);
        }
    }

    // Setup
    let stars = [];
    let ships = [];
    let last = performance.now();

    function init() {
        resize();
        // generate stars density depending on size
        const nStars = Math.min(1200, Math.max(100, Math.floor((width * height) / 2000)));
        stars = new Array(nStars).fill(0).map(() => new Star());

        // add a ship with low probability at start
        if (Math.random() > 0.9) ships.push(new Ship());
    }

    let shipCooldown = 0;

    function loop(now) {
        const dt = Math.min(0.1, (now - last) / 1000);
        last = now;

        // subtle background fade
        ctx.clearRect(0, 0, width, height);
        // dark nebula soft overlay
        const neb = ctx.createLinearGradient(0, 0, width, height);
        neb.addColorStop(0, 'rgba(1, 3, 9, 0.6)');
        neb.addColorStop(0.6, 'rgba(8,10,25,0.6)');
        neb.addColorStop(1, 'rgba(3,5,12,0.6)');
        ctx.fillStyle = neb;
        ctx.fillRect(0, 0, width, height);

        // Draw stars
        const t = now / 1000;
        for (let i = 0; i < stars.length; i++) {
            stars[i].draw(t);
        }

        // Occasionally add ship
        shipCooldown -= dt;
        if (shipCooldown <= 0 && Math.random() < 0.025) {
            ships.push(new Ship());
            shipCooldown = 3 + Math.random() * 8; // wait 3..11s
        }

        // Update and draw ships
        for (let i = ships.length - 1; i >= 0; i--) {
            const s = ships[i];
            s.update(dt);

            // draw motion blur / light streak behind
            ctx.save();
            ctx.globalAlpha = 0.25;
            ctx.strokeStyle = 'rgba(200,230,255,0.06)';
            ctx.lineWidth = s.size * 0.4;
            ctx.beginPath();
            ctx.moveTo(s.x - s.vx * 0.02, s.y);
            ctx.lineTo(s.x + s.vx * 0.01, s.y);
            ctx.stroke();
            ctx.restore();

            s.draw();

            if (s.isOffscreen() || s.elapsed > s.ttl) {
                ships.splice(i, 1);
            }
        }

        requestAnimationFrame(loop);
    }

    // Public init
    init();
    window.addEventListener('resize', () => {
        resize();
        // normalize star positions when resized
        stars.forEach(s => {
            s.x = Math.random() * width;
            s.y = Math.random() * height;
        });
    });

    // Small perf guard: if not visible, pause drawing
    let hidden = false;
    document.addEventListener('visibilitychange', () => { hidden = document.hidden; if (!hidden) last = performance.now(); });

    // Start loop
    requestAnimationFrame(loop);
})();
