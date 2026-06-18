# Version Notes

This project was copied into this folder before Git was initialized, so older source history is not available here.

From the first Git commit onward, source changes can be rolled back with Git commits/tags. Earlier V4 builds are preserved as compiled DLL snapshots when available.

## Important DLL Snapshots

| Version | Local snapshot | Notes |
| --- | --- | --- |
| V4.122 | `releases/CodexAutoCADBridgeV4_122.dll` | Earlier quality baseline. Log showed best area utilization around 75.43%. |
| V4.129 | `releases/CodexAutoCADBridgeV4_129.dll` | Faster baseline after lazy real-polygon caching. Log showed about 5.45s average per individual and best around 71.90%. |
| V4.130 | `releases/CodexAutoCADBridgeV4_130.dll` | Current source snapshot at the time Git was initialized. Adds top-band hole candidates. |
| V4.132 | `releases/CodexAutoCADBridgeV4_132.dll` | Selective polish experiment: 10 generations, coarse candidate scoring first, NFP/slide polish only after a placement is selected. Test result reached about 73.00% with lower time cost. |

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputName CodexAutoCADBridgeV4_130.dll
```

After loading a DLL in AutoCAD, run `CDX_VERSION` to confirm the loaded version.

## External References

`external/DeepNestPort` was copied locally as reference material while exploring NFP ideas. It is not part of the main plugin build and is intentionally ignored by this Git repository for now.

## Rollback Basics

Use tags for stable points:

```powershell
git tag
git checkout v4.130
```

To return to normal development after checking an old tag:

```powershell
git checkout main
```
