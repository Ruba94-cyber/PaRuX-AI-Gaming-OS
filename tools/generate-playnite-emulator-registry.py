#!/usr/bin/env python3
"""
Generates src/PaRuX.Server/Orchestration/Gaming/Emulators/EmulatorRegistry.Playnite.cs from Playnite's emulator definitions.

Playnite (https://github.com/JosefNemec/Playnite) is MIT licensed; its emulator definitions (Emulation/Emulators/*/emulator.yaml and
Platforms.yaml) are reused here with attribution (see THIRD_PARTY_NOTICES.md). Only definitions that map cleanly to PaRuX's argument-list
launch model are kept: no scripts, no unknown placeholders, an executable name that is a plain file name. Everything is marked
"unverified": a definition here has not been tested against an installed emulator by PaRuX.

Usage (any Playnite 10 install or a checkout of its source tree):
    python tools/generate-playnite-emulator-registry.py "<playnite folder>/Emulation" > EmulatorRegistry.Playnite.cs
"""
import os
import re
import sys

import yaml

# Hand-tuned PaRuX definitions win over generated ones with the same id.
HAND_TUNED = {"eden", "eden-cli", "dolphin", "cemu", "pcsx2", "rpcs3", "duckstation", "ppsspp", "retroarch", "mame"}
PLACEHOLDERS = {"{ImagePath}": "{rom}", "{ImageNameNoExt}": "{romname}", "{ImageDir}": "{romdir}"}


def tokenize(arguments):
    """Splits a Windows-style argument string, honouring double quotes (which are removed)."""
    tokens, current, in_quote, started = [], [], False, False
    for ch in arguments:
        if ch == '"':
            in_quote = not in_quote
            started = True
        elif ch.isspace() and not in_quote:
            if started:
                tokens.append("".join(current))
                current, started = [], False
        else:
            current.append(ch)
            started = True
    if in_quote:
        return None
    if started:
        tokens.append("".join(current))
    return tokens


def translate(arguments):
    tokens = tokenize(arguments or "")
    if not tokens:
        return None
    out = []
    for token in tokens:
        for source, target in PLACEHOLDERS.items():
            token = token.replace(source, target)
        if re.search(r"\{[A-Za-z]", token.replace("{rom}", "").replace("{romname}", "").replace("{romdir}", "")):
            return None  # an unsupported placeholder such as {EmulatorDir}
        out.append(token)
    if not any("{rom}" in t or "{romname}" in t for t in out):
        return None
    return tuple(out)


def executable_names(pattern):
    """Plain executable file names out of the simple regexes Playnite uses; None when the pattern is anything else."""
    if not pattern:
        return None
    body = pattern.strip()
    if body.startswith("^"):
        body = body[1:]
    if body.endswith("$"):
        body = body[:-1]
    names = []
    group = re.fullmatch(r"\(([A-Za-z0-9_\-|\\.]+)\)(\\\.exe)", body)
    if group:
        names = [alt.replace("\\.", ".") + ".exe" for alt in group.group(1).split("|")]
    else:
        match = re.fullmatch(r"([A-Za-z0-9_\-]+(?:\\\.[A-Za-z0-9_\-]+)*)\\\.exe", body)
        if match:
            names = [match.group(1).replace("\\.", ".") + ".exe"]
    names = [n for n in names if re.fullmatch(r"[A-Za-z0-9_\-.]+\.exe", n)]
    return names or None


def cs(value):
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def array(items):
    return "new[] { " + ", ".join(cs(i) for i in items) + " }"


def main(root):
    with open(os.path.join(root, "Platforms.yaml"), encoding="utf-8-sig") as handle:
        platform_names = {p["Id"]: p["Name"] for p in yaml.safe_load(handle)}
    entries = []
    skipped = []
    for folder in sorted(os.listdir(os.path.join(root, "Emulators")), key=str.lower):
        path = os.path.join(root, "Emulators", folder, "emulator.yaml")
        if not os.path.isfile(path):
            continue
        with open(path, encoding="utf-8-sig") as handle:
            data = yaml.safe_load(handle)
        emu_id = str(data.get("Id", "")).lower()
        if not re.fullmatch(r"[a-z0-9\-]+", emu_id) or emu_id in HAND_TUNED:
            skipped.append((folder, "hand-tuned or odd id"))
            continue
        groups = {}
        for profile in data.get("Profiles") or []:
            if any(key in profile for key in ("StartupScript", "PreScript", "PostScript")):
                continue
            template = translate(profile.get("StartupArguments"))
            names = executable_names(profile.get("StartupExecutable"))
            if template is None or names is None:
                continue
            key = (template, tuple(names))
            platforms, extensions = groups.setdefault(key, (set(), set()))
            platforms.update(profile.get("Platforms") or [])
            extensions.update("." + str(e).lower().lstrip(".") for e in (profile.get("ImageExtensions") or []))
        if not groups:
            skipped.append((folder, "no translatable profile"))
            continue
        for index, ((template, names), (platforms, extensions)) in enumerate(groups.items()):
            if not extensions or any(not re.fullmatch(r"\.[a-z0-9_+\-]+", e) for e in extensions):
                continue
            labels = [platform_names.get(p, p) for p in sorted(platforms)]
            platform = " / ".join(labels[:3]) + (f" +{len(labels) - 3}" if len(labels) > 3 else "")
            entry_id = emu_id if index == 0 else f"{emu_id}-{index + 1}"
            name = data["Name"] if len(groups) == 1 else f"{data['Name']} ({platform})"
            entries.append((entry_id, name, platform or "Various", list(names), sorted(extensions), list(template)))
    lines = [
        "// <auto-generated>",
        "// Generated by tools/generate-playnite-emulator-registry.py from Playnite's emulator definitions",
        "// (Playnite, MIT License, https://github.com/JosefNemec/Playnite - see THIRD_PARTY_NOTICES.md). Do not edit by hand.",
        "// Every entry is 'unverified': it has not been tested here against an installed emulator.",
        "// </auto-generated>",
        "namespace PaRuX.Server.Orchestration.Gaming.Emulators;",
        "",
        "internal static class PlayniteEmulatorDefinitions",
        "{",
        "    private static readonly string[] None = System.Array.Empty<string>();",
        "",
        "    public static readonly System.Collections.Generic.IReadOnlyList<EmulatorDefinition> All = new EmulatorDefinition[]",
        "    {",
    ]
    for emu_id, name, platform, names, extensions, template in entries:
        lines.append(f"        new({cs(emu_id)}, {cs(name)}, {cs(platform)}, {array(names)},")
        lines.append(f"            {array(extensions)}, None,")
        lines.append(f"            {array(template)}, null, false),")
    lines += ["    };", "}", ""]
    sys.stdout.write("\n".join(lines))
    sys.stderr.write(f"{len(entries)} definitions generated; {len(skipped)} emulators skipped\n")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
