# Mount natif World of Warcraft Forever

Le jeu ne contient plus aucune donnée de World of Warcraft. Le module `wowforever`
(`source/mount/wowforever/`) lit l'installation du joueur (stockage CASC local, produit
`wowf`) et convertit à la demande. Comme le client, il télécharge depuis le CDN de Blizzard,
pour la version installée, les fichiers que le lanceur n'a pas encore écrits (une partie des
textures après une installation « jouable »), puis les garde dans
`%LOCALAPPDATA%/sbox-wowforever/cdn`. Choix validé par Exl le 17 octobre 2026.

## Tables du client

Les tables (WDC5) sont lues à l'exécution dans l'installation par `Core/Storage/Db2.cs` : champs numériques,
tableaux, données communes et palettes, cartes de relations et chaînes. Les numéros de champs suivent les
définitions WoWDBDefs (MIT) de la version installée. Les sections chiffrées sans clé publique sont ignorées.

## Données externes

Le client ne contient ni la position ni le comportement des PNJ et des monstres (le serveur les gère). Seules ces
données viennent de la base publique Classic de CMaNGOS (`Data/spawns.txt.gz` : 63 303 placements et 10 383 types de
créatures, événements saisonniers exclus ; sorts en combat et hors combat, auras, monture, pose et armes équipées ;
voir son COPYRIGHT.md, usage non commercial), régénérée par `tools/mount_data/extract_spawns.py`. Apparences,
textures, animations, effets de sort, objets et noms affichés viennent du client.

Le mount charge aussi les polices d'interface du client (Friz Quadrata TT, Morpheus, Skurri, Arial Narrow) dans le
gestionnaire de polices du moteur : les interfaces les utilisent par leur nom de famille. Elles sont marquées comme
polices de menu pour survivre au chargement d'un jeu, qui vide les autres.

## Langue

Le mount lit la langue de texte installée par le launcher (`frFR text?` dans les tags de `.build.info`, sinon
`enUS`) et ouvre les fichiers localisés de cette langue : noms d'objets, de sorts, de montures, de zones, de cartes,
de races, de classes et options de personnalisation viennent du client dans cette langue. `strings.prefab` sert toutes
les chaînes d'interface du client (GlobalStrings, par étiquette : `BACK`, `CUSTOMIZE`, `HEADSLOT`...) et
`places.prefab` les lieux nommés par leur zone, `bestiary.prefab` tous les monstres et PNJ (base CMaNGOS) pour le bac à sable. Les noms des PNJ font exception : le client n'en porte presque aucun
(ils viennent du serveur), ils restent ceux de la base CMaNGOS, en anglais.

## Détection

`SBOX_WOWFOREVER_DIR`, puis les clés de registre Blizzard, puis les dossiers usuels de
chaque disque (dont `Programmes/World of Warcraft`). Le dossier doit contenir `.build.info`
avec le produit `wowf` et `Data/data`. La version lue est celle installée (1.60.1.70245 au
17 octobre 2026), pas une version figée.

## Ressources exposées (`mount://wowforever/...`)

| Chemin | Contenu |
| --- | --- |
| `world.prefab` | Catalogue des 62 cartes et 3 038 tuiles (JSON dans la métadonnée `WowData`) |
| `maps/<carte>/<tuile>/terrain.vmdl` | Relief détaillé, couches MCAL d'origine et liquides |
| `maps/<carte>/<tuile>/terrain_lod1.vmdl`, `terrain_lod2.vmdl` | Relief lointain (1 sommet sur 4, puis 16) avec la texture de carte du client |
| `maps/<carte>/<tuile>/placements.prefab` | Placements M2/WMO des fichiers obj0 et obj1 |
| `models/<FileDataID>.vmdl` et `.prefab` | Modèle M2 ou WMO et sa description (limites, décors internes, émetteurs de particules) |
| `models/<FileDataID>/particle<0-3>.vtex` | Planches de sprites des particules du modèle, non compressées pour être découpées |
| `characters/<race>_<sexe>.vmdl` et `.prefab` | 20 personnages jouables : squelette, séquences, géosets en groupes de corps ; options de personnalisation du client. Voir [characters.md](characters.md) |
| `characters.prefab` | Catalogue JSON `WowData` : `Key`, `Title`, `Path`, `Info`, `Race`, `Sex` |
| `items.prefab`, `items/<display>.prefab` | 6 559 objets portables et la description de leur apparence par race et sexe |
| `items/models/<fichier>_<texture>.vmdl` | Modèles attachés des objets (armes, boucliers, casques, épaulières), animés |
| `textures/<FileDataID>.vtex` | Textures de personnalisation et d'armure, non compressées pour composer la peau |
| `objets/<catégorie>/<nom>_<id>.vmdl` | Les objets à modèle du client (armes, boucliers, casques, épaulières...), dans le menu de spawn |
| `glues/ui_<race>.vmdl` et `.prefab` | Décors de création de personnage du client (dans le menu de spawn) ; caméra et point où se tient le personnage |
| `ui/<chemin>.vtex` | Textures d'interface des écrans du client (boutons, cadres, flèches) |
| `ui/atlas/<nom>.vtex`, `ui/atlases.prefab` | Pièces d'atlas d'interface du client (UiTextureAtlasMember) : icônes de race et de classe, anneaux, cadres de bronze, boutons rouges ; tailles |
| `models/<FileDataID>_collision.vmdl` | Géométrie visible à LOD natifs et collision statique précise, pour le streaming |
| `maps/<carte>/<tuile>/terrain_collision.vmdl` | Terrain détaillé avec collision triangulée |
| `ambience.prefab` | Lumières par zone (positions Classic publiques, couleurs Forever) |
| `models/<FileDataID>_lod1.vmdl`, `_lod2.vmdl` | Versions réduites livrées par le client (skins LOD des M2, groupes LOD des WMO) pour les copies lointaines |
| `world/.../<nom>.vmdl` | Les 62 000 modèles du monde installés, sous leur nom client et avec collisions : l'arbre du menu de spawn Sandbox |
| `creatures/<modèle>/<modèle>_<display>.vmdl` | 14 132 apparences de créatures et montures (CreatureDisplayInfo), animées : jusqu'à 29 séquences (marche, course, attaque, sort, mort, nage, monture…) |
| `npcs/<race>/<sexe>/<modèle>_<display>.vmdl` | PNJ humanoïdes avec la tenue pré-dessinée par le client (CreatureDisplayInfoExtra → TextureFileData) |
| `creatures.prefab`, `mounts.prefab` | Catalogues JSON : apparences (chemin, échelle) ; montures (nom du client, apparences) |
| `maps/<carte>/<tuile>/creatures.prefab` | PNJ et monstres de la tuile : nom, niveau, modèle, position, orientation, rayon de promenade, sorts, auras, monture, pose, objets |
| `maps/<carte>/<tuile>/areas.prefab` | Zone et sous-zone de chaque chunk de terrain (AreaTable) pour les bannières de zone |
| `loadingscreens.prefab`, `loadingscreens/<carte>.vtex` | Écran de chargement de chaque carte (ceux de Forever pour les continents) |
| `spells.prefab`, `spells/<id>.prefab` | 7 321 noms de sorts ; phases (préparation, lancer, projectile, impact, aura, canalisation) et modèles d'effet par point d'attache |
| `effects/<FileDataID>.vmdl` et `.prefab` | Modèles d'effet de sort animés, leurs émetteurs de particules (sur les os) et leurs rubans |
| `maps/<continent>/<lieu>.scene` | 11 lieux jouables dans n'importe quel jeu, dont le mode Sandbox : terrain marchable, décors, intérieurs, lumière, ciel, brouillard, particules, point d'apparition |

Tout ce qui n'est pas destiné au joueur (catalogue, tuiles, descriptions, textures et sprites de particules) est marqué
`DeveloperOnly` : visible dans l'explorateur d'assets, absent du menu de spawn. Les shaders de la mount
(`source/mount/wowforever/Assets/shaders/wowforever/`) sont installés à côté de la DLL, comme pour les mounts
officielles (Quake, GoldSrc) ; le projet n'en contient plus aucun.

L'index des modèles est mis en cache par version dans `%LOCALAPPDATA%/sbox-wowforever/`.
Lecture et décodage se font sur des threads de travail ; le fil principal ne fait que créer
les objets natifs. Une lecture du stockage qui échoue en parallèle est reprise seule ; un
fichier ou une texture introuvable n'est jamais gardé en cache, pour qu'un essai suivant
(après un téléchargement CDN, par exemple) le trouve. Les shaders appartiennent au module et sont installés avec lui.

## Fidélité vérifiée

Hors moteur, sur la tuile 32_48 des Royaumes de l'Est : les 33 000 sommets du relief
correspondent à l'ancien export à 0,00002 près ; les 793 placements obj0 ont les mêmes
modèles et positions (écart 0,001) ; un WMO (136 parties, 144 décors) et un M2 ont les mêmes
limites. Les lecteurs sont adaptés de wow.export (MIT) et TACTSharp (MIT).

## Installer, retirer

```powershell
python -B tools/install_wowforever_mount.py install   # compile et copie dans <s&box>/mount/wowforever
python -B tools/install_wowforever_mount.py remove
```

Le moteur charge les mounts au démarrage : une instance déjà ouverte garde sa version.
Relancer uniquement les instances de ce projet à mettre à jour, après sauvegarde ; préserver les autres travaux. Le projet déclare `"Mounts": ["wowforever"]`.

## Limites

- Herbes GroundEffect : générées par le mount avec le même tirage que l'ancien export
  (340/340 identiques sur la tuile 32_48). Le terrain et les bâtiments servent leurs triangles de collision ; les objets mobiles utilisent les enveloppes convexes du maillage de collision M2, sans rendre les feuillages solides.
- Animations : les transformations de texture des M2 sont lues comme une vitesse de
  défilement constante (piste de translation, séquence globale ou première animation) ;
  les particules (structure M2Particle de wowdev.wiki, taille d'enregistrement détectée)
  reprennent la première clé de chaque piste. Les squelettes des 20 personnages sont animés ; les autres M2 du monde restent dans leur pose de repos.
- Le moteur prend les mips d'une texture du plus petit au plus grand ; les DXT des BLP sont
  envoyés tels quels dans cet ordre. Ne jamais redemander une ressource dont le chargement
  est en cours (blocage du fil principal) : passer par `Code/MountLoads.cs`.
- Les tables Map et LiquidType utiles sont compilées dans le module
  (`tools/mount_data/generate_mount_tables.py`) ; les géométries et textures viennent
  toutes de l'installation.
- Lieux jouables : construits au chargement (≈25 s pour Comté-de-l'Or, 3 500 objets), sans streaming ; les petits
  objets se limitent à 220 yards, les collisions de décors à 650, le terrain détaillé à 750, les grands décors à 2 200 et les bâtiments à 2 600 yards du point d'apparition. L’horizon atteint 5 000 yards. Ces scènes sont des lieux locaux, sans streaming autour d’un joueur qui en sortirait ; l’explorateur assure ce streaming. Le paquet
  `facepunch.sandbox` ne se chargeait pas sur cette machine le 17 octobre (erreur de liste blanche du paquet, sans lien
  avec la mount) : la scène a été validée avec le `PlayerController` du moteur posé au point d'apparition.
- Chargements synchrones (un jeu qui charge une carte par `Scene.LoadFromFile`, le menu de spawn, `Model.Load`) :
  les chargeurs le détectent et construisent tout sur place, sans attendre d'images ; auparavant le fil principal
  se bloquait (s&box figé sans erreur). Vérifié dans le jeu `pcjr.yasbox` : Comté-de-l'Or en 28 s avec ses particules.
  Les sprites de particules portent leur chemin pour survivre à la sérialisation des scènes.
- Textures DXT1 à transparence 1 bit : décodées en RGBA (le GPU les lisait opaques, lianes noires).
- Les anciennes données converties sont archivées hors d'Assets dans
  `archive/converted-assets-2026-10-16/` (supprimables après validation).

## Performances (17 octobre 2026, fenêtre au premier plan, aucune autre instance)

Arrivée en ~13 s ; 132 i/s à l'arrêt. Vol à 12 yards/s : P50 6,9 ms, P95 10 ms, 20 images
au-delà de 33 ms et aucune au-delà de 50. À 40 yards/s : P95 36 ms ; à 160 : P95 45 ms.
La création des objets du moteur est limitée à 4 ms par image et découpée en étapes.
Preuves : `validation/2026-10-17-mount/`. Après la correction des lectures et l'ajout des
particules (Orgrimmar) : P95 9,5 ms et aucune image au-delà de 33 ms à 12 yards/s
(`validation/2026-10-17-effects/`).

## Révision du 18 octobre 2026

Le chargement des bâtiments lointains examine désormais les tuiles de leur portée complète.
Les modèles nommés disposent de LOD natifs ; la collision est indépendante du LOD visuel.
Les triangles WMO marqués collision seulement sont conservés, les triangles sans collision
ne créent pas de murs invisibles. Un groupe WMO sans LOD disponible conserve sa version détaillée.
Les villes Forever utilisent aussi `MPY2` (flags/matériau sur 16 bits) et `MOVX`
(indices sur 32 bits), désormais lus avec les anciens `MOPY`/`MOVI`. Les règles de
collision ont été recoupées avec les structures décrites dans
[l'extracteur WMO de TrinityCore](https://github.com/TrinityCore/TrinityCore/blob/master/src/tools/vmap4_extractor/wmo.cpp).
Orgrimmar fournit ainsi 138 102 triangles de collision, vérifiés par les tests du lecteur
et une trace de sol native. Les groupes inaccessibles et antiportails ne deviennent pas solides.
Les contrôles de cette révision et leurs limites sont dans [current-state.md](current-state.md).

## Écrans de chargement et créatures

`loadingscreens.prefab` donne pour chaque carte l'image de son écran (Map → LoadingScreens), son logo et les pièces
du cadre de WoW Forever (LoadingScreenSkin : panneaux de cuir, reliure dorée, bande cousue). L'agencement du cadre
est déduit de ses images, le client le dessine dans son code natif. Les modèles de créatures du menu de spawn portent
toutes leurs animations (noms d'AnimationData repris de wow.export, MIT) ; les PNJ du monde utilisent une copie
légère (`_npc`). Les modèles fixes sont dessinés dans leur pose de repos (premier instant de leur première séquence) ; particules, rubans et lumières suivent la même pose. Les cartes portées par un os qui fait face à la caméra (halos des lanternes et des torches) deviennent des halos orientés vers la caméra (`Billboards`), au lieu de plans fixes qui coupaient l'effet.
