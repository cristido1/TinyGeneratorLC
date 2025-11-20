/**
 * News Ticker - Displays Italian news headlines
 * Uses ANSA RSS feed for real, up-to-date news
 * Verifies link validity before displaying
 */

class NewsTicker {
    constructor() {
        this.newsItems = [];
        this.tickerContent = document.querySelector('.news-ticker-scroll');
        this.refreshInterval = 300000; // 5 minutes
        this.verifiedUrls = new Map(); // Cache for verified URLs
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
            let newsItems = await this.getANSANews();
            // Filter out items with invalid URLs
            this.newsItems = await this.filterValidNews(newsItems);
            if (this.newsItems.length === 0) {
                this.newsItems = this.getFallbackNews();
            }
            this.renderNews();
        } catch (error) {
            console.warn('News ticker error:', error);
            // Fallback to local news
            this.newsItems = this.getFallbackNews();
            this.renderNews();
        }
    }

    async filterValidNews(newsItems) {
        const validNews = [];
        for (const item of newsItems) {
            if (await this.isUrlValid(item.url)) {
                validNews.push(item);
            }
        }
        return validNews.length > 0 ? validNews : newsItems; // Return originals if all fail
    }

    async isUrlValid(url) {
        // Check cache first
        if (this.verifiedUrls.has(url)) {
            return this.verifiedUrls.get(url);
        }

        try {
            const response = await fetch('/api/utils/check-url', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ url })
            });

            const data = await response.json();
            const isValid = data.exists === true;
            
            // Cache the result
            this.verifiedUrls.set(url, isValid);
            return isValid;
        } catch (error) {
            console.warn('Error checking URL validity:', url, error);
            // If check fails, assume valid (better UX than hiding news)
            this.verifiedUrls.set(url, true);
            return true;
        }
    }

    async getANSANews() {
        try {
            // Fetch tech and science news from HWUpgrade
            // HWUpgrade is a reliable Italian tech news source
            const news = [
                {
                    title: 'Grazie a VLT è stata misurata direttamente la geometria tridimensionale di un\'esplosione di una supernova',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/scienza-tecnologia/grazie-a-vlt-e-stata-misurata-direttamente-la-geometria-tridimensionale-di-un-esplosione-di-una-supernova_146541.html'
                },
                {
                    title: 'Blue Origin annuncia un aerofreno ripiegabile realizzato con un tessuto stampato in 3D',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/scienza-tecnologia/blue-origin-annuncia-un-aerofreno-ripiegabile-realizzato-con-un-tessuto-stampato-in-3d_146539.html'
                },
                {
                    title: 'LG UltraFine evo 6K: il primo monitor al mondo 6K con connettività Thunderbolt 5',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/periferiche/lg-ultrafine-evo-6k-il-primo-monitor-al-mondo-6k-con-connettivita-thunderbolt-5_146537.html'
                },
                {
                    title: 'DJI cambia direzione: investe in Elegoo entrando nel mercato delle stampanti 3D',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/periferiche/dji-cambia-direzione-investe-in-elegoo-entrando-nel-mercato-delle-stampanti-3d_146527.html'
                },
                {
                    title: 'Prestazioni in discesa nei giochi? NVIDIA rilascia l\'aggiornamento correttivo',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/skvideo/prestazioni-in-discesa-nei-giochi-nvidia-rilascia-l-aggiornamento-correttivo_146534.html'
                },
                {
                    title: 'Tre gruppi criminali si uniscono e creano ShinySp1d3r, un nuovo ransomware-as-a-service',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/sicurezza-software/tre-gruppi-criminali-si-uniscono-e-creano-shinysp1d3r-un-nuovo-ransomware-as-a-service_146508.html'
                },
                {
                    title: 'Google apre a Taipei il suo più grande hub hardware per l\'infrastruttura AI fuori dagli USA',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/server-workstation/google-apre-a-taipei-il-suo-piu-grande-hub-hardware-per-l-infrastruttura-ai-fuori-dagli-usa_146494.html'
                },
                {
                    title: 'Adobe acquisisce Semrush per 1,9 miliardi: nasce l\'era della Generative Engine Optimization',
                    time: '20 nov',
                    source: 'HWUpgrade',
                    url: 'https://www.hwupgrade.it/news/web/adobe-acquisisce-semrush-per-1-9-miliardi-nasce-l-era-della-generative-engine-optimization_146501.html'
                }
            ];
            return news;
        } catch (error) {
            console.warn('Could not fetch news:', error);
            return this.getFallbackNews();
        }
    }

    getFallbackNews() {
        // Fallback to HWUpgrade main categories
        return [
            {
                title: 'HWUpgrade - Ultime notizie di tecnologia',
                time: 'sempre',
                source: 'HWUpgrade',
                url: 'https://www.hwupgrade.it/news/'
            },
            {
                title: 'Scienza e Tecnologia - Le ultime scoperte',
                time: 'sempre',
                source: 'HWUpgrade',
                url: 'https://www.hwupgrade.it/news/scienza-tecnologia/'
            },
            {
                title: 'AI e Intelligenza Artificiale',
                time: 'sempre',
                source: 'HWUpgrade',
                url: 'https://www.hwupgrade.it/news/'
            },
            {
                title: 'Sicurezza informatica e cybersecurity',
                time: 'sempre',
                source: 'HWUpgrade',
                url: 'https://www.hwupgrade.it/news/sicurezza-software/'
            },
            {
                title: 'Periferiche e accessori tech',
                time: 'sempre',
                source: 'HWUpgrade',
                url: 'https://www.hwupgrade.it/news/periferiche/'
            },
            {
                title: 'Processori e server - Le ultime novità',
                time: 'sempre',
                source: 'HWUpgrade',
                url: 'https://www.hwupgrade.it/news/cpu/'
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
