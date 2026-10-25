# Application web

Vue 3, Vue Router et Nuxt UI (plugin Vite, sans Nuxt), thème et composants
partagés fournis par `@shelf/ui`. Connexion persistante, invitations et liste
de la racine de l’espace personnel ; la navigation dans les dossiers et les
transferts seront ajoutés séparément.

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

Pinia ne conserve que le compte courant et l’état de session. La liste personnelle
reste locale à la page et est rechargée quand la session est vérifiée.
Le cookie HttpOnly reste entièrement sous le contrôle du navigateur ; aucun
secret n’est écrit dans les stockages JavaScript. La session est revérifiée
au démarrage, au retour sur l’onglet et à l’actualisation de l’espace.

Les administrateurs accèdent à `/administration/invitations` depuis une navigation
distincte de l’espace personnel. Création, liste et révocation utilisent la même
session et les contrôles serveur existants. Le lien secret est affiché seulement
à la création, avec copie et sélection manuelle en secours ; quitter ou recharger
la page le perd. Une nouvelle invitation révoque les anciennes pour cet email.
Aucun email n’est envoyé automatiquement.

Pendant une revérification de la même session, la page Administration est masquée
mais conserve son formulaire et son éventuel lien en mémoire, y compris après
une erreur réseau. Ils sont détruits à la déconnexion, au rejet de session ou
au changement de compte ; le retour d’une vérification ne rejoue aucune écriture.

La page `/accept-invitation` lit `#token=…`, retire immédiatement le fragment de
l’historique et garde le secret uniquement en mémoire locale à la page. Après un
rechargement, rouvrir le lien original. Aucun email n’est demandé ou déduit du
token : le serveur impose celui de l’invitation. La création du compte par
`/auth/web/register` précède la connexion par `/auth/web/login`. Si seule la
connexion échoue, la page confirme le compte créé et propose la connexion,
sans nouveau formulaire d’inscription. Une réponse perdue pendant la création
reste une issue incertaine, jamais un succès supposé ; aucune écriture n’est
réessayée automatiquement. Les formulaires ne quittent pas la mémoire de leur page.
La navigation interne est bloquée pendant l’inscription et sa connexion pour
éviter qu’elles ne continuent après un départ volontaire. Un changement de session
dans un autre onglet empêche également la connexion automatique tardive.

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

`vp run typecheck` construit l'application avant `vue-tsc` : le plugin Nuxt UI
génère pendant le build les déclarations `components.d.ts` et `auto-imports.d.ts`
(ignorées par Git) dont dépend la vérification des types.
