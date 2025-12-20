// Sidebar toggle functionality
(function() {
    const STORAGE_KEY = 'sidebar-collapsed';
    
    function init() {
        const sidebar = document.querySelector('.app-sidebar');
        const toggleBtn = document.querySelector('.sidebar-toggle');
        const mobileBtn = document.querySelector('.mobile-menu-btn');
        const overlay = document.querySelector('.sidebar-overlay');
        
        if (!sidebar) return;
        
        // Restore collapsed state from localStorage
        const isCollapsed = localStorage.getItem(STORAGE_KEY) === 'true';
        if (isCollapsed) {
            sidebar.classList.add('collapsed');
            document.body.classList.add('sidebar-collapsed');
        }
        
        // Toggle collapsed state
        if (toggleBtn) {
            toggleBtn.addEventListener('click', () => {
                const collapsed = sidebar.classList.toggle('collapsed');
                document.body.classList.toggle('sidebar-collapsed', collapsed);
                localStorage.setItem(STORAGE_KEY, collapsed);
            });
        }
        
        // Mobile menu toggle
        if (mobileBtn) {
            mobileBtn.addEventListener('click', () => {
                sidebar.classList.toggle('mobile-open');
                overlay?.classList.toggle('visible');
            });
        }
        
        // Close mobile menu when clicking overlay
        if (overlay) {
            overlay.addEventListener('click', () => {
                sidebar.classList.remove('mobile-open');
                overlay.classList.remove('visible');
            });
        }
        
        // Highlight active nav item based on current path
        highlightActiveNav();
    }
    
    function highlightActiveNav() {
        const path = window.location.pathname.toLowerCase();
        const links = document.querySelectorAll('.sidebar-nav a');
        
        links.forEach(link => {
            const href = link.getAttribute('href')?.toLowerCase() || '';
            // Exact match or starts with (for nested pages)
            const isActive = path === href || 
                (href !== '/' && path.startsWith(href));
            link.classList.toggle('active', isActive);
        });
    }
    
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
