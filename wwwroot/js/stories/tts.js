(function(window){
    function initTts() {
        document.addEventListener('click', function(e){
            if (e.target && e.target.classList.contains('tts-playlist-btn')) {
                e.preventDefault();
                const storyId = e.target.dataset.storyId;
                fetch(window.location.pathname + '?handler=TtsPlaylist&id=' + storyId)
                    .then(r => r.json())
                    .then(async data => {
                        if (!data?.items?.length) { alert('Nessuna traccia TTS'); return; }

                        // Ensure audio element is visible and has controls
                        const audio = document.getElementById('ttsPlaylistAudio');
                        audio.classList.remove('d-none');
                        audio.controls = true;

                        // Playlist items: url, character, text, durationMs (optional)
                        const items = data.items;

                        // Build cumulative durations (in seconds). If durationMs missing, try to load metadata.
                        let totalSeconds = 0;
                        const durations = [];
                        for (let i = 0; i < items.length; i++) {
                            const it = items[i];
                            if (it.durationMs != null) {
                                const s = Math.max(0, it.durationMs / 1000.0);
                                durations.push(s);
                                totalSeconds += s;
                            } else {
                                // load metadata to get duration (may be async)
                                try {
                                    const metaDur = await probeDuration(items[i].url);
                                    durations.push(metaDur);
                                    totalSeconds += metaDur;
                                } catch (ex) {
                                    // fallback small duration (unknown)
                                    durations.push(0);
                                }
                            }
                        }

                        // Create UI for jump controls if missing
                        let toolbar = document.getElementById('ttsPlaylistToolbar');
                        if (!toolbar) {
                            toolbar = document.createElement('div');
                            toolbar.id = 'ttsPlaylistToolbar';
                            toolbar.className = 'd-flex align-items-center gap-2 my-2';
                            audio.parentNode.insertBefore(toolbar, audio.nextSibling);

                            const backBtn = document.createElement('button'); backBtn.className='btn btn-sm btn-outline-secondary'; backBtn.textContent='-30s';
                            const fwdBtn = document.createElement('button'); fwdBtn.className='btn btn-sm btn-outline-secondary'; fwdBtn.textContent='+30s';
                            const timeLabel = document.createElement('div'); timeLabel.id='ttsPlaylistTimeLabel'; timeLabel.className='ms-2 text-muted';

                            toolbar.appendChild(backBtn);
                            toolbar.appendChild(fwdBtn);
                            toolbar.appendChild(timeLabel);

                            backBtn.addEventListener('click', ()=>{ seekByOffset(-30); });
                            fwdBtn.addEventListener('click', ()=>{ seekByOffset(30); });
                        }

                        // Playback engine: manage idx and offset inside segment
                        let idx = 0;
                        let segmentOffset = 0; // seconds inside current segment
                        let cumulative = [0];
                        for (let i=0;i<durations.length;i++) cumulative.push(cumulative[i]+durations[i]);

                        function indexForGlobalTime(t){
                            // find segment index and offset for global time t (seconds)
                            if (t <= 0) return {i:0, off:0};
                            if (t >= cumulative[cumulative.length-1]) return {i:durations.length-1, off: Math.max(0,durations[durations.length-1]-0.1)};
                            for (let j=0;j<durations.length;j++){
                                if (t >= cumulative[j] && t < cumulative[j+1]) return {i:j, off: t - cumulative[j]};
                            }
                            return {i:0, off:0};
                        }

                        async function playSegmentAt(i, offsetSec) {
                            idx = i;
                            segmentOffset = offsetSec || 0;
                            audio.src = items[idx].url;
                            // Wait metadata to set currentTime
                            audio.pause();
                            audio.removeAttribute('preload');
                            audio.addEventListener('loadedmetadata', function onMeta(){
                                audio.removeEventListener('loadedmetadata', onMeta);
                                if (segmentOffset && audio.duration && segmentOffset < audio.duration - 0.05) {
                                    audio.currentTime = segmentOffset;
                                }
                                audio.play().catch(()=>{});
                            });
                        }

                        // On ended -> advance to next segment
                        audio.onended = () => {
                            if (idx + 1 < items.length) {
                                idx++;
                                segmentOffset = 0;
                                playSegmentAt(idx, 0);
                            }
                        };

                        // Update time label
                        audio.ontimeupdate = () => {
                            const global = cumulative[idx] + (audio.currentTime || 0);
                            const total = cumulative[cumulative.length-1] || 0;
                            const label = document.getElementById('ttsPlaylistTimeLabel');
                            if (label) label.textContent = formatTime(global) + ' / ' + formatTime(total);
                        };

                        // Seek to global time
                        async function seekToGlobal(tSec){
                            const ci = indexForGlobalTime(tSec);
                            await playSegmentAt(ci.i, ci.off);
                        }

                        // Seek by offset seconds relative to current global position
                        async function seekByOffset(deltaSec){
                            const currentGlobal = cumulative[idx] + (audio.currentTime || 0);
                            let target = currentGlobal + deltaSec;
                            target = Math.max(0, Math.min(target, cumulative[cumulative.length-1] || 0));
                            await seekToGlobal(target);
                        }

                        // Expose small helpers to toolbar
                        window._seekToGlobal = seekToGlobal;
                        window._seekByOffset = seekByOffset;

                        // Add click-to-seek on progress bar if browser provides it; otherwise use custom small clickable overlay
                        // For simplicity, add double-click on audio to jump +60s and shift+double-click -60s
                        audio.ondblclick = (ev) => { if (ev.shiftKey) seekByOffset(-60); else seekByOffset(60); };

                        // Kick off playback from start
                        idx = 0; segmentOffset = 0; playSegmentAt(0,0);

                        // helper functions
                        function formatTime(s) {
                            if (!isFinite(s) || s<=0) return '0:00';
                            const h = Math.floor(s/3600); const m = Math.floor((s%3600)/60); const sec = Math.floor(s%60);
                            if (h>0) return `${h}:${String(m).padStart(2,'0')}:${String(sec).padStart(2,'0')}`;
                            return `${m}:${String(sec).padStart(2,'0')}`;
                        }

                        function probeDuration(url){
                            return new Promise((resolve, reject)=>{
                                const a = document.createElement('audio');
                                a.preload = 'metadata';
                                a.src = url;
                                a.addEventListener('loadedmetadata', function(){
                                    resolve(a.duration || 0);
                                });
                                a.addEventListener('error', function(){ reject(new Error('metadata error')); });
                            });
                        }
                    });
            }
        });
    }

    window.StoriesApp = window.StoriesApp || {};
    window.StoriesApp.initTts = initTts;
})(window);
