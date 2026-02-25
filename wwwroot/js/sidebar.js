// Sidebar toggle functionality
(function() {
    const STORAGE_KEY = 'sidebar-collapsed';
    const SECTION_STORAGE_KEY = 'sidebar-open-section';
    
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

        initSectionAccordion(sidebar);
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

    function initSectionAccordion(sidebar) {
        const sections = Array.from(sidebar.querySelectorAll('.sidebar-section'));
        if (!sections.length) return;

        const setOpenSection = (targetSection) => {
            sections.forEach(section => {
                const isOpen = section === targetSection;
                section.classList.toggle('is-open', isOpen);
                const toggle = section.querySelector('.sidebar-section-toggle');
                if (toggle) {
                    toggle.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
                }
            });

            const key = targetSection?.dataset.sectionKey;
            if (key) {
                localStorage.setItem(SECTION_STORAGE_KEY, key);
            }
        };

        sections.forEach(section => {
            const toggle = section.querySelector('.sidebar-section-toggle');
            if (!toggle) return;

            toggle.addEventListener('click', () => {
                if (section.classList.contains('is-open')) {
                    return;
                }
                setOpenSection(section);
            });
        });

        const activeSection = sections.find(section => section.querySelector('a.active'));
        const savedKey = localStorage.getItem(SECTION_STORAGE_KEY);
        const savedSection = savedKey ? sections.find(s => s.dataset.sectionKey === savedKey) : null;
        setOpenSection(activeSection || savedSection || sections[0]);
    }
    
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
