# Pagine Razor (UI) e cosa fanno

Questa lista è ricavata dalla struttura in `Pages/`.

## Principali
- `/` (`Pages/Index.cshtml`): home.
- `/Genera` (`Pages/Genera.cshtml`): generazione storie.
- `/Chat` (`Pages/Chat.cshtml`) + `/ChatLog` (`Pages/ChatLog/Index.cshtml`): chat e storico.
- `/Admin` (`Pages/Admin.cshtml`): dashboard/admin.

## Storie
- `/Stories` (`Pages/Stories/Index.cshtml`): lista storie.
- `/Stories/Create`, `/Stories/Edit`, `/Stories/Details`.
- `/Stories/TtsSchema`: visualizzazione/schema TTS per storia.
- `/StoriesStatus/*`: gestione stati macchina.

## Agenti / ruoli / voci
- `/Agents/*`: CRUD agenti + pagina fallback modelli.
- `/Roles/*`: gestione ruoli per fallback.
- `/TtsVoices/*`: gestione voci.

## Modelli e test
- `/Models/*`: catalogo modelli.
- `/LangChainTest`: runner test.
- `/TestDefinitions/*`: definizioni test.
- `/ModelStats/Index`: statistiche modelli.
- `/OllamaMonitor`: monitor.

## Config e logging
- `/Settings/Index`: viewer/edit configurazioni (UI).
- `/Logs/Index`, `/Logs/Analysis`, `/Logs/LiveMonitor`: log, analisi, live.
- `/SystemReports/Index`: report.

## Serie
- `/Series/*`: gestione serie.
- `/GeneraSerie/Index`: generazione serie.

## Narrative Engine
- `/NarrativeProfiles/*`
- `/NarrativeResources/*`
- `/MicroObjectives/*`
- `/FailureRules/*`
- `/ConsequenceRules/*`
- `/ConsequenceImpacts/*`

## Planning
- `/PlannerMethods` e `/TipoPlanning`.

## Nota su Index pages
Per qualsiasi modifica/creazione di pagine Index (griglie/lista), seguire `docs/index_page_rules.txt` (DataTables + Bootstrap 5).
