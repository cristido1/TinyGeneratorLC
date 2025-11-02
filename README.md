# TinyGenerator

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