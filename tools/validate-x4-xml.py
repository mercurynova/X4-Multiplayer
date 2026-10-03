#!/usr/bin/env python3
"""Validates X4 mod XML against the XSDs of the unpacked game data (x4-unpacked/, read-only reference).

  python tools/validate-x4-xml.py --x4-unpacked <path> <file-or-folder> [...]

What is checked, by file kind (detected from the root element):
  <mdscript>      libraries/md.xsd  (the same schema the game editor uses; md/md.xsd is identical for the base game)
  <diff>          libraries/diff.xsd (structure of the diff only), and for a diff that targets a known library
                  (factions.xml, colors.xml, ...) the <add> payload is wrapped in the library root and validated
                  against that library's schema, so a wrong attribute on a <faction> is found.
  <language>      well-formed only (text files have no XSD); duplicate page/line ids are reported
  <aiscript>      libraries/aiscripts.xsd
  anything else   well-formed only

Exit code 0 = all valid, 1 = at least one error. Needs lxml (pip install lxml). Expression strings inside attributes
(e.g. "$Obj.hullpercentage") are NOT parsed by any XSD: those are checked against libraries/scriptproperties.xml by
--check-properties (a plain name lookup, catches typos like .hullpercent).
"""
import argparse
import os
import re
import sys

try:
    from lxml import etree
except ImportError:  # pragma: no cover
    print("lxml is required: pip install lxml", file=sys.stderr)
    sys.exit(2)

LIB_FOR_FILE = {
    "factions.xml": ("factions.xsd", "factions"),
    "colors.xml": ("colors.xsd", "colormap"),
    "diplomacy.xml": ("diplomacy.xsd", "diplomacy"),
}


def load_schema(root, name):
    path = os.path.join(root, "libraries", name)
    return etree.XMLSchema(etree.parse(path))


def errors_of(schema, doc, label):
    if schema.validate(doc):
        return []
    return ["%s:%s: %s" % (label, e.line, e.message) for e in schema.error_log]


def check_diff_payload(root, path, doc):
    errs = []
    lib = LIB_FOR_FILE.get(os.path.basename(path).lower())
    if not lib:
        return errs
    xsd, rootname = lib
    schema = load_schema(root, xsd)
    for add in doc.getroot().iter("add"):
        sel = add.get("sel", "")
        if sel.rstrip("/") not in ("/" + rootname, "/" + rootname + "/") and not sel.startswith("/" + rootname + "/"):
            continue
        # wrap the payload: children of <add sel="/factions"> are children of <factions>
        if sel.strip("/") == rootname:
            wrapper = etree.Element(rootname)
            for child in add:
                wrapper.append(etree.fromstring(etree.tostring(child)))
            errs += errors_of(schema, etree.ElementTree(wrapper), path + " (add " + sel + ")")
    return errs


def check_properties(root, path):
    """Name lookup of '.property' tokens used in MD expressions against scriptproperties.xml (typo finder, not a parser)."""
    props = set()
    sp = etree.parse(os.path.join(root, "libraries", "scriptproperties.xml"))
    for p in sp.getroot().iter("property"):
        name = p.get("name", "")
        props.add(re.split(r"[.{]", name)[0])
    text = open(path, encoding="utf-8").read()
    errs = []
    # only expressions inside attribute values: $Var.prop or object.prop chains
    for m in re.finditer(r'"([^"]*)"', text):
        val = m.group(1)
        for t in re.finditer(r"(?:\$\w+|\bplayer|\bevent|\bfaction\.\w+)((?:\.[A-Za-z_]\w*(?:\.\{[^}]*\})?)+)", val):
            first = t.group(1).split(".")[1]
            # properties are lower case; Capitalised segments are MD namespaces / cue names (md.Diplomacy.x), not properties
            if first and first[0].islower() and first not in props and first not in ("count", "isclass", "name"):
                errs.append("%s: unknown property '.%s' in expression %r" % (path, first, val[:100]))
    return errs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--x4-unpacked", required=True)
    ap.add_argument("--check-properties", action="store_true")
    ap.add_argument("paths", nargs="+")
    a = ap.parse_args()
    root = a.x4_unpacked
    md = load_schema(root, "md.xsd")
    diff = load_schema(root, "diff.xsd")
    ai = load_schema(root, "aiscripts.xsd")
    files = []
    for p in a.paths:
        if os.path.isdir(p):
            for d, _, fs in os.walk(p):
                files += [os.path.join(d, f) for f in fs if f.lower().endswith(".xml")]
        else:
            files.append(p)
    bad = 0
    for f in sorted(files):
        try:
            doc = etree.parse(f)
        except etree.XMLSyntaxError as e:
            print("FAIL %s: not well-formed: %s" % (f, e))
            bad += 1
            continue
        tag = doc.getroot().tag
        errs = []
        kind = tag
        if tag == "mdscript":
            errs += errors_of(md, doc, f)
            if a.check_properties:
                errs += check_properties(root, f)
        elif tag == "diff":
            errs += errors_of(diff, doc, f)
            errs += check_diff_payload(root, f, doc)
        elif tag == "aiscript":
            errs += errors_of(ai, doc, f)
        elif tag == "language":
            seen = set()
            for page in doc.getroot().iter("page"):
                for t in page.iter("t"):
                    k = (page.get("id"), t.get("id"))
                    if k in seen:
                        errs.append("%s: duplicate text id %s,%s" % (f, k[0], k[1]))
                    seen.add(k)
        if errs:
            bad += 1
            print("FAIL %s (%s)" % (f, kind))
            for e in errs[:30]:
                print("   " + e)
        else:
            print("ok   %s (%s)" % (f, kind))
    print("%d file(s), %d with errors" % (len(files), bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
