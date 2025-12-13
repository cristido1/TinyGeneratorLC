-- Esempi di Serie da inserire nella tabella series
-- Basati sulla struttura consigliata con separazione tra campi di filtraggio e campi per generazione

BEGIN TRANSACTION;

-- ESEMPIO 1: FUTURO - Frontiera di Cenere
INSERT INTO series (
    titolo,
    genere,
    sottogenere,
    periodo_narrativo,
    tono_base,
    target,
    lingua,
    ambientazione_base,
    premessa_serie,
    arco_narrativo_serie,
    stile_scrittura,
    regole_narrative,
    note_ai,
    episodi_generati,
    data_inserimento
) VALUES (
    'Frontiera di Cenere',
    'Fantascienza',
    'Militare / Space Opera realistica',
    'Futuro lontano',
    'Cupo, realistico',
    'Adulto',
    'Italiano',
    'Periodo: anno 3050
Umanità:
- Civiltà interstellare attiva
- Motore FTL disponibile da circa 100 anni
- Colonizzazione limitata a poche decine di sistemi

Tecnologia:
- Viaggi FTL affidabili ma costosi
- Armi avanzate con limiti fisici credibili
- Comunicazioni interstellari con ritardi

Contesto politico:
- Governi terrestri frammentati
- Flotte militari semi-autonome
- Corporazioni influenti',
    'L''umanità ha conquistato le stelle ma ha perso l''illusione di essere invincibile. Ogni espansione porta nuovi conflitti e dilemmi morali.',
    'Dal periodo di espansione fiduciosa alla scoperta di una minaccia sistemica che mette in crisi la sopravvivenza della civiltà umana.',
    'Tecnico ma leggibile, dialoghi asciutti, attenzione alla vita di bordo, niente eroismi gratuiti.',
    '- Niente tecnologia magica
- Ogni decisione ha conseguenze
- Niente distruzione gratuita di pianeti abitati',
    'Privilegiare tensione psicologica rispetto all''azione continua.',
    0,
    datetime('now')
);

-- ESEMPIO 2: PASSATO - Il Sale e il Ferro
INSERT INTO series (
    titolo,
    genere,
    sottogenere,
    periodo_narrativo,
    tono_base,
    target,
    lingua,
    ambientazione_base,
    premessa_serie,
    arco_narrativo_serie,
    stile_scrittura,
    regole_narrative,
    note_ai,
    episodi_generati,
    data_inserimento
) VALUES (
    'Il Sale e il Ferro',
    'Storico',
    'Dramma / Avventura',
    'Medioevo',
    'Crudo, realistico',
    'Adulto',
    'Italiano',
    'Periodo: Italia settentrionale, fine XIII secolo
Società:
- Città-stato in conflitto
- Potere diviso tra nobiltà, clero e corporazioni

Tecnologia:
- Armi bianche e balestre
- Navigazione costiera e fluviale
- Medicina empirica e religiosa

Contesto politico:
- Alleanze instabili
- Tradimenti frequenti
- Giustizia arbitraria',
    'In un mondo dove il potere vale più della verità, sopravvive solo chi accetta compromessi morali.',
    'Ascesa e caduta di una famiglia mercantile attraverso guerre, carestie e tradimenti.',
    'Narrativo diretto, descrizioni sensoriali, dialoghi realistici, nessuna idealizzazione del passato.',
    '- Nessun anacronismo
- Violenza solo se narrativa
- Le istituzioni non sono mai totalmente giuste',
    'Evitare linguaggio moderno; privilegiare conflitti personali e sociali.',
    0,
    datetime('now')
);

COMMIT;
