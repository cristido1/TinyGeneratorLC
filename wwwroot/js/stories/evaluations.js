(function(window){
    function initEvaluations() {
        document.addEventListener('click', function(e){
            if (e.target && e.target.classList.contains('manual-eval-btn')) {
                const storyId = e.target.dataset.storyId;
                const score = prompt('Punteggio totale (0-100):');
                if (!score) return;
                const overall = prompt('Commento (opzionale):');
                const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
                const form = document.createElement('form');
                form.method = 'post'; form.action = '?handler=ManualEvaluate';
                const idInp = document.createElement('input'); idInp.type='hidden'; idInp.name='id'; idInp.value=storyId; form.appendChild(idInp);
                const sc = document.createElement('input'); sc.type='hidden'; sc.name='score'; sc.value=score; form.appendChild(sc);
                const ov = document.createElement('input'); ov.type='hidden'; ov.name='overall'; ov.value=overall || ''; form.appendChild(ov);
                if (token) { const t = document.createElement('input'); t.type='hidden'; t.name='__RequestVerificationToken'; t.value=token; form.appendChild(t); }
                document.body.appendChild(form); form.submit();
            }

            if (e.target && e.target.classList.contains('delete-eval-btn')) {
                e.preventDefault();
                if (!confirm('Eliminare la valutazione?')) return;
                const evalId = e.target.dataset.evalId; const storyId = e.target.dataset.storyId;
                const fd = new FormData(); fd.append('id', evalId); fd.append('storyId', storyId); fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]')?.value || '');
                fetch('?handler=DeleteEvaluation', { method: 'POST', body: fd }).then(r => { if (r.redirected) window.location = r.url; else window.location.reload(); });
            }
        });
    }

    window.StoriesApp = window.StoriesApp || {};
    window.StoriesApp.initEvaluations = initEvaluations;
})(window);
