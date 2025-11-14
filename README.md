# TinyGenerator

> ATTENZIONE PER SVILUPPATORI
>
> Questo progetto usa agenti costruiti con Microsoft Semantic Kernel. È obbligatorio usare le function/skill e le API fornite da Semantic Kernel per qualsiasi integrazione di funzioni (es. salvataggio/lettura memoria, accesso al DB, skill esterne). Non "inventare" nomi di funzioni o meccaniche di invocation alternative nei prompt degli agenti che vadano a modificare il comportamento degli agenti writer o evaluator in produzione. I test di function-calling devono essere isolati e non influire sui prompt usati dagli agenti di generazione.

> NOTE DI PROGETTO (importante)
>
> - Questo repository è specificamente progettato per testare e validare il comportamento di Microsoft Semantic Kernel. Pertanto:
>   1. Non usare workaround che bypassano Semantic Kernel (compatibility shims, wrapper che trasformano risposte in stringhe o che reinterpretano invocation). Se il kernel non è presente o genera errori, i test devono fallire visibilmente (fail-fast) per evidenziare problemi di integrazione.
>   2. Evitare fallback automatici che trasformano chiamate di funzione in testo o JSON nel prompt. Gli agenti devono invocare le skill/addin tramite il meccanismo ufficiale del Semantic Kernel.
>   3. Se si aggiungono compatibility helpers (per sviluppo locale), documentarli chiaramente e assicurarsi che possano essere disabilitati con una flag di configurazione in modo che l'app possa eseguire un'esperienza "pure SK" senza shim.
>   4. Qualsiasi modifica che cambia il flusso di invocation degli agenti richiede revisione e approvazione prima del merge.

> Rileggi queste note prima di modificare il comportamento degli agenti o la compatibility layer.

Un'applicazione web ASP.NET Core per la generazione di storie usando agenti AI basati su Semantic Kernel e modelli locali Ollama.

## Descrizione

TinyGenerator permette di creare storie complete attraverso un processo strutturato che utilizza un planner per orchestrare agenti AI. Gli agenti seguono un piano multi-pass per generare trame, personaggi, capitoli e riassunti, producendo narrazioni coerenti e ben strutturate.

## Caratteristiche

- **Generazione guidata da planner**: Utilizza il planner di Semantic Kernel per coordinare passi sequenziali di creazione storia.
- **Agenti specializzati**: Due agenti scrittori (basati su modelli locali) e due valutatori per garantire qualità.
- **Controllo costi e token**: Monitoraggio e limiti sui costi di generazione.
- **Persistenza**: Salvataggio storie e log in database SQLite.
- **Interfaccia utente**: Design ispirato a Google Keep con sidebar collassabile.
- **Logging**: Registrazione attività in SQLite per auditing.

## Requisiti

- .NET 9.0
- SQLite
- Ollama con modelli locali:
  - Scrittori: `llama2-uncensored:7b`, `qwen2.5:7b`
  - Valutatori: `qwen2.5:3b`, `llama3.2:3b`

## Installazione

1. Clona il repository:
   ```bash
   git clone https://github.com/cristido1/TinyGenerator.git
   cd TinyGenerator
   ```

2. Installa dipendenze:
   ```bash
   dotnet restore
   ```

3. Assicurati che Ollama sia installato e i modelli scaricati:
   ```bash
   ollama pull llama3.1:8b
   ollama pull qwen2.5:7b
   ollama pull qwen2.5:3b
   ollama pull llama3.2:3b
   ```

4. Avvia l'applicazione:
   ```bash
   dotnet run
   ```

5. Apri http://localhost:5077 nel browser.

## Utilizzo

1. Nella pagina principale, inserisci un tema per la storia (es. "un'avventura fantasy con draghi").
2. Clicca "Genera" per avviare il processo.
3. Il planner coordina gli agenti per:
   - Creare una trama in 6 capitoli.
   - Definire personaggi e caratteri.
   - Scrivere ciascun capitolo con narratore e dialoghi.
   - Generare riassunti cumulativi.
4. La storia completa viene valutata e, se supera il punteggio minimo (7/10), salvata.

## Architettura

- **StoryGeneratorService**: Coordina generazione usando planner.
- **Planner**: HandlebarsPlanner stub che definisce passi sequenziali.
- **Agenti**: ChatCompletionAgent per scrittura e valutazione.
- **Memoria**: Contesto in-memory per stato generazione.
- **Database**: SQLite per storie, log, costi.

## Configurazione

- Modifica `appsettings.json` per limiti costi/token.
- I modelli sono hardcoded in `StoryGeneratorService.cs`.

## Contributi

Contributi benvenuti! Segui le linee guida standard per pull request.

## Licenza

MIT

## Note operative e best practice

Per le pagine che gestiscono la lista e la modifica degli agenti seguire le best practice di Razor Pages, DataTables.net e Bootstrap 5:

- Centralizzare i riferimenti a JS/CSS critici (jQuery, Bootstrap, DataTables) in `Pages/Shared/_Layout.cshtml` e usare fallback CDN quando necessario.
- Usare le Tag Helpers Razor per i form (`asp-for`, `asp-page`, `asp-route-*`) e `SelectList`/`select` per le combo valorizzate dal server.
- Inizializzare DataTables in un file JS separato (es. `wwwroot/js/agents-index.js`) e usare l'estensione `Buttons` per toolbar coese, `responsive.renderer` per i dettagli e la paginazione/filtro forniti da DataTables (evitare duplicazioni UI).
- Mettere la logica di rendering dei dettagli (prompt/instructions/execution_plan) nel renderer di DataTables o usare colonne nascoste solo come fonte dati; non duplicare icone o handler JS manuali quando DataTables fornisce lo stesso comportamento.
- Validare sempre i campi JSON lato server (es. `Skills`, `Config`, `ExecutionPlan`) e fornire messaggi di errore chiari nella pagina `Edit`.

Pagine di riferimento per queste modifiche e per la UI Agents:

- `Pages/Agents/Index.cshtml`
- `Pages/Agents/Edit.cshtml`
- `Pages/Agents/Create.cshtml`

Seguire questi punti garantisce coerenza visiva, migliore manutenzione e comportamenti prevedibili tra client/server.