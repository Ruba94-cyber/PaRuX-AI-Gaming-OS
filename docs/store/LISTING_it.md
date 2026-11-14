# Scheda Microsoft Store - Italiano (it-IT)

Testo pronto da incollare in Partner Center (Scheda Store > it-IT). Le affermazioni sono limitate a ciò che codice, `docs/REQUIREMENT_MATRIX.md` e `docs/QA_EVIDENCE.md` mostrano come implementato e testato. Niente numeri di velocità, classifiche o "il migliore". La tabella delle evidenze è in `LISTING_en.md` (solo per il titolare). Politiche su prezzo, versione gratuita e donazioni: `POLICY_AND_CERTIFICATION.md`. Prezzo: `PRICING_AND_AVAILABILITY.md`.

## Titolo

PaRuX AI & Gaming OS

## Descrizione breve (meno di 100 caratteri)

Chatta con modelli AI e interroga i tuoi file, direttamente sul tuo PC.

## Descrizione (da incollare)

PaRuX AI & Gaming OS è uno spazio di lavoro AI "local-first" per Windows. Chatta con modelli linguistici che girano sul tuo PC e fai domande sui documenti che scegli tu.

Da sapere prima di acquistare o installare:
- PaRuX ha bisogno di un modello AI per rispondere. Il pacchetto dello Store contiene l'app e un motore di inferenza locale, ma nessun file modello. Alla prima chat PaRuX ti aiuta a scegliere un modello e lo scarica solo dopo la tua conferma (i modelli sono grandi, spesso diversi GB).
- L'edizione Microsoft Store include il motore di inferenza e non scarica né installa mai programmi: nessun motore aggiuntivo, nessuno strumento Python, nessun installer. Vengono scaricati solo i file dei modelli AI e altri dati, e solo dopo la tua conferma. Il motore torrent integrato e il download di archivi di giochi non fanno parte di questa edizione. Puoi comunque indicare a PaRuX software che hai già installato tu.
- Gli aggiornamenti dell'app arrivano dal Microsoft Store.
- La velocità dipende dal tuo PC. Un computer con una GPU recente o abbastanza memoria esegue meglio i modelli più grandi.
- Questa è una versione iniziale. Alcune funzioni sono ancora in fase di test su hardware reale.
- Alcune schermate potrebbero ancora mostrare testo in inglese quando l'interfaccia è in italiano.

Cosa puoi fare
- Chattare con modelli che girano sul tuo PC. Le conversazioni restano salvate sul tuo PC.
- Fare domande sui tuoi documenti. PaRuX indicizza le cartelle che scegli e risponde indicando la fonte usata; rifiuta di rispondere quando i tuoi file non contengono la risposta.
- Trovare e gestire i modelli nel Model Hub integrato, con avanzamento e ripresa dei download.
- Avviare i giochi già installati sul PC da una libreria giochi. La Modalità Gaming ferma i processi AI locali mentre è in esecuzione un gioco avviato da PaRuX, per lasciare memoria e GPU al gioco.
- Usare PaRuX in inglese o in italiano.
- Opzionale: collegare i tuoi dispositivi abbinati nella rete domestica (disattivato di default).

I tuoi file e le tue chat non vengono caricati su un server PaRuX. Non serve un account PaRuX, non ci sono pubblicità né acquisti in-app. Consulta l'informativa sulla privacy per sapere cosa esce dal PC e quando.

Informazioni su questa edizione: un solo prezzo, una tantum, tramite il Microsoft Store. Ha le stesse funzioni di AI locale delle altre versioni del progetto, tranne quanto elencato sopra che l'edizione Store non include (download di eseguibili, motore torrent). PaRuX è "source-available" con licenza FSL-1.1-MIT; il codice sorgente è su GitHub.

Avviso: PaRuX usa il motore di inferenza llama.cpp (licenza MIT), incluso nel pacchetto.

## Funzionalità del prodotto (una per riga, max 200 caratteri)

Chat AI locale con modelli scaricati una volta ed eseguiti sul tuo PC
Domande sui tuoi documenti con la fonte indicata e rifiuto quando la risposta non è nei file
Model Hub con download riprendibili, avviati solo quando lo decidi tu
Libreria giochi che avvia i giochi già installati; la Modalità Gaming libera memoria e GPU per il gioco
Interfaccia in inglese e italiano
Nessun account, nessuna pubblicità, nessun tracciamento, nessun acquisto in-app
Abbinamento opzionale dei tuoi dispositivi nella rete domestica, disattivato finché non lo attivi
Aggiornamenti tramite il Microsoft Store

## Termini di ricerca (max 7)

AI locale; chat offline; LLM; AI privata; documenti; model hub; libreria giochi

## Novità di questa versione (4.2.0)

Prima versione sul Microsoft Store. Chat locale, domande sui documenti con fonti, Model Hub, libreria giochi con Modalità Gaming, interfaccia in inglese e italiano. Gli aggiornamenti dell'app arrivano ora dal Microsoft Store.

## Requisiti di sistema

Minimi: Windows 10 versione 1809 (build 17763) o successivo, 64 bit (x64); circa 1 GB liberi per l'app più lo spazio per i modelli scaricati; l'app parte ed esegue modelli piccoli su CPU.
Consigliati: 16 GB di RAM (un modello da 4 miliardi di parametri ha usato al massimo 6,7 GB sul PC di riferimento; i PC da 8 GB non sono ancora verificati), un SSD e una GPU con supporto Vulkan per i modelli più grandi. Sono indicazioni: la velocità sul tuo PC non è garantita.

## Copyright e marchi

(c) 2026 l'editore di PaRuX. I nomi di terzi appartengono ai rispettivi proprietari.
(Il titolare sostituisce "l'editore di PaRuX" con il nome editore già presente in Partner Center.)

## Categoria

Produttività (alternativa: Strumenti per sviluppatori). Non "Giochi".

## Note per la certificazione

Vanno inserite in inglese: vedi `POLICY_AND_CERTIFICATION.md` sezione 4.

## Classificazione per età

Compila tu il questionario IARC; risposte pronte in `POLICY_AND_CERTIFICATION.md` sezione 5.

## Schermate

Vedi `SCREENSHOTS.md` (nessuna è inclusa nel repository).
