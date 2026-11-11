# Scheda Microsoft Store - Italiano (it-IT)

Bozza per Partner Center. Il nome del prodotto deve coincidere con il nome riservato dal proprietario in Partner Center (predefinito della build: "PaRuX AI Studio"). Le affermazioni sono limitate a cio che `docs/REQUIREMENT_MATRIX.md` indica come implementato e testato. Non aggiungere numeri di velocita, classifiche o "il migliore". Rimuovi i riferimenti "(evidenza: ...)" e "(EV-06)" prima di incollare in Partner Center.

## Descrizione breve (max 100 caratteri)

Chatta con modelli AI e interroga i tuoi file, direttamente sul tuo PC.

## Descrizione completa

PaRuX AI Studio e uno spazio di lavoro AI "local-first" per Windows. Chatta con modelli linguistici che girano sul tuo PC e fai domande sui documenti che scegli tu.

Da sapere prima di installare:
- PaRuX ha bisogno di un modello AI per rispondere. Il pacchetto dello Store contiene l'app e un motore di inferenza locale, ma nessun file modello. Alla prima chat PaRuX ti aiuta a scegliere un modello e lo scarica solo dopo la tua conferma (i modelli sono grandi, spesso diversi GB).
- L'edizione Microsoft Store include il motore di inferenza e non scarica ne installa mai programmi: nessun motore aggiuntivo, nessuno strumento Python, nessun installer. Vengono scaricati solo i file dei modelli AI e altri dati, e solo dopo la tua conferma. Il motore torrent integrato non fa parte di questa edizione. Puoi comunque indicare a PaRuX software che hai gia installato tu.
- La velocita dipende dal tuo PC. Un computer con una GPU recente o abbastanza memoria esegue meglio i modelli piu grandi.
- Questa e una versione iniziale. Alcune funzioni sono ancora in fase di test su hardware reale.

Cosa puoi fare
- Chattare con modelli che girano sul tuo PC. Le conversazioni restano salvate sul tuo PC.
- Fare domande sui tuoi documenti. PaRuX indicizza le cartelle che scegli e risponde indicando la fonte usata; rifiuta di rispondere quando i tuoi file non contengono la risposta.
- Trovare e gestire i modelli nel Model Hub integrato, con avanzamento e ripresa dei download.
- Usare PaRuX in inglese o in italiano (alcune schermate potrebbero ancora mostrare testo in inglese).
- Opzionale: collegare i tuoi dispositivi abbinati nella rete domestica (disattivato di default).

I tuoi file e le tue chat non vengono caricati su un server PaRuX. Non serve un account PaRuX e non ci sono pubblicita. Consulta l'informativa sulla privacy per sapere cosa esce dal PC e quando.

## Punti elenco

- Chat AI locale con modelli scaricati una volta e usati offline (evidenza: collaudo Standard Portable, 12/12 controlli, QA-036)
- Domande sui tuoi documenti con la fonte indicata e rifiuto quando la risposta non e nei file (evidenza: RAG-01/02, EV-09 sul PC di riferimento)
- Model Hub con download riprendibili, avviati solo su tua azione
- Interfaccia in inglese e italiano
- Nessun account, nessuna pubblicita, nessun tracciamento
- Abbinamento LAN opzionale per i tuoi dispositivi, disattivato finche non lo attivi

## Parole chiave (max 7)

AI locale; chat offline; LLM; AI privata; documenti; llama.cpp; model hub

## Novita di questa versione (4.1.0)

Prima versione sul Microsoft Store. Chat locale, domande sui documenti con fonti, Model Hub, interfaccia in inglese e italiano. Gli aggiornamenti dell'app arrivano ora dal Microsoft Store.

## Requisiti di sistema

Minimi (l'app parte ed esegue modelli piccoli su CPU): Windows 10 versione 1809 (build 17763) o successivo, 64 bit (x64), circa 1 GB liberi per l'app piu lo spazio per i modelli scaricati.
Memoria: un modello da 4 miliardi di parametri ha usato al massimo 6,7 GB di RAM nel test sul PC di riferimento (EV-06); i PC da 8 GB non sono ancora verificati, quindi si consigliano 16 GB di RAM. I modelli piu grandi richiedono piu RAM, GPU con supporto Vulkan, SSD. Sono indicazioni: la velocita sul tuo PC non e garantita.

## Note per la certificazione

Vedi la versione inglese (`LISTING_en.md`): le note per la certificazione vanno inserite in inglese in Partner Center.

## Classificazione per eta (compila tu il questionario IARC)

Stesse note della versione inglese: strumento di chat AI generico, testo generato dal modello scelto dall'utente, nessun acquisto in-app, nessuna pubblicita, nessun gioco d'azzardo, nessun social network. La schermata libreria giochi avvia giochi gia installati e non ne vende.

## Schermate

Vedi `SUBMISSION_CHECKLIST.md` (schermate reali da acquisire; nessuna e inclusa nel repository).
