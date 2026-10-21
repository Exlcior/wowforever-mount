"""Creature spawns for the mount from the public CMaNGOS Classic database (server data only).

The game client holds no NPC positions or behaviour: they come from the server. This reads the CMaNGOS
classic-db full dump (https://github.com/cmangos/classic-db, see its COPYRIGHT.md) and writes, gzip-compressed,
one line per spawn and one per creature kind to source/mount/wowforever/Data/spawns.txt.gz:

  T;entry;name;subname;minlevel;maxlevel;rank;npcflags;scale;displays;combat spells;idle spells;auras;mount;stand;emote;items
  S;map;entry;x;y;z;orientation;wander;auras;mount;stand;emote

Spells are what the server makes each creature cast (creature_spell_list, creature_template_spells and EventAI
casts): "combat" in a fight, "idle" out of combat on a timer. Auras, mount, stand state and looping emote come
from creature_template_addon and, per spawn, creature_addon; items are the equipped weapons (creature_equip_template).
Lists are comma separated and empty when there is none.

Models, textures, animations and spell visuals all come from the player's installed client through these ids.
Usage: python -B tools/mount_data/extract_spawns.py <ClassicDB_*.sql.gz>
"""
import gzip, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, 'source', 'mount', 'wowforever', 'Data', 'spawns.txt.gz')
TUPLE = re.compile(r"\((?:[^()'\\]|'(?:[^'\\]|\\.)*'|\\.)*\)")
VALUE = re.compile(r"'((?:[^'\\]|\\.)*)'|([^,]+)")
TABLES = ('creature', 'creature_template', 'game_event_creature', 'creature_template_spells', 'creature_spell_list',
          'creature_ai_scripts', 'creature_template_addon', 'creature_addon', 'creature_equip_template')


def columns(create):
    return [m.group(1) for m in re.finditer(r"^\s*`(\w+)`", create, re.M)]


def rows(statement):
    for t in TUPLE.finditer(statement):
        body = t.group(0)[1:-1]
        values = []
        for m in VALUE.finditer(body):
            if m.group(1) is not None: values.append(m.group(1).replace("\\'", "'").replace('\\"', '"'))
            else: values.append(m.group(2).strip())
        yield values


def auras(addon):
    return ','.join(a for a in (addon.get('auras') or '').split() if a.isdigit() and a != '0')


def clean(s):
    return '' if s in (None, 'NULL') else s.replace(';', ',').replace('\n', ' ')


def main(dump):
    tables, creates, current = {name: [] for name in TABLES}, {}, None
    with gzip.open(dump, 'rt', encoding='utf-8', errors='replace') as f:
        buffer = []
        for line in f:
            if line.startswith('CREATE TABLE `'):
                current = line.split('`')[1]; buffer = [line]; continue
            if current and buffer:
                buffer.append(line)
                if line.startswith(')'):
                    creates[current] = ''.join(buffer); buffer = []
                continue
            if line.startswith('INSERT INTO `'):
                name = line.split('`')[1]
                if name in tables: tables[name].append(line)
    cols = {name: columns(creates[name]) for name in tables}

    def table(name):
        for statement in tables[name]:
            for v in rows(statement): yield dict(zip(cols[name], v))

    combat, idle = {}, {}

    def add(spells, entry, spell):
        if spell not in ('', '0') and spell not in spells.setdefault(entry, []): spells[entry].append(spell)

    spell_lists = {}
    for r in table('creature_spell_list'): spell_lists.setdefault(r['Id'], []).append(r['SpellId'])
    for r in table('creature_template_spells'):
        for i in range(1, 11): add(combat, r['entry'], r.get(f'spell{i}', '0'))
    # EventAI: action 11 casts its first parameter. Event 1 is the out-of-combat timer, the others happen in a fight.
    for r in table('creature_ai_scripts'):
        for a in (1, 2, 3):
            if r[f'action{a}_type'] == '11': add(idle if r['event_type'] == '1' else combat, r['creature_id'], r[f'action{a}_param1'])
    addons = {r['entry']: r for r in table('creature_template_addon')}
    equipment = {r['entry']: r for r in table('creature_equip_template')}

    out = []
    kinds = with_spells = 0
    for r in table('creature_template'):
        displays = [r.get(f'ModelId{i}', '0') for i in range(1, 5)]
        displays = [d for d in displays if d not in ('0', '')]
        if not displays: continue
        entry = r['Entry']
        for spell in spell_lists.get(r.get('SpellList', '0'), []): add(combat, entry, spell)
        addon = addons.get(entry, {})
        equip = equipment.get(r.get('EquipmentTemplateId', '0'), {})
        items = [equip.get(f'equipentry{i}', '0') for i in (1, 2, 3)]
        out.append(';'.join(['T', entry, clean(r.get('Name')), clean(r.get('SubName')), r.get('MinLevel', '1'), r.get('MaxLevel', '1'),
                             r.get('Rank', '0'), r.get('NpcFlags', '0'), r.get('Scale', '1'), ','.join(displays),
                             ','.join(combat.get(entry, [])), ','.join(idle.get(entry, [])), auras(addon),
                             addon.get('mount', '0'), addon.get('stand_state', '0'), addon.get('emote', '0'),
                             ','.join(i for i in items if i not in ('', '0'))]))
        kinds += 1
        with_spells += bool(combat.get(entry) or idle.get(entry))
    # Seasonal spawns (fairs, holidays) only exist during their event: the everyday world leaves them out.
    events = {r['guid'] for r in table('game_event_creature') if int(r['event']) > 0}
    spawn_addons = {r['guid']: r for r in table('creature_addon')}
    count = 0
    for r in table('creature'):
        if r['guid'] in events: continue
        addon = spawn_addons.get(r['guid'], {})
        out.append(';'.join(['S', r['map'], r['id'], r['position_x'], r['position_y'], r['position_z'], r['orientation'],
                             r.get('spawndist', '0'), auras(addon), addon.get('mount', '0'), addon.get('stand_state', '0'), addon.get('emote', '0')]))
        count += 1
    with gzip.open(OUT, 'wt', encoding='utf-8', compresslevel=9) as f: f.write('\n'.join(out))
    print({'kinds': kinds, 'kinds_with_spells': with_spells, 'spawns': count, 'spawn_addons': len(spawn_addons),
           'event_spawns_skipped': len(events), 'bytes': os.path.getsize(OUT)})


if __name__ == '__main__':
    main(sys.argv[1])
