- Obiettivo del programma è generare dei file audio lunghi almeno 10 minuti con una storia parlata con una voce per il narratore e una voce per ciascun personaggio, poi vengono aggiunti i rumori ambientali, poi gli effetti sonori, poi la musica nei momenti salienti che non deve superare il 30% della durata dell'audio.
- per fare questo usiamo degli agenti LLM con memoria e validazioni. Policy runtime: output strutturati con TAG tra parentesi quadre; non chiediamo JSON agli agenti.
- Lista agenti
  - 2 Agenti Scrittori con prompt iniziale diverso, possibilmente due modelli diversi
    
    Nota sul flusso di scrittura (multi-pass per storie lunghe): lo scrittore non riesce a produrre una storia lunga in un'unica passata. Procedura consigliata:
    - Decidere genere, sottogenere, stile, ambientazione e scrivere una descrizione breve della storia.
    - Generare una traccia di 6-8 capitoli con il dettaglio sintetico di ciò che deve succedere in ciascun capitolo. Salvare questa traccia.
    - Scrivere il primo capitolo con narrazione e dialoghi, avendo come input l'intera traccia dei capitoli. Salvare il primo capitolo e produrre un breve riassunto di quanto accaduto.
    - Per ogni capitolo successivo: usare la traccia completa + i riassunti dei capitoli precedenti (incluso il riassunto dell'ultimo capitolo scritto) come contesto per generare il capitolo successivo; salvare il capitolo e aggiornare il riassunto cumulativo.
    - Conservare la traccia e i riassunti nella memoria in modo che gli agenti possano riferirsi allo stato corrente della storia durante la generazione.
  - 2 Agenti Valutatori con prompt diverso per valutare aspetti diversi della storia. Possibilmente con due modelli diversi. Se la media delle due valutazioni è inferiore a 7 (da 7 a 10) la storia viene scartata e riscritta. Vanno avanti solo le storie con punteggio superiore a 7.
  - 1 Agente Vocale che utilizza il motore TTS esterno per assegnare le voci e generare i colloqui con enfasi e timing.
    - Nota: la generazione di uno schema TTS completo e' descritta in documenti legacy basati su tool-calls. Con `ToolCalling:Enabled=false` (default) quei flussi non sono attivi e vanno migrati a output TAG-only.
  - 1 Agente agli effetti ambientali genera il sottofondo ambientale coerente con le scene.
  - 1 Agente agli effetti speciali aggiunge rumori puntuali (es. spari, porte, ecc.) in modo sincronizzato.
  - 1 Agente musicale seleziona e inserisce musica nei momenti salienti (massimo ~30% della durata).
  - 1 Agente Mixer mixa le tracce di voce/ambience/fx/musica in un unico file audio, seguendo le seguenti regole:
     - Le voci si devono sentire bene, se c'è la musica si abbassa a zero il rumore dei suoni ambientali. Gli effetti sonori hanno un volume indipendente che dipende dalla natura, uno sparo di pistola deve avere un volume altissimo.
 
