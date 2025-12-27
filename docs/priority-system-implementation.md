# Sistema di Priorità nei Comandi - Implementazione Completata

## Modifiche Implementate

### 1. Sistema di Priorità nel CommandDispatcher

**File modificati:**
- `Services/CommandDispatcher.cs`
- `Services/ICommandDispatcher.cs`

**Cambiamenti principali:**
- Sostituito `Channel<CommandWorkItem>` con `PriorityQueue<CommandWorkItem, CommandWorkItem>`
- Aggiunto campo `Priority` e `EnqueueSequence` a `CommandWorkItem`
- Implementato `IComparable<CommandWorkItem>` per ordinamento automatico
- Aggiunto parametro `priority` (default: 2) a `Enqueue()`

**Livelli di priorità:**
- **1**: Massima priorità (riservato per futuri comandi critici)
- **2**: Priorità normale (DEFAULT - tutti i comandi standard)
- **3**: Priorità media-bassa (riassunti storie - `SummarizeStory`)
- **4**: Priorità bassa (embedding memorie - `memory_embedding_worker`)

### 2. Logica di Ordinamento

```csharp
public int CompareTo(CommandWorkItem? other)
{
    // Prima per priorità (1 = massima, numeri più bassi = priorità più alta)
    var priorityComparison = Priority.CompareTo(other.Priority);
    if (priorityComparison != 0) return priorityComparison;
    
    // A parità di priorità, FIFO (sequence più bassa = più vecchio)
    return EnqueueSequence.CompareTo(other.EnqueueSequence);
}
```

**Comportamento:**
- Comandi con priorità **minore** vengono eseguiti **prima**
- A parità di priorità, si usa FIFO (primo arrivato, primo servito)
- Un comando con priorità 1 passa avanti a tutti i comandi con priorità 2, 3, 4

### 3. Auto-Summarization dopo Valutazione

**File modificato:** `Services/StoriesService.cs`

**Trigger automatico:**
- Quando una storia viene valutata con score **> 60**
- Il sistema accoda automaticamente un comando `SummarizeStory`
- Priorità: **3** (media-bassa)

**Metadata aggiuntivi per auto-summarization:**
```csharp
["triggeredBy"] = "auto_evaluation"
["evaluationScore"] = avgScore.ToString("F2")
```

**Esempio log:**
```
[123] Valutazione completata. Score medio: 75.50
[123] Auto-summarization enqueued (score: 75.50)
```

### 4. Comandi con Priorità Personalizzata

**Priority 3 - SummarizeStory:**
```csharp
_dispatcher.Enqueue(
    "SummarizeStory",
    async ctx => { ... },
    priority: 3);  // Priorità media-bassa
```

**Priority 4 - Memory Embeddings:**
```csharp
_dispatcher.Enqueue(
    "memory_embedding_worker",
    async ctx => { ... },
    priority: 4);  // Priorità bassa
```

**Priority 2 (Default) - Tutti gli altri comandi:**
```csharp
_dispatcher.Enqueue(
    "GenerateTTS",
    async ctx => { ... });  // priority: 2 (default implicito)
```

## Vantaggi

### 1. Gestione Ottimizzata delle Risorse
- Operazioni critiche (generazione storie, TTS, audio) hanno priorità
- Operazioni "nice-to-have" (riassunti, embedding) vengono eseguite quando il sistema è meno impegnato

### 2. Migliore UX
- Utenti non aspettano per embedding o riassunti quando stanno generando contenuti
- Riassunti avvengono in background dopo valutazione positiva

### 3. Scalabilità
- Facile aggiungere nuovi livelli di priorità se necessario
- Sistema modulare e estensibile

## Esempi di Scenari

### Scenario 1: Sistema sotto carico
```
CODA PRIMA:
[Priority 2] GenerateTTS (Storia 456)
[Priority 2] GenerateMusic (Storia 456)
[Priority 3] SummarizeStory (Storia 123)  ← Aspetta
[Priority 4] memory_embedding_worker      ← Aspetta più a lungo

ESECUZIONE:
1. GenerateTTS eseguito
2. GenerateMusic eseguito
3. SummarizeStory eseguito (quando libero)
4. memory_embedding_worker eseguito (quando libero)
```

### Scenario 2: Comando urgente arriva
```
CODA:
[Priority 3] SummarizeStory (Storia 123)  ← In attesa
[Priority 4] memory_embedding_worker      ← In attesa

NUOVO COMANDO:
[Priority 2] GenerateTTS (Storia 789)     ← PASSA AVANTI!

NUOVA CODA:
[Priority 2] GenerateTTS (Storia 789)     ← Eseguito per primo
[Priority 3] SummarizeStory (Storia 123)
[Priority 4] memory_embedding_worker
```

### Scenario 3: Auto-summarization
```
1. Utente valuta storia 456 → Score: 85
2. Sistema accoda automaticamente SummarizeStory (priority: 3)
3. Se ci sono comandi priority 2 in coda, li esegue prima
4. Quando libero, genera riassunto in background
5. Utente vede riassunto quando completo (nessuna attesa)
```

## Test

### Verificare Funzionamento Priorità

1. **Accoda comandi con priorità diverse:**
```bash
# Priority 2 (default)
curl -X POST "http://localhost:5000/api/stories/123/generate-tts"

# Priority 3 (riassunto)
curl -X POST "http://localhost:5000/api/commands/summarize?storyId=456"

# Priority 4 (embedding - automatico all'avvio)
```

2. **Osserva popup "Comandi in esecuzione":**
- I comandi dovrebbero essere eseguiti in ordine di priorità
- Comandi priority 2 prima di priority 3, poi 4

3. **Testa auto-summarization:**
```bash
# 1. Genera una storia
# 2. Valuta con score > 60
# 3. Osserva che SummarizeStory viene accodato automaticamente
# 4. Verifica che non blocca altri comandi
```

## Note Tecniche

### Perché PriorityQueue invece di Channel?

**Channel (prima):**
- FIFO semplice
- No supporto nativo per priorità
- Avremmo dovuto implementare logica custom complessa

**PriorityQueue (ora):**
- Heap binario efficiente (O(log n) insert/dequeue)
- Ordinamento automatico tramite IComparable
- Gestione priorità nativa
- Thread-safe con lock esplicito

### Thread Safety

```csharp
lock (_queueLock)
{
    _queue.Enqueue(workItem, workItem);
}
_queueSemaphore.Release();
```

- `_queueLock` protegge accesso a PriorityQueue
- `_queueSemaphore` notifica worker quando c'è lavoro
- Approccio producer-consumer thread-safe

### Backward Compatibility

Tutti i comandi esistenti continuano a funzionare:
- Parametro `priority` è opzionale (default: 2)
- Nessuna modifica richiesta al codice esistente
- Solo nuovi comandi possono specificare priorità custom

## Futuri Miglioramenti

### Possibili Priority 1 (Critica)
- Comandi triggered da webhook esterni
- Operazioni di emergency stop/cleanup
- Health check critici

### Metriche
- Tempo medio in coda per priorità
- Distribuzione esecuzioni per priorità
- Identificare bottleneck

### UI
- Mostrare priorità nel popup comandi
- Badge colore diverso per priorità (es: rosso=1, giallo=2, verde=3/4)
