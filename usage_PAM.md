# PAM — Documentazione API e dettagli sul punteggio

Questo documento descrive come utilizzare il servizio PAM Audio Verification esposto da `server.py` sulla porta 8010. Include tutte le API, i parametri, esempi di richieste/risposte e una spiegazione dettagliata dei punteggi restituiti dall'endpoint di verifica.

## Indirizzo del servizio

Base URL: http://localhost:8010

Documentazione interattiva: http://localhost:8010/docs

---

## Sommario degli endpoint

- `GET /` — Informazioni generali sull'API
- `GET /health` — Controllo di salute (stato del server e modello)
- `GET /models` — Lista modelli disponibili (richiede modello caricato)
- `POST /analyze` — Analisi di un file audio (features, durata, predizioni)
- `POST /verify` — Verifica audio (speaker verification, autenticità)

---

## Formati audio supportati

Estensioni supportate: `.wav`, `.mp3`, `.flac`, `.m4a`, `.ogg`
Limite dimensione file predefinito: 50 MB (modificabile in `config.py`).

---

## Endpoints: dettagli e esempi

### GET /

Risposta (200):
```json
{
  "message": "PAM (Pretrained Audio Models) API Server",
  "version": "1.0.0",
  "status": "running",
  "port": 8010,
  "endpoints": { 
    "/docs": "Interactive API documentation",
    "/health": "Health check",
    "/analyze": "Analyze audio file",
    "/models": "List available models"
  }
}
```

### GET /health

Controlla se il servizio è attivo e se il modello è caricato.

Response (200):
```json
{
  "status": "healthy",
  "model_status": "loaded", // oppure "not_loaded"
  "timestamp": "2025-11-12"
}
```

### GET /models

Lista modelli disponibili. Se il modello PAM non è caricato, restituisce 503.

Response (200):
```json
{
  "available_models": ["pam_base", "pam_large"],
  "current_model": "pam_base",
  "status": "ready"
}
```

### POST /analyze

Descrizione: carica un file audio e restituisce metadati, features e (se implementato) predizioni dal modello PAM.

Parametri (multipart/form-data):
- `file` (obbligatorio): file audio
- `model_name` (opzionale): nome del modello da usare

Esempio curl:
```bash
curl -X POST "http://localhost:8010/analyze" \
  -H "accept: application/json" \
  -H "Content-Type: multipart/form-data" \
  -F "file=@/path/to/your/audio.wav"
```

Risposta (200) — esempio (campo `analysis_results` contiene placeholder e/o features estratte con `librosa`):
```json
{
  "filename": "audio.wav",
  "file_size": 1234567,
  "model_used": "default",
  "analysis_results": {
    "duration": 5.23,
    "sample_rate": 44100,
    "features": {
      "spectral_centroid": 2000.5,
      "zero_crossing_rate": 0.1,
      "mfcc": [1.2, -0.5, 0.8, ...]
    },
    "pam_predictions": {
      "confidence": 0.92,
      "labels": ["speech", "music"]
    }
  },
  "status": "success"
}
```

Errori comuni:
- 400 Bad Request — file mancante o formato non supportato
- 413 Payload too large — file > limite
- 503 Service Unavailable — PAM non caricato

---

### POST /verify

Descrizione: endpoint principale per la verifica audio.
Supporta casi come speaker verification (confronto tra file principale e file di riferimento) o controlli di autenticità.

Parametri (multipart/form-data):
- `file` (obbligatorio): file audio da verificare
- `reference_file` (opzionale): file audio di riferimento per confronto
- `verification_type` (opzionale, default `speaker_verification`): tipo di verifica (es. `speaker_verification`, `audio_authenticity`)

Esempio curl (con reference file):
```bash
curl -X POST "http://localhost:8010/verify" \
  -H "accept: application/json" \
  -H "Content-Type: multipart/form-data" \
  -F "file=@/path/to/test_audio.wav" \
  -F "reference_file=@/path/to/reference_audio.wav" \
  -F "verification_type=speaker_verification"
```

Esempio risposta (200):
```json
{
  "verification_type": "speaker_verification",
  "main_file": "test_audio.wav",
  "reference_file": "reference_audio.wav",
  "results": {
    "verification_score": 0.92,
    "is_match": true,
    "verification_type": "speaker_verification",
    "details": {
      "similarity_score": 0.92,
      "threshold": 0.8,
      "processing_time": "0.5s",
      "method": "placeholder"
    }
  },
  "status": "success"
}
```

Campi spiegati:
- `verification_score` (float 0..1): punteggio complessivo di confidenza. Numero float dove 1.0 indica massima confidenza.
- `is_match` (bool): true se la `verification_score` o la `similarity_score` supera la `threshold` usata.
- `similarity_score` (float 0..1): metrica di similarità calcolata (es. coseno, euclidea normalizzata, ecc.).
- `threshold` (float 0..1): soglia usata per decidere il match. Valore di default/consigliato: 0.7–0.85 a seconda dell'algoritmo e della qualità dei campioni.
- `processing_time`: tempo impiegato dall'elaborazione (stringa o float seconds).
- `method`: stringa che descrive il metodo usato per la verifica (es. `pam_speaker_embed_compare`), utile per audit.

Codici HTTP e loro significato:
- 200 — verifica completata
- 400 — richiesta malformata
- 413 — file troppo grande
- 503 — modello PAM non caricato
- 500 — errore interno durante l'elaborazione

---

## Dettagli sul punteggio e raccomandazioni pratiche

1) Interpretazione dei valori
- `verification_score` / `similarity_score` si assumono normalizzati nell'intervallo [0, 1]. Più alto è il valore, maggiore è la similarità/confidenza.
- `is_match` = (`similarity_score` >= `threshold`). Tale soglia va tarata sullo specifico use-case e sul dataset.

2) Soglie consigliate (linee guida generali)
- Uso generale (bassa tolleranza ai falsi positivi): threshold = 0.80–0.85
- Uso permissivo (favorire recall): threshold = 0.65–0.75
- Uso bilanciato: threshold = 0.75–0.80

3) Consigli per calibrazione
- Esegui un esperimento con un dataset bilanciato di coppie "positive" (same-speaker) e "negative" (different-speaker).
- Calcola ROC / DET e scegli threshold in base al tradeoff FAR/FRR desiderato.
- Misura AUC per valutare separabilità.

4) Robustezza e fattori che influenzano il punteggio
- Durata del campione: meno di 1–2 secondi può degradare la qualità
- Rumore di fondo: rumore elevato abbassa i punteggi di similarità
- Codec e bitrate: la compressione forte (es. bitrate basso MP3) può ridurre le prestazioni
- Differenze di microfono/ambiente: normalizzazione e augmentations possono aiutare

5) Suggerimenti per migliorare accuratezza
- Estrarre embeddings robusti (es. x-vector, ECAPA-TDNN, o embedding forniti da PAM)
- Normalizzare energia e rimuovere silenzio iniziale/finale
- Usare più segmenti e aggregare i risultati (voting o score averaging)

---

## Schema JSON delle risposte (sommario)

Analyze response schema (semplificato):
```json
{
  "filename": "string",
  "file_size": number,
  "model_used": "string",
  "analysis_results": { ... },
  "status": "success"
}
```

Verify response schema (semplificato):
```json
{
  "verification_type": "string",
  "main_file": "string",
  "reference_file": "string|null",
  "results": {
    "verification_score": number,
    "is_match": boolean,
    "verification_type": "string",
    "details": {
      "similarity_score": number,
      "threshold": number,
      "processing_time": "string",
      "method": "string"
    }
  },
  "status": "success"
}
```

---

## Esempi di client

### Python (requests) - Verifica
```python
import requests

url = "http://localhost:8010/verify"
files = {
    'file': open('test_audio.wav', 'rb'),
    'reference_file': open('reference.wav', 'rb')
}
data = {'verification_type': 'speaker_verification'}

r = requests.post(url, files=files, data=data)
print(r.status_code)
print(r.json())
```

### JavaScript (fetch) - Analisi
```js
const form = new FormData();
form.append('file', audioBlob, 'audio.wav');

fetch('http://localhost:8010/analyze', { method: 'POST', body: form })
  .then(r => r.json())
  .then(console.log)
  .catch(console.error);
```

---

## Come collegare il codice al repository PAM (soham97/pam)

Nel file `server.py` le funzioni chiave da aggiornare quando si integra il modello reale sono:
- `load_pam_model()` — inizializzare correttamente il modello PAM e salvare handle in `pam_model` globale
- `process_with_pam(audio_path, model_name)` — sostituire la logica placeholder con inferenza reale (estrazione embeddings, classificazione, etc.)
- `perform_verification(main_path, ref_path, verification_type)` — implementare la logica di confronto degli embeddings (es. coseno) e restituire `similarity_score` e `verification_score` reali

Esempio di flusso con PAM (pseudocodice):
```py
# load
pam_model = pam.load_model(model_name='pam_base')

# infer
embedding1 = pam_model.embed_file(main_path)
embedding2 = pam_model.embed_file(ref_path)

# compare
similarity = cosine_similarity(embedding1, embedding2)
verification_score = calibrate(similarity)
is_match = similarity >= threshold
```

Nota: adattare nomi di funzioni ai nomi reali esposti dalla libreria PAM.

---

## Logging e audit

Consiglio di salvare per ogni richiesta:
- nome file e dimensione
- modello usato
- punteggi grezzi (similarity, logits, ecc.)
- threshold usato
- timestamp e processing_time
- eventuali warnings (file troppo corto, rumore, basso SNR)

Questi dati servono per ricalibrare e investigare anomalie.

---

## Sicurezza

- Limitare dimensione e tipi di file per evitare DoS
- Se esposto pubblicamente: autenticazione (API key / JWT) e rate limiting
- Scan antivirus opzionale sui file caricati

---

## Avanzamenti suggeriti

- Implementare endpoints asynchroni con elaborazione in background per file lunghi
- Aggiungere endpoint per gestire modelli (carica/scarica/switch)
- Aggiungere test end-to-end con dataset di riferimento per calibrazione automatica

---

## Changelog

- 2025-11-12: Documento iniziale con specifiche API, esempi e linee guida sui punteggi.

---

Se vuoi, posso ora:
- aggiornare `perform_verification` per chiamare direttamente le funzioni dal repo `soham97/pam` (se mi confermi i nomi delle API o vuoi che provi a scoprirli automaticamente), oppure
- aggiungere esempi più dettagliati di calibrazione (script Python per calcolare ROC/DET su un dataset).

