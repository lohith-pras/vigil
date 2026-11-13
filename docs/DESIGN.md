# Vigil design

The durable visual rules for the panel. Mockups: the "Vigil panel redesign" canvas. Implementation: `src/Vigil/PanelWindow.xaml`.

## Idea

A tray instrument, not a dashboard: readings set like a meter's display, separated by hairlines, on zinc-black glass. No cards inside cards, no uppercase eyebrows, no gradients.

## Material

- **Shell:** double bezel. 1 px ring `#1FFFFFFF`, 4 px of tinted glass `#0AFFFFFF`, then the panel `#CC09090B` (zinc-950 at 80 %).
- **Glass:** Windows 11 acrylic behind the panel (`DWMWA_SYSTEMBACKDROP_TYPE` = transient window). Windows 10: the same layers over black, so solid near-black.
- **Hairlines** `#12FFFFFF` separate sections. Hover wash `#0AFFFFFF`.
- **Hatch** (−45°, 1 px, every 5 px, `#24FFFFFF`) fills the unused part of every bar: headroom, not a grey track.
- **Dot field** for history: 60 columns × 6 dots, unused dots `#10FFFFFF`, newest column cobalt.

## Type

One family for words, one for numbers. Embedded (SIL OFL) from `src/Vigil/Fonts`.

| Use | Face | Size |
|---|---|---|
| Wordmark `vigil` (lowercase, i-dot is the cobalt dot) | Geist SemiBold | 21 |
| Big reading (countdown) | Geist Mono Medium | 40 |
| Readings (CPU %, memory %) | Geist Mono Medium | 22 / 20 |
| Row titles | Geist Medium | 15 |
| Body, options | Geist | 14 / 13 |
| Section headings (sentence case) | Geist Medium, `#A1A1AA` | 13 |
| Help, labels | Geist, `#8B8B94` | 12 |
| Axis, units, small data | Geist Mono, `#8B8B94` | 11 |

Every number is Geist Mono, so values never shift width as they change.

## Colour (restrained)

| Token | Value | Only for |
|---|---|---|
| Text | `#FAFAFA` | primary text, bars at rest |
| SubText | `#A1A1AA` | headings, secondary values |
| Faint | `#8B8B94` | help, labels, axis |
| Accent (cobalt) | `#4C7DFF` | the active tab dot, a switch that's on, the newest reading, the wordmark dot. Nothing else. |
| Good | `#3DD68C` | awake / charging |
| Warn | `#F5B54A` | high (memory ≥ 80 %) |
| Danger | `#F2555A` | low battery, End?, drives > 85 % |

Selected chips are inverted (white fill, black text), not cobalt.

## Controls

- **Tabs:** icon only, no fill; the active one gets the sliding cobalt dot below.
- **Chips:** 34 px pills, 1 px outline at rest, white when selected. Durations in Geist Mono.
- **Switch:** 38 × 22, knob slides, track turns cobalt when on.
- **Segmented control** (sort): two half-pills inside a hairline ring.
- **Row action** (End): 24 px mini pill; armed = red fill, "End?".
- **Footer:** two 36 px pills, Settings and Quit.

## Motion

| What | How |
|---|---|
| Panel open | 6 px rise + fade, 150 ms, `(.23, 1, .32, 1)` |
| Panel close | instant |
| Tab dot | slides 180 ms, `(.77, 0, .175, 1)`; page content swaps instantly |
| Press (every button, chip, tab) | scale .97, 140 ms ease-out |
| Switch | knob 200 ms in-out, track colour 180 ms |
| Readings | update without animation |
| "Show animations in Windows" off | fades only, nothing slides |
