# Releasing Phone Use

A release is a `v*` tag on `master`. The tag builds the Windows app in CI; the Mac DMG is built and notarized on
a Mac with the Developer ID certificate, then added by hand. The release stays a draft until both are on it, and
the Windows app's updater only offers published releases, so nobody is offered a half-finished one.

## 1. Bump the version on master

Set the new version in:

- `pyproject.toml` (`version = "…"`)
- `phoneuse/app.py` (the About dialog's `version=`)
- `mac/project.yml`
- `packaging/flatpak/co.lxe.PhoneUse.metainfo.xml` (a `<release>` entry)

Commit ("Phone Use X.Y.Z"), push to `master`, and wait for the **windows app** and **mac core on macOS 15**
workflows to pass on that commit:

```sh
gh run list -R lxe/phoneuse -b master -L 4
```

Fix anything they find *before* tagging. A fix that lands after the tag means moving the tag (step 5).

## 2. Tag once

```sh
git fetch origin
git tag -a vX.Y.Z -m "Phone Use X.Y.Z" origin/master
git push origin vX.Y.Z
```

The tag push runs **windows app**, which builds `PhoneUse-X.Y.Z-win-x64-setup.exe` and the portable zip, creates
the release as a **draft** if there isn't one, and uploads both. Watch it:

```sh
gh run list -R lxe/phoneuse -b vX.Y.Z
gh release view vX.Y.Z -R lxe/phoneuse   # draft: true, two Windows assets
```

## 3. Add the Mac DMG

On the Mac with the Developer ID certificate and the `phoneuse-notary` notarytool profile, from the same commit:

```sh
git checkout vX.Y.Z
mac/scripts/package.sh --fresh
cp mac/build/release/PhoneUse.dmg PhoneUse-X.Y.Z-mac.dmg
gh release upload vX.Y.Z PhoneUse-X.Y.Z-mac.dmg -R lxe/phoneuse --clobber
```

`package.sh` ends with `spctl --assess`; it should say *accepted, source=Notarized Developer ID*.

## 4. Publish

Write the notes, then publish. This makes it the Latest release, which the README's Download link and the
Windows updater both follow:

```sh
gh release edit vX.Y.Z -R lxe/phoneuse --draft=false --latest --notes-file notes.md
```

## 5. If the tag has to move

Avoid it: every tag push rebuilds Windows and re-uploads over the draft. When a fix must go into the release
before it's published, land it on `master`, then move the tag and let the Windows build run again, and rebuild
the DMG from the new commit (step 3):

```sh
git tag -fa vX.Y.Z -m "Phone Use X.Y.Z" origin/master
git push -f origin vX.Y.Z
```

Once a release is published, don't move its tag; release X.Y.Z+1 instead.
