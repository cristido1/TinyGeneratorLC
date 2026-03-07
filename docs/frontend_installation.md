# Frontend Installazione e Pacchetti

Questa nota documenta cosa è installato nella cartella locale `frontend/` (esclusa dal versionamento Git).

## File di riferimento

- `frontend/package.json`: dipendenze e script
- `frontend/package-lock.json`: versione risolta dei pacchetti installati

## Script disponibili

- `npm run build` -> build Vite verso `wwwroot/js/vue-roles`

## Dipendenze runtime

- `vue` `^3.5.21`
- `primevue` `^4.4.1`
- `primeicons` `^7.0.0`
- `@primeuix/themes` `^1.2.3`

## Dipendenze sviluppo

- `vite` `^7.1.5`
- `@vitejs/plugin-vue` `^6.0.1`

## Nota operativa

La sorgente `frontend/` resta locale; gli asset usati dall'app vengono generati in:

- `wwwroot/js/vue-roles/`
