(function(window){
    function initTts() {
        document.addEventListener('click', function(e){
            if (e.target && e.target.classList.contains('tts-playlist-btn')) {
                e.preventDefault();
                const storyId = e.target.dataset.storyId;
                fetch(window.location.pathname + '?handler=TtsPlaylist&id=' + storyId)
                    .then(r => r.json())
                    .then(data => {
                        if (!data?.items?.length) { alert('Nessuna traccia TTS'); return; }
                        const audio = document.getElementById('ttsPlaylistAudio');
                        let idx = 0;
                        function playNext(){
                            if (idx >= data.items.length) return;
                            audio.src = data.items[idx].url; audio.play().catch(()=>{ idx++; setTimeout(playNext,2000); });
                        }
                        audio.onended = () => { idx++; setTimeout(playNext,2000); };
                        idx = 0; playNext();
                    });
            }
        });
    }

    window.StoriesApp = window.StoriesApp || {};
    window.StoriesApp.initTts = initTts;
})(window);
