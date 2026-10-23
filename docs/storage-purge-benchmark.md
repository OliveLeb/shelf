# Purge de la corbeille — validation de #35

## Résultat et limites

Mesure SQL jetable réalisée dans un orb le 23 octobre 2026, sur **PostgreSQL
15.19 (Debian 15.19-0+deb12u1), x86_64, compilé avec GCC 12.2.0**.
Le serveur local désactive `fsync`, `synchronous_commit` et `full_page_writes` :
ces chiffres ne prédisent pas la latence d'une instance de production.

**PostgreSQL 18 n'a pas été exécuté** : seul PostgreSQL 15 est installé dans
cet orb, le paquet 18 n'est pas proposé par ses dépôts configurés et aucun
daemon Docker n'y est disponible. Le déploiement configure PostgreSQL 18 ;
il faut y refaire cette mesure dans une base jetable avant toute extrapolation.

La migration `011_parent_indexes.sql` ajoute deux index B-tree **non partiels**
sur `(parent_id, space_id)` : `elements_parent_space` et `uploads_parent_space`.
Le premier couvre aussi les éléments en corbeille, exclus de l'index d'unicité
des noms actifs. Le second couvre la clé étrangère des envois en attente :
la purge détache leur espace, sans perdre les clés nécessaires au nettoyage
après expiration des URL signées. Les anciennes migrations, contraintes,
routes HTTP, traitements de purge et protocoles S3 sont inchangés.

La création d'index ordinaire prend les verrous PostgreSQL habituels et ajoute
un coût disque/écriture. Le migrateur est transactionnel : pas de `CONCURRENTLY`
dans cette migration. Aucune migration de base partagée/production n'a été faite.

## Jeu et protocole

- Un dossier en corbeille avec 1 000 fichiers, chacun avec une miniature.
- 20 000 fichiers actifs sans rapport, avec leurs miniatures, dans le même espace.
- 10 envois en attente vers le dossier, plus 2 000 autres envois sans rapport.
- Schéma réel, toutes les contraintes et tous les triggers activés.
- Trois échantillons avant et trois après ; chaque suppression est annulée
  pour conserver le même jeu logique. Les sondes parent/espace précèdent chaque
  suppression et réchauffent aussi la base avant la première mesure.
- Un contrôle inverse retire ensuite les deux index et remesure, à cache chaud.
- Les sondes `SELECT` montrent les chemins d'accès possibles pour les mêmes
  prédicats que les clés étrangères ; elles ne prétendent pas tracer les requêtes
  internes des triggers. Les temps des triggers du vrai `DELETE` mesurent les cascades.
- Pas de transfert S3 dans cette mesure SQL : les tests HTTP/MinIO vérifient
  séparément la suppression effective et la reprise. Aucun seuil CI ajouté.

| Échantillon | Avant (ms) | Après (ms) |
| --- | ---: | ---: |
| 1 | 1 883,224 | 88,609 |
| 2 | 1 809,777 | 94,031 |
| 3 | 1 862,928 | 89,238 |

La médiane passe de **1 862,928 à 89,238 ms** (environ ×20,9).
Au troisième échantillon, le trigger `elements_parent` passe de 1 632,421
à 28,759 ms pour **1 001 appels dans les deux cas** ; `uploads_parent` passe
de 161,831 à 21,444 ms, également pour 1 001 appels.
Après retrait des index, la purge remonte à **1 713,193 ms**
(`elements_parent` : 1 494,879 ms ; `uploads_parent` : 149,836 ms).
Le retour du coût après retrait, les buffers déjà en cache avant indexation
et le remplacement des parcours séquentiels par des parcours indexés distinguent
la réduction du travail des cascades d'un simple effet de cache/latence.

Dans chaque transaction, avant annulation : **20 000 éléments conservés,
2 000 objets en file durable** (1 000 fichiers + 1 000 miniatures), **10 envois
détachés**. L'application via la commande réelle `migrate`, puis sa répétition,
a aussi été vérifiée sur ce schéma préexistant renseigné : toujours 20 000
éléments actifs et 1 001 en corbeille, version 11 enregistrée une seule fois.

## Reproduction (uniquement sur une instance jetable)

Depuis la racine, avec les outils PostgreSQL disponibles et un rôle local
autorisé à créer une base. Adapter `PGHOST`, `PGPORT` et `PGUSER` pour répéter
sur PostgreSQL 18. **Ne jamais viser une base partagée ou de production.**

```sh
export PGHOST=localhost PGUSER=shelf
DB="shelf_bench_35_$(date +%s)_$$"
createdb "$DB"
for migration in apps/backend/Shelf.Api/Migrations/*.sql; do
  case "$migration" in */011_*) break ;; esac
  psql -X -v ON_ERROR_STOP=1 -d "$DB" -f "$migration" || exit 1
done

psql -X -v ON_ERROR_STOP=1 -d "$DB" <<'SQL'
INSERT INTO accounts (id, username, email, password_hash)
VALUES (md5('owner')::uuid, 'owner', 'owner@example.test', 'not-a-real-hash');
INSERT INTO spaces (id, owner_id) VALUES (md5('space')::uuid, md5('owner')::uuid);
INSERT INTO trash_entries (id, space_id, original_path, deleted_at)
VALUES (md5('trash')::uuid, md5('space')::uuid, '/', now());
INSERT INTO elements (id, space_id, kind, name, added_at, trash_id)
VALUES (md5('folder')::uuid, md5('space')::uuid, 'folder', 'purge-me', now(), md5('trash')::uuid);
INSERT INTO elements (id, space_id, parent_id, kind, name, size, content_type, object_key, added_at, trash_id, thumbnail_state, thumbnail_key)
SELECT md5('file-' || n)::uuid, md5('space')::uuid,
    CASE WHEN n <= 1000 THEN md5('folder')::uuid END,
    'file', 'file-' || n, 8, 'image/jpeg', 'files/' || n, now(),
    CASE WHEN n <= 1000 THEN md5('trash')::uuid END, 'available', 'thumbnails/' || n
FROM generate_series(1, 21000) n;
INSERT INTO uploads (id, space_id, account_id, parent_id, name, size, content_type, object_key, created_at, part_size)
SELECT md5('upload-' || n)::uuid, md5('space')::uuid, md5('owner')::uuid,
    CASE WHEN n <= 10 THEN md5('folder')::uuid END,
    'upload-' || n, 8, 'image/jpeg', 'uploads/' || n, now(), 8
FROM generate_series(1, 2010) n;
ANALYZE;
SQL

measure() {
  psql -X -v ON_ERROR_STOP=1 -d "$DB" <<'SQL'
SELECT version();
EXPLAIN (ANALYZE, BUFFERS) SELECT id FROM elements
WHERE parent_id=md5('folder')::uuid AND space_id=md5('space')::uuid;
EXPLAIN (ANALYZE, BUFFERS) SELECT id FROM uploads
WHERE parent_id=md5('folder')::uuid AND space_id=md5('space')::uuid;
BEGIN;
EXPLAIN (ANALYZE, BUFFERS) DELETE FROM trash_entries WHERE id=md5('trash')::uuid;
SET CONSTRAINTS ALL IMMEDIATE;
SELECT (SELECT count(*) FROM elements) AS retained_elements,
       (SELECT count(*) FROM object_deletions) AS queued_objects,
       (SELECT count(*) FROM uploads WHERE space_id IS NULL) AS detached_uploads;
ROLLBACK;
SQL
}
for run in 1 2 3; do measure; done
psql -X -v ON_ERROR_STOP=1 -d "$DB" -f apps/backend/Shelf.Api/Migrations/011_parent_indexes.sql
for run in 1 2 3; do measure; done
psql -X -v ON_ERROR_STOP=1 -d "$DB" -c 'DROP INDEX elements_parent_space; DROP INDEX uploads_parent_space;'
measure
dropdb "$DB"
```

## Plans EXPLAIN ANALYZE représentatifs

Extraits du troisième échantillon (les UUID littéraux des conditions et les
buffers de planification sont omis ; temps, lignes, buffers d'exécution et
triggers ci-dessous sont ceux mesurés, pas des attentes à figer en CI).

### Avant

```text
Seq Scan on elements (cost=0.00..723.01 rows=1000 width=16) (actual time=0.015..2.492 rows=1000 loops=1)
  Filter: parent_id = folder AND space_id = space
  Rows Removed by Filter: 20001
  Buffers: shared hit=408
Planning Time: 0.372 ms
Execution Time: 2.533 ms

Seq Scan on uploads (cost=0.00..67.15 rows=10 width=16) (actual time=0.005..0.197 rows=10 loops=1)
  Filter: parent_id = folder AND space_id = space
  Rows Removed by Filter: 2000
  Buffers: shared hit=37
Planning Time: 0.101 ms
Execution Time: 0.202 ms

Delete on trash_entries (cost=0.00..1.01 rows=0 width=0) (actual time=0.050..0.050 rows=0 loops=1)
  Buffers: shared hit=6
  -> Seq Scan on trash_entries (cost=0.00..1.01 rows=1 width=6) (actual time=0.003..0.003 rows=1 loops=1)
       Filter: id = trash
       Buffers: shared hit=1
Planning Time: 0.096 ms
Trigger for constraint elements_trash_id_fkey on trash_entries: time=1.084 calls=1
Trigger for constraint elements_parent on elements: time=1632.421 calls=1001
Trigger for constraint shares_element_id_fkey on elements: time=13.326 calls=1001
Trigger for constraint uploads_parent on elements: time=161.831 calls=1001
Trigger elements_object_deletion on elements: time=39.172 calls=1001
Trigger elements_thumbnail_deletion on elements: time=14.369 calls=1001
Execution Time: 1862.928 ms
```

### Après

```text
Bitmap Heap Scan on elements (cost=14.54..437.54 rows=1000 width=16) (actual time=0.047..0.224 rows=1000 loops=1)
  Recheck Cond: parent_id = folder AND space_id = space
  Heap Blocks: exact=24
  Buffers: shared hit=26
  -> Bitmap Index Scan on elements_parent_space (cost=0.00..14.29 rows=1000 width=0) (actual time=0.029..0.029 rows=1000 loops=1)
       Index Cond: parent_id = folder AND space_id = space
       Buffers: shared hit=2
Planning Time: 0.485 ms
Execution Time: 0.273 ms

Index Scan using uploads_parent_space on uploads (cost=0.28..22.48 rows=10 width=16) (actual time=0.008..0.009 rows=10 loops=1)
  Index Cond: parent_id = folder AND space_id = space
  Buffers: shared hit=3
Planning Time: 0.121 ms
Execution Time: 0.016 ms

Delete on trash_entries (cost=0.00..1.01 rows=0 width=0) (actual time=0.044..0.044 rows=0 loops=1)
  Buffers: shared hit=6
  -> Seq Scan on trash_entries (cost=0.00..1.01 rows=1 width=6) (actual time=0.004..0.004 rows=1 loops=1)
       Filter: id = trash
       Buffers: shared hit=1
Planning Time: 0.097 ms
Trigger for constraint elements_trash_id_fkey on trash_entries: time=1.148 calls=1
Trigger for constraint elements_parent on elements: time=28.759 calls=1001
Trigger for constraint shares_element_id_fkey on elements: time=3.765 calls=1001
Trigger for constraint uploads_parent on elements: time=21.444 calls=1001
Trigger elements_object_deletion on elements: time=22.447 calls=1001
Trigger elements_thumbnail_deletion on elements: time=11.209 calls=1001
Execution Time: 89.238 ms
```

## Validation des critères d'acceptation

- Migration additive : seuls deux index non partiels sont créés ; aucune
  contrainte ni migration antérieure modifiée.
- Installation neuve : chaque test HTTP crée sa propre base et lance la commande
  réelle `migrate`. Mise à niveau renseignée et deuxième passage vérifiés séparément.
- Mesure reproductible : le bloc shell ci-dessus a été extrait du document et
  réexécuté avec succès, avec les mêmes comptes de conservation/file durable.
- Réduction du travail : plans, temps des triggers et contrôle inverse ci-dessus ;
  limite PostgreSQL 18 déclarée, sans extrapolation.
- `dotnet test apps/backend/Shelf.slnx --no-restore --filter 'FullyQualifiedName~TrashTests|FullyQualifiedName~AccountStorageTests|FullyQualifiedName~CopyTests|FullyQualifiedName~ThumbnailTests'`
  : **46 réussis, 0 échec, 0 ignoré**. Couvre purge manuelle, corbeille vidée,
  expiration à trente jours, suppression de compte, panne S3 et reprise durable,
  restauration, droits, copies et protection des fichiers conservés.
- `dotnet test apps/backend/Shelf.slnx --no-restore`
  : **159 réussis, 0 échec, 0 ignoré**, durée 2 min 12 s. PostgreSQL et MinIO réels.
- Contrat généré identique après compilation ; aucune route modifiée, aucun
  benchmark, plan exact ou seuil chronométrique ajouté à la CI.
- Revue Standards et Spec : aucun écart identifié sur les deux axes.
