// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
document.addEventListener('DOMContentLoaded', function () {
	const toggle = document.querySelector('.nav-toggle');
	const body = document.body;
	const overlay = document.querySelector('.sidebar-overlay');
	const sidebar = document.querySelector('.sidebar');
    const collapseBtn = document.querySelector('.sidebar-collapse');
    const COLLAPSE_KEY = 'tg_sidebar_collapsed';

	function openSidebar() {
		body.classList.add('sidebar-open');
		if (overlay) overlay.classList.add('visible');
		if (toggle) toggle.setAttribute('aria-expanded', 'true');
	}

	function closeSidebar() {
		body.classList.remove('sidebar-open');
		if (overlay) overlay.classList.remove('visible');
		if (toggle) toggle.setAttribute('aria-expanded', 'false');
	}

	if (toggle) {
		toggle.addEventListener('click', function (e) {
			e.preventDefault();
			if (body.classList.contains('sidebar-open')) closeSidebar(); else openSidebar();
		});
	}

	if (overlay) {
		overlay.addEventListener('click', function () { closeSidebar(); });
	}

	// collapse behaviour for desktop: toggle collapsed class and persist
	function setCollapsedState(collapsed) {
		if (!sidebar) return;
		if (collapsed) {
			sidebar.classList.add('collapsed');
			body.classList.add('sidebar-collapsed');
			if (collapseBtn) collapseBtn.setAttribute('aria-pressed', 'true');
		} else {
			sidebar.classList.remove('collapsed');
			body.classList.remove('sidebar-collapsed');
			if (collapseBtn) collapseBtn.setAttribute('aria-pressed', 'false');
		}
		try { localStorage.setItem(COLLAPSE_KEY, collapsed ? '1' : '0'); } catch (e) { }
	}

	if (collapseBtn) {
		collapseBtn.addEventListener('click', function (e) {
			e.preventDefault();
			const isCollapsed = sidebar && sidebar.classList.contains('collapsed');
			setCollapsedState(!isCollapsed);
		});
	}

	// initialize from localStorage
	try {
		const saved = localStorage.getItem(COLLAPSE_KEY);
		if (saved === '1') setCollapsedState(true);
	} catch (e) { }
});
