-- Storia di spionaggio: "Codice Ombra"
-- 6 episodi con personaggi completi, azione intensa e finale a sorpresa

-- Inserimento storia principale
INSERT INTO stories (
    generation_id, memory_key, ts, prompt, title, story_raw, 
    char_count, eval, score, approved, status_id, folder
) VALUES (
    'spy_codice_ombra_2026',
    'spy_thriller_six_episodes',
    datetime('now'),
    'Storia di spionaggio internazionale con tradimenti, inseguimenti e finale a sorpresa',
    'Codice Ombra',
    'Una storia di spionaggio internazionale dove nulla è come sembra. L''agente Elena Volkov viene incaricata di infiltrarsi in una rete criminale che traffica segreti nucleari. Ma quando scopre che il suo mentore Marcus Kane potrebbe essere una talpa, deve scegliere tra lealtà e dovere. Attraverso sei episodi di azione mozzafiato, tradimenti e colpi di scena, la verità emergerà in un finale esplosivo che cambierà tutto.',
    8500,
    'Storia avvincente con ottimo ritmo e personaggi complessi',
    8.5,
    1,
    NULL,
    'spy_codice_ombra'
);

-- Capitoli/Episodi della storia
INSERT INTO chapters (memory_key, chapter_number, content, ts) VALUES 
(
    'spy_thriller_six_episodes',
    1,
    'EPISODIO 1: IL RICHIAMO

Berlino, ore 02:47. Elena Volkov osserva attraverso il visore notturno l''ingresso del nightclub "Nachtfalke". Tre uomini armati presidiano l''entrata. La sua missione è infiltrarsi nell''organizzazione di Viktor Krasnov, trafficante di segreti nucleari.

"Sei sicura?" chiede Marcus Kane attraverso l''auricolare. Suo mentore da dieci anni, l''uomo che l''ha addestrata.

"Come sempre" risponde Elena, controllando la Glock 19 nascosta sotto il giubbotto.

L''infiltrazione procede senza intoppi. Elena, spacciandosi per una intermediaria del mercato nero, attira l''attenzione di Alexei, il braccio destro di Krasnov. Ma durante la trattativa, riconosce un volto familiare tra le guardie: James Harper, ex collega del MI6, dichiarato morto tre anni prima in un''operazione a Istanbul.

Il loro sguardo si incrocia per un istante. Harper la riconosce.

Improvvisamente, le luci si spengono. Grida. Spari. Elena si butta dietro il bancone mentre i proiettili trafiggono bottiglie di vodka. Sette morti in trenta secondi. Lei ne elimina tre, ma Harper svanisce nel caos.

All''alba, mentre esamina le foto della scena del crimine, Elena nota qualcosa di impossibile: un ciondolo che apparteneva a Marcus Kane tra i cadaveri. Ma Marcus è a Londra. O dovrebbe esserlo.

Il telefono squilla. Numero sconosciuto.

"Hai ventiquattro ore per scoprire chi tradisce chi, Elena. Poi tutti morirete."

La linea si interrompe.',
    datetime('now')
),
(
    'spy_thriller_six_episodes',
    2,
    'EPISODIO 2: IL DOPPIO GIOCO

Vienna, Hotel Imperial. Elena deve incontrare il suo contatto nella CIA, Sarah Chen, ma l''agente arriva con tre ore di ritardo, coperta di sangue.

"Ci hanno trovati" ansima Sarah. "Harper... non è morto. È lui che coordina il traffico. E ha accesso ai nostri database."

Prima che Elena possa rispondere, la porta esplode. Quattro uomini con armi automatiche. Elena spinge Sarah dietro il divano e risponde al fuoco. Due assalitori cadono, ma gli altri avanzano. Combattimento corpo a corpo. Elena rompe il braccio al terzo, ma il quarto la colpisce con il calcio del fucile.

Si sveglia in un magazzino abbandonato. Sarah è legata accanto a lei. Di fronte, James Harper, con il suo ghigno caratteristico.

"Sapevo che saresti venuta, Elena. Marcus ti ha sempre sottovalutata."

"Marcus non c''entra con questo" replica lei.

Harper ride. "Marcus Kane è Codice Ombra. Il traditore che cercate da cinque anni. E tu sei l''unica che può provarlo."

Mostra una chiavetta USB. "Qui ci sono i trasferimenti bancari, le comunicazioni cifrate. Tutto. Ma c''è un problema: anche io voglio Kane. E tu mi aiuterai."

Un''esplosione scuote l''edificio. Vetri che si frantumano. Squadra d''assalto che irrompe dal tetto. Nel caos, Elena e Sarah riescono a fuggire, ma Harper scompare ancora una volta.

Sulla chiavetta, Elena trova i file. Tutti puntano a Marcus Kane. Ma c''è anche un video datato tre giorni prima: Kane che consegna un pacco a Krasnov personalmente.

Elena guarda Sarah. "Dobbiamo tornare a Londra. Stasera."',
    datetime('now')
),
(
    'spy_thriller_six_episodes',
    3,
    'EPISODIO 3: LA VERITÀ NASCOSTA

Londra, sede del MI6. Elena e Sarah entrano con credenziali false alle 03:00. I corridori deserti amplificano ogni rumore. Devono raggiungere l''ufficio di Kane prima dell''alba.

Nel suo computer, trovano documenti classificati: operazioni nascoste, fondi neri, morti inspiegate. Ma c''è qualcosa che non torna. Le date non corrispondono. Kane era in missione durante alcuni trasferimenti.

Un rumore. Elena si gira di scatto. Marcus Kane, in persona, sulla porta con una Sig Sauer puntata.

"Sei diventata brava, Elena. Troppo brava."

"Marcus, cosa hai fatto?"

Kane abbassa l''arma. "Non sono io il traditore. Ma so chi è. E se vi lascio andare, ci uccideranno tutti e tre."

Spiega: un anno fa ha scoperto che qualcuno nel MI6 vendeva segreti. Ha creato Codice Ombra come operazione di copertura per smascherare il vero traditore. Harper faceva parte del piano, ma è stato bruciato.

"Chi è?" chiede Sarah.

"Il Direttore" risponde una voce dal corridoio.

Sir Edmund Blackwood, direttore del MI6 da quindici anni, entra con sei agenti armati.

"Sapevo che sareste arrivati a questo, Marcus. Peccato."

Blackwood solleva l''arma. Kane si butta davanti a Elena. Due colpi. Kane cade.

Elena e Sarah rispondono al fuoco. Cinque agenti cadono. Ma Blackwood fugge.

Elena si china su Marcus. "Resisti!"

Marcus le afferra la mano. "Parigi... il deposito 47... tutte le prove..." Gli occhi si chiudono.

Codice rosso. L''edificio è in lockdown. Elena e Sarah devono uscire. Ora.',
    datetime('now')
),
(
    'spy_thriller_six_episodes',
    4,
    'EPISODIO 4: CACCIA A PARIGI

Parigi, Gare du Nord. Elena e Sarah arrivano all''alba su un treno merci. Sono ricercate in tutta Europa. Blackwood ha dichiarato che hanno ucciso Kane e tradito il servizio.

Il deposito 47 si trova nei tunnel della metro abbandonati sotto Montmartre. Due guardie all''ingresso. Elena le neutralizza in silenzio. Dentro, cassette di sicurezza. Quella di Kane contiene hard disk, documenti, foto.

E una registrazione.

"Se stai ascoltando questo, sono morto" dice la voce di Kane. "Blackwood lavora per Krasnov da dodici anni. Ha venduto codici nucleari, liste di agenti, tutto. Harper lo ha scoperto e Blackwood lo ha fatto uccidere. Io ho finto di essere il traditore per guadagnare tempo. Elena, fidati di Harper. È l''unico rimasto."

Un''esplosione scuote il tunnel. Sarah grida. Il deposito sta crollando. Elena afferra i documenti e corre. Dietro di lei, il soffitto crolla. Sarah resta intrappolata.

"Vai!" urla Sarah. "Finisci la missione!"

Elena esita. Un''altra esplosione. Deve scegliere.

Emerge dai tunnel mentre la polizia francese circonda l''area. Combatte, corre, spara. Quattro poliziotti feriti. Riesce a fuggire su una moto rubata.

In un hotel di periferia, esamina i documenti. C''è un piano: Krasnov venderà codici di lancio nucleari russi a un gruppo terroristico tra 48 ore. Luogo: Monaco di Baviera. Blackwood fornirà la sicurezza.

Elena ha un solo alleato possibile: James Harper.

Compone il numero che lui le ha lasciato a Vienna.

"Sono io" dice. "Ho bisogno del tuo aiuto."

"Lo so" risponde Harper. "Ci vediamo a Monaco. E preparati. Sarà un bagno di sangue."',
    datetime('now')
),
(
    'spy_thriller_six_episodes',
    5,
    'EPISODIO 5: L''ULTIMO INGANNO

Monaco, Marienplatz. Elena e Harper si incontrano in una chiesa. Non si fidano l''uno dell''altra, ma non hanno scelta.

"Il trasferimento avverrà in un magazzino portuale alle 22:00" dice Harper. "Krasnov avrà venti uomini. Blackwood altri quindici. Noi siamo in due."

Elena sorride amaramente. "Allora useremo il cervello."

Il piano è semplice: far credere a Krasnov che Blackwood intende derubarlo. E far credere a Blackwood che Krasnov intende ucciderlo. Poi colpire nel caos.

Alle 21:45, Elena si infiltra nel magazzino. Harper entra dalla parte opposta. Le comunicazioni intercettate funzionano. Krasnov e i suoi uomini arrivano nervosi, armi in pugno. Blackwood fa lo stesso.

"Hai i codici?" chiede Krasnov.

"Hai il denaro?" risponde Blackwood.

Elena innesca una registrazione modificata. La voce di Blackwood che dice: "Krasnov è un idiota. Quando avrò i codici, lo eliminerò."

Krasnov estrae la pistola. "Traditore!"

Il magazzino esplode in una sparatoria. Elena e Harper colpiscono dai lati. Dieci uomini cadono nei primi venti secondi. Krasnov viene colpito tre volte. Blackwood si barrica dietro un container.

Elena lo raggiunge. Duello ravvicinato. Lei è più veloce. Due colpi al petto.

Blackwood cade, ma sorride. "Pensavi... che fossi solo?"

Dal container esce una figura. Sarah Chen. Viva.

"Mi dispiace, Elena" dice Sarah, puntandole l''arma. "Ma Blackwood pagava meglio."

Il colpo parte. Elena sente il dolore bruciante al fianco. Cade.

Harper spara. Sarah cade morta.

"Tutto bene?" chiede Harper, aiutando Elena ad alzarsi.

"Vivrò" risponde lei, premendo sulla ferita. "I codici?"

Harper indica la valigetta accanto a Krasnov. "Qui. Ma c''è un problema."

"Quale?"

"Guarda il timer."

Sulla valigetta, un display digitale: 00:02:47... 00:02:46...

"È una bomba nucleare tattica" sussurra Harper. "E non possiamo disinnescarla."',
    datetime('now')
),
(
    'spy_thriller_six_episodes',
    6,
    'EPISODIO 6: CODICE OMBRA

Due minuti e quaranta secondi. Elena e Harper guardano il timer.

"Possiamo portarla nel porto, lontano dalla città" dice Elena.

"Non faremo in tempo" risponde Harper. "Il raggio letale è di due chilometri."

Elena osserva la valigetta. Un''idea folle. "Hai ancora contatti nella NATO?"

"Sì, ma..."

"Chiamali. Ora!"

Mentre Harper compone il numero, Elena esamina il dispositivo. Non può fermarlo, ma può rallentarlo. Strappa i fili del sistema di riscaldamento. Il timer rallenta: ogni secondo diventa due.

Cinque minuti. Forse hanno una possibilità.

Un elicottero militare atterra sul tetto del magazzino. Elena e Harper salgono con la bomba. Il pilota, un capitano tedesco, vola verso il mare a velocità massima.

"Trenta secondi al punto di rilascio" annuncia il pilota.

Elena guarda il timer: 00:00:18.

"Sgancia!"

La valigetta precipita nell''oceano. L''elicottero vira bruscamente.

Cinque secondi. Quattro. Tre. Due.

L''esplosione subacquea crea un''onda d''urto che scuote l''elicottero, ma reggono. Nessuna radiazione raggiunge la costa.

Due settimane dopo. Londra, nuovo quartier generale del MI6.

Elena riceve una medaglia in una cerimonia privata. Harper è accanto a lei. Entrambi ora lavorano per una nuova divisione anti-terrorismo.

Ma quella sera, nel suo appartamento, Elena trova una busta. Dentro, una foto: Marcus Kane, vivo, in un caffè di Praga. Data: una settimana fa.

E un messaggio: "Codice Ombra non è mai stato Marcus. Non è mai stato Blackwood. Codice Ombra sono io. E non abbiamo ancora finito. - V.K."

Le iniziali di Viktor Krasnov.

Elena guarda la foto di nuovo. Il volto di "Marcus" è leggermente diverso. Chirurgia plastica?

Il telefono squilla. Harper.

"Hai visto le notizie? Krasnov è vivo. E ha rubato altri codici."

Elena sorride amaramente. "Allora ricominciamo."

FINE... O FORSE NO?',
    datetime('now')
);
