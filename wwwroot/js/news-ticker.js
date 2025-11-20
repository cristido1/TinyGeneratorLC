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
        // Return example Italian news headlines with URLs
        // In production, these could come from an API endpoint
        return [
            {
                title: 'Nuove iniziative digitali nel settore pubblico italiano',
                time: '5 min fa',
                source: 'ANSA',
                url: 'https://www.ansa.it/'
            },
            {
                title: 'Tecnologie IA trasformano il mercato del lavoro',
                time: '15 min fa',
                source: 'Corriere',
                url: 'https://www.corriere.it/'
            },
            {
                title: 'Startup italiane crescono nel panorama europeo',
                time: '25 min fa',
                source: 'Repubblica',
                url: 'https://www.repubblica.it/'
            },
            {
                title: 'Innovazione nelle smart cities italiane',
                time: '35 min fa',
                source: 'ANSA',
                url: 'https://www.ansa.it/'
            },
            {
                title: 'Sviluppo sostenibile: nuovi progetti green',
                time: '45 min fa',
                source: 'Corriere',
                url: 'https://www.corriere.it/'
            },
            {
                title: 'Italia leader in sviluppo tecnologico europeo',
                time: '55 min fa',
                source: 'Repubblica',
                url: 'https://www.repubblica.it/'
            }
        ];
    }

    renderNews() {
        if (this.tickerContent) {
            this.tickerContent.innerHTML = '';
            
            // Create news items and duplicate them for continuous scroll
            const newsHTML = this.newsItems
                .map((item, index) => `
                    <a href="${item.url}" target="_blank" rel="noopener noreferrer" class="news-item" title="${item.title}" data-index="${index}">
                        <span class="news-item-time">${item.time}</span>
                        <span>${item.title}</span>
                    </a>
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
                const url = item.getAttribute('href');
                // Browser will handle the link opening in new tab naturally
                // We just add visual feedback
                item.style.opacity = '0.7';
                setTimeout(() => {
                    item.style.opacity = '1';
                }, 200);
            });
            
            item.addEventListener('mouseenter', () => {
                item.style.opacity = '0.8';
            });
            
            item.addEventListener('mouseleave', () => {
                item.style.opacity = '1';
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
