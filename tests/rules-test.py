"""Source rule checks for Spooky Together. No game, no build: reads src/ and the docs.

  python tests/rules-test.py

Checks (from the YokWare-multiplayer-game skill):
  ops.handled        every Op has a handler (HostPacket or ClientPacket)
  ops.sent           every Op is built somewhere (Wire.Packet(Op.X ...)
  ops.host.accepts   every Op a client sends to the host is handled in HostPacket
  statics.reset      every mutable static is cleared by a Reset*/Shutdown/Clear* method, set and cleared
                     in a try/finally, or marked on its declaration: "// process-scoped" (caches, set-up),
                     "// call-scoped" (set and used within one call), or "// lab-only"
  catches.explained  every empty catch says why on the same line (a // comment)
  mutants.listed     every Mutant("x") fix switch in src/ has a tour in scripts/mutants.ps1, and the other way round
  versions.agree     ModVersion matches MODLOG's top entry; Wire.Version matches the newest protocol in MODLOG
Exit code 1 on any failure.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
failures = []


def check(name, ok, detail):
    print(("PASS " if ok else "FAIL ") + name + (": " + detail if detail else ""))
    if not ok:
        failures.append(name)


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


sources = {n: read(os.path.join(SRC, n)) for n in sorted(os.listdir(SRC)) if n.endswith(".cs")}
everything = "\n".join(sources.values())


def method_body(text, signature_regex):
    """Body text of the first method matching the regex (brace matched), or ''."""
    m = re.search(signature_regex, text)
    if not m:
        return ""
    i = text.index("{", m.end() - 1)
    depth, j = 0, i
    while j < len(text):
        if text[j] == "{":
            depth += 1
        elif text[j] == "}":
            depth -= 1
            if depth == 0:
                return text[i:j + 1]
        j += 1
    return text[i:]


# --- Ops -------------------------------------------------------------------------------------------
enum = re.search(r"enum Op\s*:\s*byte\s*\{([^}]*)\}", everything)
ops = [re.sub(r"\s*=.*", "", o).strip() for o in enum.group(1).split(",") if o.strip()]
host = method_body(everything, r"void HostPacket\s*\([^)]*\)\s*\{")
client = method_body(everything, r"void ClientPacket\s*\([^)]*\)\s*\{")
host_ops = set(re.findall(r"Op\.(\w+)", host))
client_ops = set(re.findall(r"case Op\.(\w+)", client))
unhandled = [o for o in ops if o not in host_ops and o not in client_ops]
check("ops.handled", not unhandled, ", ".join(unhandled) or "%d ops, %d host-side, %d client-side" % (len(ops), len(host_ops), len(client_ops)))
built = set(re.findall(r"Wire\.Packet\(Op\.(\w+)", everything))
unsent = [o for o in ops if o not in built]
check("ops.sent", not unsent, ", ".join(unsent) or "every op is built somewhere")
to_host = set()
for m in re.finditer(r"SendToHost(?:Raw)?\(\s*Wire\.Packet\(Op\.(\w+)", everything):
    to_host.add(m.group(1))
missing = sorted(o for o in to_host if o not in host_ops)
check("ops.host.accepts", not missing, ", ".join(missing) or "%d ops clients send: %s" % (len(to_host), ", ".join(sorted(to_host))))

# --- Statics ---------------------------------------------------------------------------------------
decl = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected)\s+)?static\s+(?!class\b|void\b|bool\s+\w+\s*\(|readonly\s+string\[\])(?:readonly\s+)?([\w<>\[\],\. ]+?)\s+(\w+(?:\s*,\s*\w+)*)\s*(=(?!>)[^;]*)?;(.*)$")
reset_bodies = ""
for text in sources.values():
    for m in re.finditer(r"\b(?:void|bool)\s+(\w*(?:Reset|Shutdown|Clear)\w*)\s*\([^)]*\)\s*\{", text):
        reset_bodies += method_body(text[m.start():], r"\{") + "\n"
finally_blocks = "\n".join(re.findall(r"finally\s*\{[^}]*\}", everything))
problems, counted = [], 0
for name, text in sources.items():
    for line in text.splitlines():
        if "const " in line or "(" in line.split("=")[0]:
            continue
        m = decl.match(line)
        if not m:
            continue
        typ = m.group(1)
        if typ.startswith("readonly") or "Color" in typ:
            continue
        comment = m.group(4)
        if "process-scoped" in comment or "lab-only" in comment or "call-scoped" in comment:
            counted += 1
            continue
        for field in [f.strip() for f in m.group(2).split(",")]:
            counted += 1
            if re.search(r"\b" + field + r"\b", reset_bodies) or re.search(r"\b" + field + r"\s*=", finally_blocks):
                continue
            problems.append(name + ": " + field)
check("statics.reset", not problems, "; ".join(problems) or "%d statics covered" % counted)

# --- Empty catches ---------------------------------------------------------------------------------
bare = []
for name, text in sources.items():
    for n, line in enumerate(text.splitlines(), 1):
        if re.search(r"catch\s*(\([^)]*\))?\s*\{\s*(return[^;]*;\s*|continue;\s*)?\}", line) and "//" not in line.split("catch", 1)[1]:
            bare.append("%s:%d" % (name, n))
check("catches.explained", not bare, ", ".join(bare) or "every empty catch says why")

# --- Mutants ---------------------------------------------------------------------------------------
in_src = set(re.findall(r'Mutant(?:Arg)?\("(\w+)"\)', everything))
in_script = set(re.findall(r"^\s*'(\w+)'\s*=\s*'\w+'", read(os.path.join(ROOT, "scripts", "mutants.ps1")), re.M))
check("mutants.listed", in_src == in_script and in_src,
      "src only: %s; mutants.ps1 only: %s" % (sorted(in_src - in_script), sorted(in_script - in_src)) if in_src != in_script
      else "%d fixes can be turned off, each with its tour: %s" % (len(in_src), ", ".join(sorted(in_src))))

# --- Versions --------------------------------------------------------------------------------------
mod_version = re.search(r'ModVersion\s*=\s*"([^"]+)"', everything).group(1)
wire_version = int(re.search(r"const int Version\s*=\s*(\d+)", everything).group(1))
modlog = read(os.path.join(ROOT, "MODLOG.md"))
top = re.search(r"^## (?!Unreleased)(\S+)", modlog, re.M).group(1)   # an "Unreleased" section may sit above the newest version
protocols = re.findall(r"^## .*?protocol (\d+)", modlog, re.M)
newest = int(protocols[0]) if protocols else -1
check("versions.agree", top == mod_version and newest == wire_version,
      "ModVersion %s, MODLOG top %s; Wire.Version %d, MODLOG newest protocol %d" % (mod_version, top, wire_version, newest))

print("RULES: %d failed" % len(failures))
sys.exit(1 if failures else 0)
