"""Lista las cadenas en español usadas como clave de localización y cuáles carecen de traducción al inglés."""
import re, glob, sys, json
keys = set()
pat_cs = [r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"', r'Form\.(?:Text|Button|Card|Row|ToggleRow|Tile)\(\s*(?:"\\u[0-9A-Fa-f]{4}",\s*)?"((?:[^"\\]|\\.)*)"',
          r'W\.(?:Btn|Label)\(\s*"((?:[^"\\]|\\.)*)"', r'Ui\.(?:Confirm|Info|Error|Prompt)\(\s*"((?:[^"\\]|\\.)*)"', r'Form\.Tr\([^;]*?,\s*"((?:[^"\\]|\\.)*)"\)',
          r'\(\s*[^()"]*?,\s*"((?:[^"\\]|\\.)*)"\)']
for f in glob.glob('src/**/*.cs', recursive=True):
    if '/obj/' in f or 'English.cs' in f: continue
    s = open(f, encoding='utf-8').read()
    for p in pat_cs[:5]:
        for m in re.finditer(p, s): keys.add(m.group(1).encode().decode('unicode_escape').encode('latin-1').decode('utf-8') if '\\' in m.group(1) else m.group(1))
for f in glob.glob('src/**/*.xaml', recursive=True):
    s = open(f, encoding='utf-8').read()
    for m in re.finditer(r"\{l:T\s+'([^']*)'\}", s): keys.add(m.group(1))
    for m in re.finditer(r"\{l:T\s+([^'}][^}]*)\}", s): keys.add(m.group(1).strip())
keys = sorted(k for k in keys if re.search(r'[A-Za-zÁÉÍÓÚáéíóúñ]{2}', k))
json.dump(keys, open(sys.argv[1], 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print(len(keys))
