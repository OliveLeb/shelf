# Issue tracker: GitHub

Les issues et spécifications de Shelf sont publiées dans GitHub Issues du dépôt `OliveLeb/shelf`.
Utiliser le CLI `gh` pour lire, créer et mettre à jour les issues. Pour les descriptions multilignes,
écrire le contenu dans un fichier temporaire et utiliser `--body-file`.

- Lire une issue : `gh issue view <number> --comments`.
- Lister les issues : `gh issue list --state open --json number,title,body,labels`.
- Créer une issue : `gh issue create --title <title> --body-file <path>`.
- Appliquer un label : `gh issue edit <number> --add-label <label>`.
- Publier une spécification signifie créer une issue GitHub ; un brouillon local ne constitue pas une publication.

## Branche et PR brouillon par issue

Travailler sur une issue signifie créer une branche dédiée (ex. `feat/authentication`, `fix/session-expiry`).
Dès que la branche est créée, ouvrir une **PR brouillon** liée à l'issue :

```sh
gh pr create --draft --fill-first --base main
```

- Inclure `Closes #<number>` dans la description de la PR.
- La PR est mise à jour au fil des commits ; elle quitte l'état brouillon quand le travail est prêt pour relecture.

**PRs as a request surface: no.**
