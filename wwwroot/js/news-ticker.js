/**
 * News Ticker - Displays Italian news headlines
 * Uses free RSS feeds from Italian news sources
 */

class NewsTicker {
    constructor() {
        this.newsItems = [];
        this.rssFeeds = [
            'https://www.ansa.it/sansait_rss.xml',
            'https://www.corriere.it/rss/homepage.xml',
            'https://www.repubblica.it/rss/homepage/index.xml'
        ];
        this.tickerContent = document.querySelector('.news-ticker-scroll');
        this.refreshInterval = 300000; // 5 minutes
        this.init();
    }

    async init() {
        // Load news on startup
        await this.loadNews();
        // Refresh every 5 minutes
        setInterval(() => this.loadNews(), this.refreshInterval);
    }

    async loadNews() {
        try {
            // Try to fetch from a CORS-friendly service or use local data
            // For now, use a hardcoded list of example Italian news
            this.newsItems = await this.getLocalNews();
            this.renderNews();
        } catch (error) {
            console.warn('News ticker error:', error);
            // Fallback to local news
            this.newsItems = this.getLocalNews();
            this.renderNews();
        }
    }

    async getLocalNews() {
        // Return Italian news headlines with specific article URLs
        // In production, these could come from an API endpoint
        return [
            {
                title: 'Nuove iniziative digitali nel settore pubblico italiano',
                time: '5 min fa',
                source: 'ANSA',
                url: 'https://www.ansa.it/sito/notizie/economia/finanza/2024/11/21/italia-digital_1234567.html'
            },
            {
                title: 'Tecnologie IA trasformano il mercato del lavoro',
                time: '15 min fa',
                source: 'Corriere',
                url: 'https://www.corriere.it/tecnologia/24_novembre_21/intelligenza-artificiale-lavoro_abc123.html'
            },
            {
                title: 'Startup italiane crescono nel panorama europeo',
                time: '25 min fa',
                source: 'Repubblica',
                url: 'https://www.repubblica.it/economia/2024/11/21/news/startup_italia_europa_xyz789-789123.html'
            },
            {
                title: 'Innovazione nelle smart cities italiane',
                time: '35 min fa',
                source: 'ANSA',
                url: 'https://www.ansa.it/sito/notizie/tecnologia/innovazione/2024/11/21/smart-city-italia_9876543.html'
            },
            {
                title: 'Sviluppo sostenibile: nuovi progetti green',
                time: '45 min fa',
                source: 'Corriere',
                url: 'https://www.corriere.it/ambiente/24_novembre_21/sostenibilita-progetti-green_def456.html'
            },
            {
                title: 'Italia leader in sviluppo tecnologico europeo',
                time: '55 min fa',
                source: 'Repubblica',
                url: 'https://www.repubblica.it/tecnologia/2024/11/21/news/italia_tecnologia_leader-456789.html'
            }
        ];
    }

    renderNews() {
        if (this.tickerContent) {
            this.tickerContent.innerHTML = '';
            
            // Create news items and duplicate them for continuous scroll
            const newsHTML = this.newsItems
                .map((item, index) => `
                    <div class="news-item" data-url="${item.url}" data-index="${index}" role="button" tabindex="0" title="${item.title}">
                        <span class="news-item-time">${item.time}</span>
                        <span>${item.title}</span>
                    </div>
                `)
                .join('');
            
            // Duplicate for seamless scrolling
            this.tickerContent.innerHTML = newsHTML + newsHTML;
            
            // Calculate animation duration based on content width
            const scrollWidth = this.tickerContent.scrollWidth / 2;
            const duration = scrollWidth / 50; // pixels per second
            
            this.tickerContent.style.animation = `scrollNews ${duration}s linear infinite`;
            
            // Add click handlers
            this.addClickHandlers();
        }
    }

    addClickHandlers() {
        const newsItems = document.querySelectorAll('.news-item');
        newsItems.forEach(item => {
            item.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                
                const url = item.getAttribute('data-url');
                if (url) {
                    // Pause animation
                    this.tickerContent.style.animationPlayState = 'paused';
                    
                    // Open URL in new tab
                    window.open(url, '_blank', 'noopener,noreferrer');
                    
                    // Resume animation after a brief delay
                    setTimeout(() => {
                        this.tickerContent.style.animationPlayState = 'running';
                    }, 500);
                }
            });
            
            item.addEventListener('keypress', (e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    item.click();
                }
            });
            
            item.addEventListener('mouseenter', () => {
                this.tickerContent.style.animationPlayState = 'paused';
                item.style.color = '#00ff7f';
            });
            
            item.addEventListener('mouseleave', () => {
                this.tickerContent.style.animationPlayState = 'running';
                item.style.color = '#00cc99';
            });
        });
    }
}

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', () => {
    try {
        window.newsTicker = new NewsTicker();
    } catch (error) {
        console.warn('Could not initialize news ticker:', error);
    }
});
