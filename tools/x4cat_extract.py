#!/usr/bin/env python3
"""Extract files from X4 .cat/.dat archives.

Each NN.cat is a text index of "path size mtime md5" lines; file bytes are
stored back-to-back in the matching NN.dat in the same order. Later catalogs
override earlier ones. *_sig.cat files are signatures and are skipped.

Usage: x4cat_extract.py <game_dir> <out_dir> [regex ...]
"""
import os, re, sys, glob

def main():
    game, out = sys.argv[1], sys.argv[2]
    pats = [re.compile(p) for p in sys.argv[3:]] or [re.compile(".")]
    roots = [game] + sorted(glob.glob(os.path.join(game, "extensions", "ego_dlc_*")))
    n = 0
    for root in roots:
        prefix = "" if root == game else os.path.relpath(root, game).replace("\\", "/") + "/"
        for cat in sorted(glob.glob(os.path.join(root, "*.cat"))):
            if cat.endswith("_sig.cat"):
                continue
            dat = cat[:-4] + ".dat"
            off = 0
            with open(cat, encoding="utf-8", errors="replace") as c, open(dat, "rb") as d:
                for line in c:
                    parts = line.rstrip("\n").rsplit(" ", 3)
                    if len(parts) != 4:
                        continue
                    path, size = parts[0], int(parts[1])
                    full = prefix + path
                    if any(p.search(full) for p in pats):
                        d.seek(off)
                        dest = os.path.join(out, full)
                        os.makedirs(os.path.dirname(dest), exist_ok=True)
                        with open(dest, "wb") as f:
                            f.write(d.read(size))
                        n += 1
                    off += size
    print(f"extracted {n} files to {out}")

if __name__ == "__main__":
    main()
