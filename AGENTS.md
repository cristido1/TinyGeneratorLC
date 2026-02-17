# Codex Project Instructions

## Index Pages
- Before modifying or creating any `Index` page, read and follow the rules in `docs/index_page_rules.txt`.

## Lingua
- Rispondi sempre in italiano.

## Database
- In questo progetto **non si usano migrazioni applicative**.
- Per modifiche schema/dati usare **comandi SQL diretti** sul database.
- Prima di operazioni potenzialmente distruttive (UPDATE/DELETE massivi, ALTER TABLE, ricalcoli globali), creare una **copia di backup** del file DB.
- Se non sei sicuro dell'impatto di una modifica DB, fai prima un **backup del database** e poi procedi con SQL diretto.
