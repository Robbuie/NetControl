# The OUI table

`oui.bin` is the IEEE OUI registry, packed. It is embedded in `NetControl.Core` and read by
`OuiDatabase`, which is how a MAC in the request log gets a manufacturer name beside it.

It is a generated file that is committed deliberately. Refreshing it is a chore you run, not
something the build does.

## Refreshing it

```powershell
dotnet run --project tools/NetControl.OuiPacker -- update
dotnet build
dotnet test
```

`update` downloads the four IEEE registry files, packs them, and rewrites `oui.bin`. Commit the
result. If the machine with the repository on it cannot reach IEEE, download the files in a
browser and pack them from a folder instead:

| File | URL | |
|---|---|---|
| `oui.csv` | <https://standards-oui.ieee.org/oui/oui.csv> | MA-L, 24-bit. Required. |
| `mam.csv` | <https://standards-oui.ieee.org/oui28/mam.csv> | MA-M, 28-bit |
| `oui36.csv` | <https://standards-oui.ieee.org/oui36/oui36.csv> | MA-S, 36-bit |
| `iab.csv` | <https://standards-oui.ieee.org/iab/iab.csv> | IAB, 36-bit, retired but still in service |

```powershell
dotnet run --project tools/NetControl.OuiPacker -- pack --from C:\temp\ieee
```

To see what is actually in a packed file:

```powershell
dotnet run --project tools/NetControl.OuiPacker -- dump --mac 00:1D:9C:C7:B0:70
dotnet run --project tools/NetControl.OuiPacker -- dump --take 100
```

## Why all four files

IEEE sells three sizes of block. MA-L is the classic 24-bit OUI. MA-M (28-bit) and MA-S (36-bit)
are smaller blocks for companies that do not need sixteen million addresses, and they are carved
out of 24-bit ranges that `oui.csv` lists as belonging to *IEEE Registration Authority*. IAB is
MA-S's retired predecessor - closed to new assignments, still present in equipment that is still
in panels.

So a 24-bit-only table does not merely miss small vendors, it confidently names them
"IEEE Registration Authority". That is why `OuiDatabase` searches 36 bits first, then 28, then 24,
and stops at the first hit.

## What is in it

As of the 2026-08-07 refresh: 58,134 assignments in 604 KB, being 39,902 MA-L, 6,533 MA-M and
11,699 at 36 bits (MA-S plus IAB). Three duplicate prefixes in the IEEE files were collapsed.

Note the shape of that: nearly a third of all assignments are narrower than 24 bits. A
MA-L-only table would not merely have missed those 18,232 blocks, it would have answered
"IEEE Registration Authority" for most of them.

`dump` prints the same summary for whatever is currently packed.

## What ships before the first refresh

A clone that has never been refreshed carries a seed table packed from
`tools/NetControl.OuiPacker/seed/seed-oui.csv` - about twenty industrial MA-L prefixes, enough
that the code path and the UI work. Its source date is 1970-01-01, the deliberate signal that
nobody has run a real refresh yet. `update` replaces it entirely, and `pack --seed` puts it back.

Do not grow the seed by hand. If a vendor is missing, run the update.

## The format

Described in full in `OuiPackFormat.cs`. Briefly: a Deflate stream containing a 32-byte header,
three tables sorted by prefix, and a blob of shared UTF-8 vendor names. Lookups binary search the
tables in place and only build a string when there is a match, because the request log calls this
once per arriving packet and a power-cycled device retransmits hard.

`OuiPackWriter` sits next to `OuiDatabase` rather than in the tool so that the format is described
once and the tests can round-trip a table built in memory.

## Licensing

The IEEE registry is published for public use. Nothing here is derived from the Rockwell tool.
