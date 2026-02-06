## AI Coding Assistant — TinyGenerator (concise)

Queste istruzioni sono pensate per rendere un AI assistant produttivo **subito** in questo repo.

### Entry point (docs)
- `README.md` (indice)
- `readme_tables.md` (tabelle DB)
- `readme_agent_roles.md` (agenti/ruoli + fallback)
- `readme_responses.md` (TAG + validazione)
- `readme_appsettings.md` (config)
- `readme_command_dispatcher.md` (dispatcher + lista comandi)
- `readme_pages.md` (pagine Razor)

### File da leggere per primi
- `Program.cs` (DI, startup)
- `Services/CommandDispatcher.cs` (coda comandi)
- `Services/LangChainChatBridge.cs` (call model + validazione + fallback)
- `Services/StoriesService.cs` + `Services/Commands/*` (pipeline)
- `data/TinyGeneratorDbContext.cs` + `Models/*` (schema EF)

### Convenzioni e gotcha (obbligatori)
- **NO EF Core migrations**: non usare `dotnet ef migrations add` / `dotnet ef database update`. Lo schema SQLite è mantenuto con SQL/manual scripts idempotenti.
- **Index pages / DataTables**: prima di creare/modificare pagine *Index* (liste/griglie), seguire `docs/index_page_rules.txt`.
- **Output LLM (policy runtime)**: gli agenti devono usare TAG tra parentesi quadre; non richiedere JSON agli agenti.
- **Tool calling**: supporto presente per retrocompatibilita'/test, ma e' disabilitato di default (config `ToolCalling:Enabled=false`). Non introdurre nuove dipendenze runtime da tool-calls.
- **Fail-fast**: non silenziare errori tool; la pipeline deve fallire in modo visibile.

### Documentazione (disciplina)
- Per ogni modifica strutturale o comportamentale (DB schema, API, orchestrazione, UI): aggiornare **README.md** e/o il file `readme_*.md` pertinente.

### Workflow (VS Code tasks)
- Build: `dotnet build` (task `build`)
- Test: `dotnet test` (task `test`)
- Dev: `dotnet watch run` (task `watch`)