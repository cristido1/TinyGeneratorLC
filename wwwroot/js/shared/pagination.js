(function(){
    function buildPager(){
        const pager = document.getElementById('pager');
        if (!pager) return;
        const pageIndex = parseInt(pager.dataset.pageIndex||'1',10);
        const pageSize = parseInt(pager.dataset.pageSize||'25',10);
        const total = parseInt(pager.dataset.totalCount||'0',10);
        const search = pager.dataset.search || '';
        const orderBy = pager.dataset.orderBy || '';
        const totalPages = Math.max(1, Math.ceil(total / pageSize));
        const container = document.createElement('nav');
        container.innerHTML = '';
        const ul = document.createElement('ul'); ul.className='pagination';
        function makeLink(p, label, disabled){
            const li = document.createElement('li'); li.className = 'page-item' + (disabled? ' disabled':'');
            const a = document.createElement('a'); a.className='page-link';
            const qs = new URLSearchParams(); qs.set('page', p); qs.set('pageSize', pageSize); if (search) qs.set('search', search); if (orderBy) qs.set('orderBy', orderBy);
            a.href = '?' + qs.toString(); a.textContent = label; li.appendChild(a); return li;
        }
        ul.appendChild(makeLink(1,'«', pageIndex===1));
        ul.appendChild(makeLink(Math.max(1,pageIndex-1),'‹', pageIndex===1));
        const start = Math.max(1, pageIndex-2); const end = Math.min(totalPages, pageIndex+2);
        for(let p=start;p<=end;p++){ const li = document.createElement('li'); li.className='page-item' + (p===pageIndex? ' active':''); const a=document.createElement('a'); a.className='page-link'; a.href='?'+(new URLSearchParams({page:p,pageSize:pageSize, ...(search?{search}:{}), ...(orderBy?{orderBy}:{}) })).toString(); a.textContent=String(p); li.appendChild(a); ul.appendChild(li);}        
        ul.appendChild(makeLink(Math.min(totalPages,pageIndex+1),'›', pageIndex===totalPages));
        ul.appendChild(makeLink(totalPages,'»', pageIndex===totalPages));
        container.appendChild(ul);
        pager.innerHTML = '';
        pager.appendChild(container);
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', buildPager);
    } else {
        buildPager();
    }
})();
