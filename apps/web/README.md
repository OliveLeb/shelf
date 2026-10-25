# Application web

Vue 3, Vue Router et Nuxt UI (plugin Vite, sans Nuxt), thème et composants
partagés fournis par `@shelf/ui`. Connexion persistante et liste de la racine
de l’espace personnel, ouverture des dossiers et envois directs vers S3, simples
ou multipart avec reprise en mémoire. La navigation détaillée et les lots/conflits
sont des extensions séparées.

Depuis la racine du dépôt :

```sh
docker compose run --rm node pnpm install --frozen-lockfile
docker compose --profile front up web   # https://localhost:3000
docker compose run --rm node pnpm --filter @shelf/web build
docker compose run --rm node pnpm check
docker compose run --rm node pnpm test
```

Lancer d’abord l’API avec PostgreSQL, les migrations et un compte existant
(voir [le backend](../backend/README.md)). Pour que le conteneur web atteigne
l’API sur l’hôte, lancer celle-ci avec `--no-launch-profile --urls http://0.0.0.0:5050`
et `Authentication__ClientOrigin=https://localhost:3000`. Restreindre ce port
au réseau de développement ; en production, seule l’entrée HTTPS est publique.

Le serveur Vite génère un certificat local autosigné : accepter l’avertissement
du navigateur uniquement pour ce serveur de développement (ou utiliser un
certificat local approuvé). Ne pas désactiver `Secure` pour travailler en HTTP.
Le navigateur appelle `/api` sur la **même origine HTTPS** ; Vite retire ce
préfixe et relaie vers l’API, sans modifier `Origin` ni `Set-Cookie`.
`SHELF_API_TARGET` choisit la cible serveur (jamais une variable `VITE_` exposée
au client). Hors Docker, la cible par défaut est `http://localhost:5050`.

Pour un orb disposant déjà de Node et des dépendances, lancer un service
supervisé `pnpm --filter @shelf/web dev --host 0.0.0.0` sur le port 3000,
puis exposer sa cible HTTPS par le mécanisme de portail Amp. Configurer
`Authentication__ClientOrigin` avec l’origine exacte utilisée dans le navigateur :
l’origine du portail pour une validation via le portail, l’origine locale pour
une validation locale. Aucun CORS permissif ni repli Bearer n’est nécessaire.

En production, servir `dist/` avec repli SPA sur `index.html` et relayer
`/api/*` vers l’API en retirant `/api`, sous la même origine HTTPS. Préserver
les en-têtes `Origin`, `Cookie` et `Set-Cookie`, interdire la mise en cache
des réponses API, et configurer `Authentication__ClientOrigin` avec cette
origine exacte. `pnpm preview` n’est pas un serveur de production API.
Voir [le transport des sessions](../../docs/authentication.md#transport-des-sessions-web-et-mobile).

Le store de session Pinia ne conserve que le compte courant et son état. Les listes sont
locales à la page et effacées quand la session est vérifiée, refusée ou quittée.
Le cookie HttpOnly reste entièrement sous le contrôle du navigateur ; aucun
secret n’est écrit dans les stockages JavaScript. La session est revérifiée
au démarrage, au retour sur l’onglet et à l’actualisation de l’espace.
Sur les navigateurs modernes disposant de Web Locks (contexte HTTPS), les
connexions/déconnexions attendent les requêtes API des autres onglets pour qu’une
ancienne réponse ne remplace pas le nouveau cookie. Un marqueur aléatoire dans
localStorage, sans secret ni identité de compte, change sous verrou avant chaque
connexion/déconnexion. Les autres onglets masquent leurs données et revérifient
la session lorsqu’ils reçoivent cet événement. Chaque requête liée au compte
vérifie aussi le marqueur sous verrou : une action de l’ancienne session est
refusée avant envoi, même si l’événement n’est pas encore arrivé, sans être rejouée.
Le stockage local doit être accessible ; sinon les requêtes échouent sans envoi.
Sans Web Locks, la sérialisation entre onglets n’est pas garantie ; les contrôles
serveur restent actifs.

## Envoi d’un fichier

Ouvrir la racine ou un dossier personnel, puis « Envoyer un fichier ». Le store
`useTransfers` possède l’opération au-delà de la page : destination capturée,
progression et erreur restent consultables dans le suivi global. Il ne conserve
aucune liste. Les octets passent directement du navigateur au S3 par PUT signé,
avec les en-têtes fournis par l’API et sans cookies API (`withCredentials=false`).
Seules la déclaration et la confirmation utilisent le transport API habituel.

Configurer le [CORS MinIO local](../../infra/README.md#origine-du-navigateur-et-envois-directs)
sur la même origine HTTPS exacte que le web. Dans l’orb, `.amp/services.yaml`
fixe `https://localhost:3000` ; adapter cette valeur et redémarrer le service
local si le navigateur utilise une autre origine. L’endpoint S3 signé doit
être accessible au navigateur lui-même, pas seulement à l’API.

Après le PUT, « Finalisation » dure jusqu’à la réponse de confirmation serveur.
Seule une confirmation réussie marque le transfert terminé et actualise sa
destination si elle est affichée. Le fichier confirmé peut être téléchargé
en cliquant sur son nom. Une erreur de confirmation ou une réponse perdue
n’est pas un succès ; vérifier la destination avant un nouvel envoi. Un conflit
de nom ne remplace jamais silencieusement l’existant.

Le ticket serveur décide du PUT simple ou des parties à envoyer. Chaque partie
utilise ses numéros, URL et en-têtes, et la dernière ne contient que les octets
restants. Les listes de parties manquantes sont relues jusqu’à épuisement, sans
supposer qu’une seule réponse couvre le fichier. Une URL expirée ou refusée par
S3 est renouvelée une fois sans progrès ; un refus persistant reste une erreur,
sans boucle de réécriture. Une reprise qui redemande une partie déjà envoyée dans
la tentative courante s’arrête également en erreur.

« Réessayer » consulte d’abord la reprise serveur d’un transfert déclaré et ne
renvoie que les parties manquantes ou incorrectes. Les octets affichés concernent
la tentative courante, pas les parties déjà conservées par le serveur. « Arrêter »
interrompt uniquement les requêtes du fichier choisi et permet de reprendre tant
que son fichier reste en mémoire. Il ne révoque pas les URL signées, ne supprime
pas un fichier publié et ne déclenche pas un nettoyage S3 immédiat : le mécanisme
serveur existant nettoie ultérieurement les données temporaires.

Une finalisation interrompue ou dont la réponse a été perdue reste « résultat
inconnu ». Réessayer consulte le serveur ; un upload introuvable ne prouve ni
une publication ni son absence. Aucune nouvelle déclaration automatique ne
remplace ce résultat inconnu. Un refus d’accès arrête la reprise concernée.

Les fichiers et les requêtes restent uniquement en mémoire, hors état sérialisable
Pinia ; les URL signées restent dans la portée du transfert. Aucune file persistante,
reprise après rechargement ou nouvelle tentative automatique d’écriture après
reconnexion n’est promise.

Une vérification de session du même compte ne coupe pas l’envoi. À détection
d’une session refusée ou changée, les requêtes sont interrompues et les données
privées effacées. La déconnexion volontaire demande confirmation si un envoi
est actif. Cela ne revient pas sur une publication serveur déjà confirmée ;
interrompre une confirmation dont la réponse manque laisse son résultat inconnu.

`vp run typecheck` construit l'application avant `vue-tsc` : le plugin Nuxt UI
génère pendant le build les déclarations `components.d.ts` et `auto-imports.d.ts`
(ignorées par Git) dont dépend la vérification des types.
