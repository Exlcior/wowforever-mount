# Personnages s&box et WoW

Dans l’explorateur, ouvrir le menu (**F9**), onglet **Personnage**, choisir **Personnage s&box** ou une apparence WoW,
puis reprendre l’exploration. Le changement conserve le personnage précédent pendant le
chargement. **Passer en vol libre** permet d’explorer sans collisions ; revenir en marche
attend le sol et les collisions proches avant de poser le joueur.

## Catalogue

`mount://wowforever/characters.prefab` expose `WowData`, une liste de `{ Key, Title, Path, Info, Race, Sex }`.
Les chemins sont `mount://wowforever/characters/<race>_<sexe>.vmdl` :

- Races : `human`, `dwarf`, `gnome`, `nightelf`, `orc`, `scourge`, `tauren`, `troll`, `bloodelf`, `draenei`.
- Sexe du fichier client : `male`, `female`.
- Séquences : `idle`, `walk`, `run`, `walk_back`, `jump`, `fall`, `land`, `swim_idle`, `swim`, `crouch_walk`, `mount`, `spell_ready`, `cast`.

Les modèles de base portent une peau, un visage et une tenue du client local. L'elfe de sang
et le draeneï utilisent les fichiers HD de cette installation.

## Personnalisation et équipement

Chaque personnage a `mount://wowforever/characters/<clé>.prefab` (`WowData`) : race et sexe du client,
sections de l'atlas (CharComponentTextureSections), options de ChrCustomization (choix, géosets,
couches de texture avec leur type, ordre, fusion et masque de sections) et, dans `BodyGroups`, les géosets
disponibles par groupe `g<groupe>`. Le modèle simple ne garde que les géosets par défaut ; le modèle
habillable `Dressable` (`characters/<clé>_dressable.vmdl`) garde tous les géosets, chacun avec son propre
matériau. Les groupes de corps du moteur ne servent pas : construits au chargement, leurs masques de
choix restent pleins et toutes les variantes s'afficheraient ensemble (ceinture, kilt, robe superposés).
Les matériaux sont nommés par type de texture et géoset (`_t1_` peau, `_t2_` cape, `_t6_` cheveux ;
`_g1302_` = groupe 13, variante 2). Les textures à composer sont servies lisibles en
`mount://wowforever/textures/<fdid>.vtex`.

`items.prefab` liste les objets portables (`Id`, `Name`, `Slot` = InventoryType, `Quality`, `Display`) ;
`items/<display>.prefab` décrit les modèles attachés par race et sexe (`items/models/<fichier>_<texture>.vmdl`),
les géosets, les pièces peintes par section et les groupes masqués par les casques.

`Code/WowAppearance.cs` applique tout cela à un `SkinnedModelRenderer` : composer l'atlas, choisir les
géosets montrés (un matériau transparent masque les autres), remplacer les textures et attacher les objets aux points d'attache. L'équipement reste le même
quand on change de race ou de sexe : chaque pièce prend la variante faite pour le nouveau modèle.
Les FileDataID sont explicités dans `source/mount/wowforever/Data/characters.json`.

## Dans un autre jeu s&box

Activer le mount `wowforever` dans le projet et charger le modèle dans un
`SkinnedModelRenderer`. Charger les ressources avec `ResourceLibrary.LoadAsync<Model>` ;
partager les tâches en cours comme le fait `Code/MountLoads.cs` évite une seconde demande
bloquante sur la même ressource. Les cartes de la mount utilisent le joueur du jeu hôte.

Pour un `PlayerController` natif, réutiliser `Code/WowCharacterAnimator.cs`, renseigner
`Renderer` et `Controller`, désactiver `UseAnimatorControls` et laisser l’adaptateur choisir
la séquence. Avec un contrôleur personnel, renseigner `Velocity`, `Facing`, `Grounded`,
`Crouching` et `Swimming`. Le modèle est orienté de -90 degrés autour de Z par l’adaptateur.
Le Citizen et ses vêtements utilisent toujours leur animation native s&box.

Les os WoW ne correspondent pas au squelette Citizen : le simple changement de chemin
dans un jeu qui impose l’animgraph Citizen ne suffit pas. Les points d’attache `wow_<index>`
suivent les pivots du squelette source ; les os `skin_<index>` portent les transformations
de déformation. Les animations sont échantillonnées à 24 Hz et interpolées par le moteur.
La capsule du contrôleur assure la collision du joueur ; les modèles n’incluent pas de ragdoll.
Les séquences de nage sont disponibles, mais l’explorateur n’ajoute pas encore de volumes
physiques de nage aux surfaces d’eau. Les animations de combat et la synchronisation
multijoueur d’un jeu hôte restent à intégrer par ce jeu.

## Validation et provenance

Lecteurs adaptés des structures de wow.export (MIT) : M2, SKEL, AFID et pistes de bones.
Atlas corps/visage/tenue selon CharComponentTextureSections, layouts 1 et 2. Les modèles
et textures proviennent uniquement du client installé. Les fichiers de données WoW ne
sont pas ajoutés à Assets ni publiés. Preuves natives et contrôle des 20 squelettes :
`validation/2026-10-18-playable-mount/`.
