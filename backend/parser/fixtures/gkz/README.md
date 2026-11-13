# GKZ schedule regression fixtures

Reduced HTML fragments from three real event cards downloaded on 2026-11-13.
They retain original titles, visible date/time blocks and JSON-LD start/end dates.
Calendar links retain original `dates` and `ctz` values; unrelated parameters,
scripts and page decoration were removed.

Each filename is the event slug at `https://gkzbashkortostan.ru/events/{slug}/`.

| Card | Visible local start | Calendar start in Asia/Yekaterinburg |
| --- | --- | --- |
| Alexander Malinin | 2026-11-13 19:00 | 2026-11-13 21:00 |
| Fridays of Abel | 2026-11-14 18:00 | 2026-11-14 20:00 |
| Diva | 2026-11-15 17:00 | 2026-11-15 19:00 |

JSON-LD contains dates without times. The parser prefers the visible schedule
but reports the calendar disagreement and requires review. These fixtures do
not establish which time is ultimately correct and are not seed catalog data.
Calendar-only fallback times also require review because the source's calendar
export has demonstrated discrepancies.

Run the offline regression tests from the repository root:

```powershell
dotnet test backend/parser/parser.slnx
```
