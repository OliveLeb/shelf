## Agent skills

### Issue tracker

Les issues et spécifications sont suivies dans GitHub Issues pour OliveLeb/shelf. Voir `docs/agents/issue-tracker.md`.

### Triage labels

Les cinq labels de triage standards sont utilisés. Voir `docs/agents/triage-labels.md`.

### Domain docs

La documentation de domaine suit un contexte unique à la racine. Voir `docs/agents/domain.md`.

### Conventional commits

Les messages de commit suivent Conventional Commits et sont rédigés en anglais. Voir `docs/agents/commits.md`.

### Branches et PR brouillon

Chaque issue travaillée a sa branche dédiée ; une PR brouillon est ouverte dès la création de la branche. Voir `docs/agents/issue-tracker.md`.

### Contrat API

Toute route HTTP ajoutée, modifiée ou supprimée met à jour `contracts/openapi.json` et la collection Bruno `contracts/bruno/` dans le même commit (`./contracts/generate.sh`). Voir `docs/agents/api-contract.md`.
