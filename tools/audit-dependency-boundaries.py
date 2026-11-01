#!/usr/bin/env python3
"""Offline, read-only dependency-boundary check for PaRuX Windows projects.

Uses Python's standard library only. It never restores packages, downloads code,
executes third-party binaries, scans user paths or writes an automatic lock file.
Update the reviewed JSON manifest explicitly after a dependency/license audit.
"""
from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def read_packages(project: Path) -> dict[str, str]:
    tree = ET.parse(project)
    packages: dict[str, str] = {}
    for item in tree.iter():
        if item.tag.rsplit("}", 1)[-1] != "PackageReference":
            continue
        name = item.attrib.get("Include", "").strip()
        version = (item.attrib.get("Version") or
                   next((c.text for c in item
                         if c.tag.rsplit("}", 1)[-1] == "Version"), "") or "").strip()
        if not name or not version or name in packages or "$(" in version:
            raise ValueError(f"Invalid, duplicate or unpinned NuGet reference in {project.name}")
        packages[name] = version
    return packages


def audit(root: Path) -> tuple[dict, list[str]]:
    manifest_file = root / "packaging" / "dependency-boundaries.json"
    with manifest_file.open("r", encoding="utf-8") as source:
        manifest = json.load(source)

    errors: list[str] = []
    if manifest.get("schema_version") != 1:
        errors.append("Unsupported dependency manifest schema")
    expected_projects = manifest.get("projects")
    if not isinstance(expected_projects, dict) or not expected_projects:
        errors.append("Missing reviewed project inventory")
        expected_projects = {}

    inventory: dict[str, dict[str, str]] = {}
    for relative_path, expected in sorted(expected_projects.items()):
        path = root / relative_path
        if not path.is_file():
            errors.append(f"Missing reviewed project: {relative_path}")
            continue
        if not isinstance(expected, dict):
            errors.append(f"Invalid reviewed package list: {relative_path}")
            continue
        try:
            actual = read_packages(path)
        except (OSError, ET.ParseError, ValueError) as exc:
            errors.append(f"Cannot audit {relative_path}: {type(exc).__name__}")
            continue
        inventory[relative_path] = actual
        for package in sorted(set(actual) - set(expected)):
            errors.append(f"NEW unreviewed dependency in {relative_path}: {package}")
        for package in sorted(set(expected) - set(actual)):
            errors.append(f"REMOVED dependency requires baseline review in {relative_path}: {package}")
        for package in sorted(set(expected) & set(actual)):
            if expected[package] != actual[package]:
                errors.append(f"Version changed without review in {relative_path}: {package}")

    licenses = manifest.get("vendored_license_files", [])
    if not isinstance(licenses, list):
        errors.append("Invalid vendored license inventory")
    else:
        for license_path in licenses:
            if (not isinstance(license_path, str) or
                    license_path.startswith(("/", "\\")) or
                    ".." in Path(license_path).parts):
                errors.append("Unsafe vendored license path in dependency manifest")
                continue
            if not (root / license_path).is_file():
                errors.append(f"Missing upstream license: {license_path}")

    all_packages = sorted({package for project in inventory.values() for package in project})
    report = {
        "reviewed_projects": len(expected_projects),
        "found_projects": len(inventory),
        "direct_references": sum(len(packages) for packages in inventory.values()),
        "distinct_packages": len(all_packages),
        "dependency_names": all_packages,
        "status": "PASS" if not errors else "FAIL",
        "errors": errors,
    }
    return report, errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1],
                        help="Repository checkout path, default: location of this script")
    parser.add_argument("--json", action="store_true", help="Machine-readable summary")
    args = parser.parse_args()

    try:
        report, errors = audit(args.root.resolve())
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        report = {"status": "FAIL", "errors": [f"Could not load reviewed manifest: {type(exc).__name__}"]}
        errors = report["errors"]

    if args.json:
        print(json.dumps(report, indent=2, sort_keys=True))
    else:
        print(f"[dependencies] {report['status']}: " +
              f"{report.get('direct_references', 0)} direct references; " +
              f"{report.get('distinct_packages', 0)} distinct packages")
        for issue in errors:
            print("[dependencies] " + issue, file=sys.stderr)
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
